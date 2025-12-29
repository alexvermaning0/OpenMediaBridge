using Windows.Media.Control;

namespace OpenMediaBridge.Services
{
    public class WindowsMediaService
    {
        public GlobalSystemMediaTransportControlsSessionManager MediaTransportControlsSessionManager;
        public GlobalSystemMediaTransportControlsSession CurrentMediaSession;
        public GlobalSystemMediaTransportControlsSessionMediaProperties CurrentMediaProperties;

        public ResoniteWSSession WSSession { get; private set; }
        public ResoniteWSServer Server { get; private set; }
        public Config Config { get; }
        
        // Debounce for rapid song changes
        private string _lastProcessedSong = "";
        private readonly object _updateLock = new object();

        public WindowsMediaService(ResoniteWSSession session, ResoniteWSServer server)
        {
            WSSession = session;
            Server = server;
            Config = server.Config;

            SetSystemMediaTransportControlsSessionManager().GetAwaiter().GetResult();

            MediaTransportControlsSessionManager.CurrentSessionChanged += MediaTransportControlsSessionManager_CurrentSessionChanged;

            // try and get current session
            CurrentMediaSession = MediaTransportControlsSessionManager.GetCurrentSession();
            if (CurrentMediaSession != null)
            {
                CurrentMediaProperties = CurrentMediaSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
                CurrentMediaSession.MediaPropertiesChanged += CurrentMediaSession_MediaPropertiesChanged;
                CurrentMediaSession.PlaybackInfoChanged += CurrentMediaSession_PlaybackInfoChanged;

                // Update cover on startup (fire and forget for constructor)
                _ = CoverServer.UpdateCoverAsync(CurrentMediaProperties);
            }
        }

        private async void MediaTransportControlsSessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            // get and set session and properties
            CurrentMediaSession = MediaTransportControlsSessionManager.GetCurrentSession();

            // reset events
            if (CurrentMediaSession != null)
            {
                CurrentMediaSession.MediaPropertiesChanged += CurrentMediaSession_MediaPropertiesChanged;
                CurrentMediaSession.PlaybackInfoChanged += CurrentMediaSession_PlaybackInfoChanged;
                
                CurrentMediaProperties = CurrentMediaSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();

                // Update cover art and WAIT for it
                await CoverServer.UpdateCoverAsync(CurrentMediaProperties);
                
                // Notify session of media update
                WSSession.SendMediaUpdate();
            }
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
                CurrentMediaProperties = props;

                // Fetch cover art and WAIT for it
                await CoverServer.UpdateCoverAsync(props);
                
                // Now notify session of media update (cover URL is ready)
                WSSession.SendMediaUpdate();
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
            MediaTransportControlsSessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }

        public async Task TryMediaControl(MediaControlType type)
        {
            try
            {
                if (CurrentMediaSession != null)
                {
                    switch(type)
                    {
                        case MediaControlType.Play:
                            await CurrentMediaSession.TryPlayAsync();
                            break;
                        case MediaControlType.Pause:
                            await CurrentMediaSession.TryPauseAsync();
                            break;
                        case MediaControlType.Stop:
                            await CurrentMediaSession.TryStopAsync();
                            break;
                        case MediaControlType.Skip:
                            await CurrentMediaSession.TrySkipNextAsync();
                            break;
                        case MediaControlType.Previous:
                            await CurrentMediaSession.TrySkipPreviousAsync();
                            break;
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            // deregister events
            MediaTransportControlsSessionManager.CurrentSessionChanged -= MediaTransportControlsSessionManager_CurrentSessionChanged;
            if (CurrentMediaSession != null)
            {
                CurrentMediaSession.PlaybackInfoChanged -= CurrentMediaSession_PlaybackInfoChanged;
                CurrentMediaSession.MediaPropertiesChanged -= CurrentMediaSession_MediaPropertiesChanged;
            }

            // null out everything
            MediaTransportControlsSessionManager = null;
            CurrentMediaSession = null;
            CurrentMediaProperties = null;
        }
    }

    public enum MediaControlType
    {
        Play,
        Pause,
        Stop,
        Skip,
        Previous
    }
}
