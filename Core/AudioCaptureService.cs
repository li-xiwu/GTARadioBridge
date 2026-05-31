using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace GTARadioBridge.Core;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MemoryStream? _mp3Stream;
    private LameMP3FileWriter? _mp3Writer;
    private readonly object _lock = new();
    private bool _isCapturing;

    public event Action<string>? OnError;
    public event Action? OnSilenceDetected;

    private DateTime _silenceStart = DateTime.MaxValue;
    private const float SilenceThreshold = 0.002f;
    private const double SilenceDurationMs = 800;

    public bool IsCapturing => _isCapturing;

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var result = new List<(string, string)>();
        result.Add(("default", "Default (system audio)"));
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
                result.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return result;
    }

    public void Start(int bitRate = 192, string deviceId = "default")
    {
        lock (_lock)
        {
            if (_isCapturing) return;
            try
            {
                if (deviceId == "default")
                {
                    _capture = new WasapiLoopbackCapture();
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    MMDevice? device = null;
                    try { device = enumerator.GetDevice(deviceId); }
                    catch
                    {
                        OnError?.Invoke($"Device '{deviceId}' not found, falling back to default.");
                        _capture = new WasapiLoopbackCapture();
                    }
                    if (device != null)
                        _capture = new WasapiLoopbackCapture(device);
                }

                _mp3Stream = new MemoryStream();
                _mp3Writer = new LameMP3FileWriter(_mp3Stream, _capture!.WaveFormat, bitRate);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isCapturing = true;
                Debug.WriteLine("[AudioCapture] Started");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to start capture: {ex.Message}");
                Cleanup();
            }
        }
    }

    public byte[] StopAndGetMp3()
    {
        lock (_lock)
        {
            if (!_isCapturing) return Array.Empty<byte>();
            try
            {
                _capture?.StopRecording();
                _mp3Writer?.Flush();
                var data = _mp3Stream?.ToArray() ?? Array.Empty<byte>();
                Cleanup();
                return data;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Error stopping capture: {ex.Message}");
                Cleanup();
                return Array.Empty<byte>();
            }
        }
    }

    public int GetCapturedByteCount()
    {
        lock (_lock) { return (int)(_mp3Stream?.Length ?? 0); }
    }

    public TimeSpan GetCapturedDuration(int bitRate = 192)
    {
        var bytes = GetCapturedByteCount();
        if (bytes == 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(bytes / (bitRate * 1000.0 / 8.0));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_mp3Writer == null || e.BytesRecorded == 0) return;
            try
            {
                _mp3Writer.Write(e.Buffer, 0, e.BytesRecorded);
                CheckSilence(e.Buffer, e.BytesRecorded);
            }
            catch (Exception ex) { OnError?.Invoke($"Write error: {ex.Message}"); }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            OnError?.Invoke($"Recording stopped with error: {e.Exception.Message}");
    }

    private void CheckSilence(byte[] buffer, int bytesRecorded)
    {
        float sumSq = 0;
        int sampleCount = bytesRecorded / 2;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            float normalized = sample / 32768f;
            sumSq += normalized * normalized;
        }
        float rms = sampleCount > 0 ? MathF.Sqrt(sumSq / sampleCount) : 0;

        if (rms < SilenceThreshold)
        {
            if (_silenceStart == DateTime.MaxValue) _silenceStart = DateTime.Now;
            else if ((DateTime.Now - _silenceStart).TotalMilliseconds > SilenceDurationMs)
            {
                OnSilenceDetected?.Invoke();
                _silenceStart = DateTime.MaxValue;
            }
        }
        else { _silenceStart = DateTime.MaxValue; }
    }

    private void Cleanup()
    {
        _isCapturing = false;
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _mp3Writer?.Dispose(); _mp3Writer = null;
        _mp3Stream?.Dispose(); _mp3Stream = null;
    }

    public void Dispose() => Cleanup();
}