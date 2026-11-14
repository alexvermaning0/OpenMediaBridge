using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace OpenMediaBridge.Services
{
    public class WindowsMediaService : IMediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private GlobalSystemMediaTransportControlsSession _currentSession;
        private GlobalSystemMediaTransportControlsSessionMediaProperties _winrtMediaProperties;

        public ResoniteWSSession WSSession { get; private set; }
        public ResoniteWSServer Server { get; private set; }
        public Config Config { get; }

        public MediaProperties? CurrentMediaProperties { get; private set; }
        public bool HasActiveSession => _currentSession != null;
        public string CurrentSourceApp => _currentSession?.SourceAppUserModelId ?? "";

        // Debounce for rapid song changes
        private string _lastProcessedSong = "";
        private readonly object _updateLock = new object();

        // Windows initializes event subscriptions in the constructor, so there is
        // nothing to start here. Present to satisfy IMediaService.
        public void Start() { }

        public WindowsMediaService(ResoniteWSSession session, ResoniteWSServer server)
        {
            WSSession = session;
            Server = server;
            Config = server.Config;

            SetSystemMediaTransportControlsSessionManager().GetAwaiter().GetResult();

            _sessionManager.CurrentSessionChanged += MediaTransportControlsSessionManager_CurrentSessionChanged;

            // try and get current session
            _currentSession = _sessionManager.GetCurrentSession();
            if (_currentSession != null)
            {
                _winrtMediaProperties = _currentSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
                CurrentMediaProperties = ToMediaProperties(_winrtMediaProperties);
                _currentSession.MediaPropertiesChanged += CurrentMediaSession_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += CurrentMediaSession_PlaybackInfoChanged;

                // Update cover on startup (fire and forget for constructor)
                _ = CoverServer.UpdateCoverAsync(CurrentMediaProperties);
            }
        }

        private static MediaProperties ToMediaProperties(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            if (props == null) return null;
            return new MediaProperties
            {
                Title = props.Title ?? "",
                Artist = props.Artist ?? "",
                AlbumTitle = props.AlbumTitle ?? "",
            };
        }

        private async void MediaTransportControlsSessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            // This is an async void event handler — an unhandled exception here
            // crashes the process, so everything is wrapped.
            try
            {
                // get and set session and properties
                _currentSession = _sessionManager.GetCurrentSession();

                // reset events
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged += CurrentMediaSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += CurrentMediaSession_PlaybackInfoChanged;

                    _winrtMediaProperties = await _currentSession.TryGetMediaPropertiesAsync();
                    CurrentMediaProperties = ToMediaProperties(_winrtMediaProperties);

                    // Push metadata now; fetch cover art in the background and
                    // re-push when it resolves, rather than blocking on it.
                    var media = CurrentMediaProperties;
                    WSSession.SendMediaUpdate();
                    _ = CoverServer.UpdateCoverAsync(media).ContinueWith(
                        _ => WSSession.SendMediaUpdate(), TaskScheduler.Default);
                }
            }
            catch { }
        }

        private async void CurrentMediaSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            // Use sender instead of CurrentMediaSession - it's more reliable
            if (sender == null) return;

            try
            {
                // Get properties immediately when event fires
                var props = await sender.TryGetMediaPropertiesAsync();
                if (props == null) return;

                // Capture values before any async work
                var title = props.Title ?? "";
                var artist = props.Artist ?? "";
                var songKey = $"{title}|{artist}";

                // Debounce - skip if we're already processing this song
                lock (_updateLock)
                {
                    if (songKey == _lastProcessedSong) return;
                    _lastProcessedSong = songKey;
                }

                // Update stored properties
                _winrtMediaProperties = props;
                CurrentMediaProperties = ToMediaProperties(props);

                // Push metadata now; fetch cover in the background and re-push
                // when ready, so title/artist aren't delayed by the cover lookup.
                var media = CurrentMediaProperties;
                WSSession.SendMediaUpdate();
                _ = CoverServer.UpdateCoverAsync(media).ContinueWith(
                    _ => WSSession.SendMediaUpdate(), TaskScheduler.Default);
            }
            catch { }
        }

        private void CurrentMediaSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            // Use sender instead of CurrentMediaSession
            if (sender == null) return;

            try
            {
                // Notify session of playback update
                WSSession.SendPlaybackUpdate();
            }
            catch { }
        }

        private async Task SetSystemMediaTransportControlsSessionManager()
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }

        public MediaPlaybackInfo GetPlaybackInfo()
        {
            var session = _currentSession;
            if (session == null) return new MediaPlaybackInfo();

            var playback = session.GetPlaybackInfo();
            return new MediaPlaybackInfo
            {
                IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                IsShuffleActive = playback.IsShuffleActive ?? false,
                RepeatMode = playback.AutoRepeatMode switch
                {
                    Windows.Media.MediaPlaybackAutoRepeatMode.Track => "track",
                    Windows.Media.MediaPlaybackAutoRepeatMode.List => "playlist",
                    _ => "none"
                }
            };
        }

        public MediaTimelineInfo GetTimelineInfo()
        {
            var session = _currentSession;
            if (session == null) return new MediaTimelineInfo();

            var timeline = session.GetTimelineProperties();
            var position = timeline.Position;

            // SMTC only refreshes Position when the app reports it, so for players
            // that report it infrequently it would otherwise appear frozen and
            // clients would see pos: stall. While playing, extrapolate from the
            // last update so the position keeps moving; pausing/seeking re-anchors
            // it on the next SMTC update.
            try
            {
                var playback = session.GetPlaybackInfo();
                if (playback != null
                    && playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                {
                    var extra = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    if (extra > TimeSpan.Zero)
                    {
                        position += extra;
                        if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                            position = timeline.EndTime;
                    }
                }
            }
            catch { }

            return new MediaTimelineInfo
            {
                Position = position,
                Duration = timeline.EndTime
            };
        }

        public async Task TryMediaControl(MediaControlType type)
        {
            try
            {
                if (_currentSession != null)
                {
                    switch (type)
                    {
                        case MediaControlType.Play:
                            await _currentSession.TryPlayAsync();
                            break;
                        case MediaControlType.Pause:
                            await _currentSession.TryPauseAsync();
                            break;
                        case MediaControlType.Stop:
                            await _currentSession.TryStopAsync();
                            break;
                        case MediaControlType.Skip:
                            await _currentSession.TrySkipNextAsync();
                            break;
                        case MediaControlType.Previous:
                            await _currentSession.TrySkipPreviousAsync();
                            break;
                    }
                }
            }
            catch { }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            var session = _currentSession;
            if (session == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            try
            {
                // SMTC wants the position in 100 ns ticks, which is exactly what
                // TimeSpan.Ticks is. Players that can't seek just return false.
                await session.TryChangePlaybackPositionAsync(position.Ticks);
            }
            catch { }
        }

        public void Dispose()
        {
            // deregister events
            if (_sessionManager != null)
                _sessionManager.CurrentSessionChanged -= MediaTransportControlsSessionManager_CurrentSessionChanged;
            if (_currentSession != null)
            {
                _currentSession.PlaybackInfoChanged -= CurrentMediaSession_PlaybackInfoChanged;
                _currentSession.MediaPropertiesChanged -= CurrentMediaSession_MediaPropertiesChanged;
            }

            // null out everything
            _sessionManager = null;
            _currentSession = null;
            _winrtMediaProperties = null;
            CurrentMediaProperties = null;
        }
    }
}
