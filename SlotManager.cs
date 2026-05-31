using GTARadioBridge.Models;
using System.Diagnostics;

namespace GTARadioBridge.Core;

/// <summary>
/// Central state machine.
///
/// Lifecycle:
///   1. On Start(), begin capturing into slot 0.
///   2. ETW tells us GTA started reading slot N  → mark N as Playing.
///   3. When remaining time on slot N &lt; PreloadThresholdSeconds,
///      stop current capture, save to slot N, start capturing into slot N+1.
///   4. When GTA skips (cuts to slot N+1 before N finishes) → handled naturally.
///   5. Native GTA skip key ( = ) causes GTA to jump to the next slot file in
///      alphabetical order — we keep slots named gbridge_slot_00 … gbridge_slot_03
///      so the ordering is predictable.
/// </summary>
public class SlotManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture;
    private readonly GTAMonitor _monitor;
    private readonly NowPlayingWatcher _nowPlaying;

    private readonly string[] _slotPaths;
    private readonly SlotState[] _slotStates;
    private readonly NowPlayingWatcher.TrackInfo?[] _slotTracks;

    private int _captureSlot = 0;   // slot currently being written
    private int _playingSlot = -1;  // slot GTA is currently reading
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

        // Wire events
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

        // Poll every 500 ms to check if we should start preparing the next slot
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

    // ── Core logic ────────────────────────────────────────────────────────────

    private void BeginCaptureIntoSlot(int slot)
    {
        if (slot >= _settings.SlotCount)
        {
            Debug.WriteLine("[SlotManager] All slots filled, wrapping to 0");
            slot = 0;
        }

        _captureSlot = slot;
        _slotStates[slot] = SlotState.Capturing;
        _slotTracks[slot] = _nowPlaying.CurrentTrack;

        _capture.Start(_settings.BitRate);
        Debug.WriteLine($"[SlotManager] Capturing into slot {slot}");
        FireStatus();
    }

    private async Task FinaliseCurrentCaptureAsync(int slot)
    {
        var mp3Data = _capture.StopAndGetMp3();

        if (mp3Data.Length == 0)
        {
            Debug.WriteLine($"[SlotManager] Slot {slot}: no data captured");
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

            // How long have we been capturing the current slot?
            var captured = _capture.GetCapturedDuration(_settings.BitRate);

            // If the slot GTA is playing will end soon, pre-finalise current capture
            // and start filling the next slot early.
            // Without ETW data yet (_playingSlot == -1), use captured duration as proxy.
            bool shouldRotate = captured.TotalSeconds >= 30; // capture at least 30s per slot

            if (!shouldRotate && _playingSlot >= 0)
            {
                // More precise: estimate remaining time on playing slot
                var playingInfo = new FileInfo(_slotPaths[_playingSlot]);
                if (playingInfo.Exists)
                {
                    double totalSecs = playingInfo.Length / (_settings.BitRate * 1000.0 / 8.0);
                    // ETW offset tells us bytes read; approximate seconds remaining
                    // (simplified: rotate when playing slot was last touched > threshold ago)
                    shouldRotate = captured.TotalSeconds >= Math.Max(20, totalSecs * 0.7);
                }
            }

            if (shouldRotate && _slotStates[_captureSlot] == SlotState.Capturing)
            {
                int currentSlot = _captureSlot;
                int nextSlot = (currentSlot + 1) % _settings.SlotCount;

                // Don't overwrite a slot GTA is currently playing
                if (_slotStates[nextSlot] != SlotState.Playing)
                {
                    _ = RotateSlotAsync(currentSlot, nextSlot);
                }
            }

            FireStatus();
        }
    }

    private async Task RotateSlotAsync(int finishedSlot, int nextSlot)
    {
        Debug.WriteLine($"[SlotManager] Rotating: finalising slot {finishedSlot}, next → {nextSlot}");
        await FinaliseCurrentCaptureAsync(finishedSlot);
        BeginCaptureIntoSlot(nextSlot);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSlotStartedPlaying(string slotFileName)
    {
        lock (_stateLock)
        {
            for (int i = 0; i < _slotPaths.Length; i++)
            {
                if (string.Equals(Path.GetFileName(_slotPaths[i]), slotFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Mark previous playing slot as spent
                    if (_playingSlot >= 0 && _playingSlot != i)
                        _slotStates[_playingSlot] = SlotState.Spent;

                    _playingSlot = i;
                    if (_slotStates[i] != SlotState.Capturing)
                        _slotStates[i] = SlotState.Playing;

                    NowPlayingChanged?.Invoke(_slotTracks[i]);
                    Debug.WriteLine($"[SlotManager] GTA now playing slot {i}");
                    FireStatus();
                    return;
                }
            }
        }
    }

    private void OnTrackChanged(NowPlayingWatcher.TrackInfo track)
    {
        // Record which track is going into the current capture slot
        lock (_stateLock)
        {
            _slotTracks[_captureSlot] = track;
        }
        Debug.WriteLine($"[SlotManager] Track changed: {track.Artist} – {track.Title}");
        FireStatus();
    }

    private void OnSilenceDetected()
    {
        Debug.WriteLine("[SlotManager] Silence detected (Apple Music paused?)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureUserMusicPathExists()
    {
        try { Directory.CreateDirectory(_settings.UserMusicPath); }
        catch (Exception ex) { Debug.WriteLine($"[SlotManager] Cannot create UserMusic dir: {ex.Message}"); }
    }

    private void FireStatus()
    {
        var captured = _running ? _capture.GetCapturedDuration(_settings.BitRate) : TimeSpan.Zero;
        StatusChanged?.Invoke(new BridgeStatus(
            _running,
            _captureSlot,
            _playingSlot,
            (SlotState[])_slotStates.Clone(),
            captured,
            _nowPlaying.CurrentTrack));
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _monitor.Dispose();
        _nowPlaying.Dispose();
    }
}
