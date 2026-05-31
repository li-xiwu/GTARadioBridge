using Windows.Media.Control;

namespace GTARadioBridge.Core;

/// <summary>
/// Watches the Windows Global System Media Transport Controls session
/// for Apple Music (or any active media player) and raises events when
/// the track changes or playback state changes.
/// </summary>
public class NowPlayingWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public record TrackInfo(string Title, string Artist, string Album, TimeSpan Duration);

    public event Action<TrackInfo>? TrackChanged;
    public event Action<bool>? PlaybackStateChanged; // true = playing

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnSessionChanged;
        AttachToCurrentSession(_manager.GetCurrentSession());
    }

    public async Task<TrackInfo?> GetCurrentTrackAsync()
    {
        if (_session == null) return null;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props == null) return null;

            var timeline = _session.GetTimelineProperties();
            return new TrackInfo(
                props.Title ?? "Unknown",
                props.Artist ?? "Unknown",
                props.AlbumTitle ?? "",
                timeline.EndTime
            );
        }
        catch { return null; }
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private void OnSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        AttachToCurrentSession(sender.GetCurrentSession());
    }

    private void AttachToCurrentSession(GlobalSystemMediaTransportControlsSession? newSession)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = newSession;

        if (_session == null) return;

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;

        // Fire immediately with current state
        _ = RefreshTrackInfoAsync();
        RefreshPlaybackState();
    }

    private async void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        await RefreshTrackInfoAsync();
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        RefreshPlaybackState();
    }

    private async Task RefreshTrackInfoAsync()
    {
        var track = await GetCurrentTrackAsync();
        if (track == null) return;
        if (track.Title == CurrentTrack?.Title && track.Artist == CurrentTrack?.Artist) return;

        CurrentTrack = track;
        TrackChanged?.Invoke(track);
    }

    private void RefreshPlaybackState()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            bool playing = info.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing == IsPlaying) return;
            IsPlaying = playing;
            PlaybackStateChanged?.Invoke(playing);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        if (_manager != null)
            _manager.CurrentSessionChanged -= OnSessionChanged;
    }
}
