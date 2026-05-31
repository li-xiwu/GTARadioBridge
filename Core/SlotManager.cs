using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using GTARadioBridge.Models;

namespace GTARadioBridge.Core;

public class SlotManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture;
    private readonly GTAMonitor _monitor;
    private readonly NowPlayingWatcher _nowPlaying;

    private readonly string[] _slotPaths;
    private readonly SlotState[] _slotStates;
    private readonly NowPlayingWatcher.TrackInfo?[] _slotTracks;

    private int _captureSlot = 0;
    private int _playingSlot = -1;
    private bool _running;

    private Timer? _monitorTimer;
    private readonly object _stateLock = new();

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<NowPlayingWatcher.TrackInfo?>? NowPlayingChanged;

    public enum SlotState { Empty, Capturing, Ready, Playing, Spent }

    public record BridgeStatus(
        bool IsRunning,
        int CaptureSlot,
        int PlayingSlot,
        SlotState[] States,
        TimeSpan CapturedSoFar,
        NowPlayingWatcher.TrackInfo? CurrentTrack);

    public SlotManager(AppSettings settings)
    {
        _settings = settings;
        _capture = new AudioCaptureService();
        _monitor = new GTAMonitor(settings.UserMusicPath);
        _nowPlaying = new NowPlayingWatcher();

        _slotPaths = Enumerable.Range(0, settings.SlotCount)
            .Select(i => Path.Combine(settings.UserMusicPath, $"gbridge_slot_{i:D2}.mp3"))
            .ToArray();

        _slotStates = new SlotState[settings.SlotCount];
        _slotTracks = new NowPlayingWatcher.TrackInfo[settings.SlotCount];

        _capture.OnError += msg => Debug.WriteLine($"[Capture] {msg}");
        _capture.OnSilenceDetected += OnSilenceDetected;
        _monitor.SlotStartedPlaying += OnSlotStartedPlaying;
        _nowPlaying.TrackChanged += OnTrackChanged;
    }

    public async Task StartAsync()
    {
        lock (_stateLock)
        {
            if (_running) return;
            _running = true;
        }

        await _nowPlaying.InitializeAsync();
        _monitor.Start();

        EnsureUserMusicPathExists();
        BeginCaptureIntoSlot(0);

        _monitorTimer = new Timer(OnMonitorTick, null, 500, 500);
        FireStatus();
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
        }

        _monitorTimer?.Dispose();
        _monitorTimer = null;

        if (_capture.IsCapturing)
            _ = FinaliseCurrentCaptureAsync(_captureSlot);

        _monitor.Stop();
        FireStatus();
    }

    private void BeginCaptureIntoSlot(int slot)
    {
        if (slot >= _settings.SlotCount) slot = 0;

        _captureSlot = slot;
        _slotStates[slot] = SlotState.Capturing;
        _slotTracks[slot] = _nowPlaying.CurrentTrack;

        _capture.Start(_settings.BitRate, _settings.CaptureDeviceId);
        Debug.WriteLine($"[SlotManager] Capturing into slot {slot}");
        FireStatus();
    }

    private async Task FinaliseCurrentCaptureAsync(int slot)
    {
        var mp3Data = _capture.StopAndGetMp3();

        if (mp3Data.Length == 0)
        {
            _slotStates[slot] = SlotState.Empty;
            FireStatus();
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(_slotPaths[slot], mp3Data);
            _slotStates[slot] = SlotState.Ready;
            Debug.WriteLine($"[SlotManager] Slot {slot} ready ({mp3Data.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SlotManager] Failed to write slot {slot}: {ex.Message}");
            _slotStates[slot] = SlotState.Empty;
        }

        FireStatus();
    }

    private void OnMonitorTick(object? _)
    {
        lock (_stateLock)
        {
            if (!_running) return;

            var captured = _capture.GetCapturedDuration(_settings.BitRate);
            bool shouldRotate = captured.TotalSeconds >= 30;

            if (!shouldRotate && _playingSlot >= 0)
            {
                var playingInfo = new FileInfo(_slotPaths[_playingSlot]);
                if (playingInfo.Exists)
                {
                    double totalSecs = playingInfo.Length / (_settings.BitRate * 1000.0 / 8.0);
                    shouldRotate = captured.TotalSeconds >= Math.Max(20, totalSecs * 0.7);
                }
            }

            if (shouldRotate && _slotStates[_captureSlot] == SlotState.Capturing)
            {
                int currentSlot = _captureSlot;
                int nextSlot = (currentSlot + 1) % _settings.SlotCount;

                if (_slotStates[nextSlot] != SlotState.Playing)
                    _ = RotateSlotAsync(currentSlot, nextSlot);
            }

            FireStatus();
        }
    }

    private async Task RotateSlotAsync(int finishedSlot, int nextSlot)
    {
        await FinaliseCurrentCaptureAsync(finishedSlot);
        BeginCaptureIntoSlot(nextSlot);
    }

    private void OnSlotStartedPlaying(string slotFileName)
    {
        lock (_stateLock)
        {
            for (int i = 0; i < _slotPaths.Length; i++)
            {
                if (string.Equals(Path.GetFileName(_slotPaths[i]), slotFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (_playingSlot >= 0 && _playingSlot != i)
                        _slotStates[_playingSlot] = SlotState.Spent;

                    _playingSlot = i;
                    if (_slotStates[i] != SlotState.Capturing)
                        _slotStates[i] = SlotState.Playing;

                    NowPlayingChanged?.Invoke(_slotTracks[i]);
                    FireStatus();
                    return;
                }
            }
        }
    }

    private void OnTrackChanged(NowPlayingWatcher.TrackInfo track)
    {
        lock (_stateLock) { _slotTracks[_captureSlot] = track; }
        FireStatus();
    }

    private void OnSilenceDetected()
    {
        Debug.WriteLine("[SlotManager] Silence detected");
    }

    private void EnsureUserMusicPathExists()
    {
        try { Directory.CreateDirectory(_settings.UserMusicPath); }
        catch (Exception ex) { Debug.WriteLine($"[SlotManager] Cannot create dir: {ex.Message}"); }
    }

    private void FireStatus()
    {
        var captured = _running ? _capture.GetCapturedDuration(_settings.BitRate) : TimeSpan.Zero;
        StatusChanged?.Invoke(new BridgeStatus(
            _running, _captureSlot, _playingSlot,
            (SlotState[])_slotStates.Clone(),
            captured, _nowPlaying.CurrentTrack));
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _monitor.Dispose();
        _nowPlaying.Dispose();
    }
}