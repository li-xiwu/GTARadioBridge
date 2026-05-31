using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System.Diagnostics;

namespace GTARadioBridge.Core;

/// <summary>
/// Captures system audio output via WASAPI loopback and encodes to MP3.
/// The capture runs in a background thread; consumers call StopAndGetMp3() 
/// to finalise the current recording into a byte array.
/// </summary>
public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MemoryStream? _mp3Stream;
    private LameMP3FileWriter? _mp3Writer;
    private readonly object _lock = new();
    private bool _isCapturing;

    public event Action<string>? OnError;
    public event Action? OnSilenceDetected;

    // RMS silence detection
    private float _lastRms;
    private DateTime _silenceStart = DateTime.MaxValue;
    private const float SilenceThreshold = 0.002f;
    private const double SilenceDurationMs = 800;

    public bool IsCapturing => _isCapturing;

    /// <summary>Start capturing the default audio output device.</summary>
    public void Start(int bitRate = 192)
    {
        lock (_lock)
        {
            if (_isCapturing) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                _mp3Stream = new MemoryStream();

                var format = new Mp3Standard(
                    _capture.WaveFormat.SampleRate,
                    _capture.WaveFormat.Channels == 1
                        ? LAMEPreset.STANDARD_FAST
                        : LAMEPreset.STANDARD_FAST,
                    bitRate);

                _mp3Writer = new LameMP3FileWriter(
                    _mp3Stream,
                    _capture.WaveFormat,
                    bitRate);

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

    /// <summary>
    /// Stop capturing and return all encoded MP3 bytes recorded so far.
    /// Returns empty array if nothing was captured.
    /// </summary>
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

    /// <summary>
    /// Snapshot the current buffer without stopping — used to check
    /// how much audio has been captured so far.
    /// </summary>
    public int GetCapturedByteCount()
    {
        lock (_lock)
        {
            return (int)(_mp3Stream?.Length ?? 0);
        }
    }

    public TimeSpan GetCapturedDuration(int bitRate = 192)
    {
        var bytes = GetCapturedByteCount();
        if (bytes == 0) return TimeSpan.Zero;
        double seconds = bytes / (bitRate * 1000.0 / 8.0);
        return TimeSpan.FromSeconds(seconds);
    }

    // ── Private ──────────────────────────────────────────────────────────────

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
            catch (Exception ex)
            {
                OnError?.Invoke($"Write error: {ex.Message}");
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            OnError?.Invoke($"Recording stopped with error: {e.Exception.Message}");
    }

    private void CheckSilence(byte[] buffer, int bytesRecorded)
    {
        // Calculate RMS from 16-bit samples
        float sumSq = 0;
        int sampleCount = bytesRecorded / 2;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            float normalized = sample / 32768f;
            sumSq += normalized * normalized;
        }
        _lastRms = sampleCount > 0 ? MathF.Sqrt(sumSq / sampleCount) : 0;

        if (_lastRms < SilenceThreshold)
        {
            if (_silenceStart == DateTime.MaxValue)
                _silenceStart = DateTime.Now;
            else if ((DateTime.Now - _silenceStart).TotalMilliseconds > SilenceDurationMs)
            {
                OnSilenceDetected?.Invoke();
                _silenceStart = DateTime.MaxValue; // reset so it fires again after next non-silence
            }
        }
        else
        {
            _silenceStart = DateTime.MaxValue;
        }
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
        _mp3Writer?.Dispose();
        _mp3Writer = null;
        _mp3Stream?.Dispose();
        _mp3Stream = null;
    }

    public void Dispose() => Cleanup();
}

// Helper: holds MP3 standard parameters — not actually used by LameMP3FileWriter
// but kept for documentation purposes
file sealed class Mp3Standard
{
    public int SampleRate { get; }
    public LAMEPreset Preset { get; }
    public int BitRate { get; }
    public Mp3Standard(int sampleRate, LAMEPreset preset, int bitRate)
        => (SampleRate, Preset, BitRate) = (sampleRate, preset, bitRate);
}
