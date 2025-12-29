using NetCoreServer;
using OpenMediaBridge.Services;
using System;
using System.Text;
using System.Timers;
using Timer = System.Timers.Timer;

namespace OpenMediaBridge
{
    public class ResoniteWSSession : WsSession
    {
        private WindowsMediaService WMService { get; set; }
        private LyricsService LyricsService { get; set; }
        private Timer _positionTimer;
        
        // Track last sent values to only send on change
        private string _lastTitle = "";
        private string _lastArtist = "";
        private string _lastAlbum = "";
        private long _lastDuration = 0;
        private string _lastCover = "";
        private string _lastSource = "";
        private string _lastLyricSrc = "";
        private bool _lastStatus = false;
        private bool _lastShuffle = false;
        private string _lastRepeat = "";
        private string _lastLyric = "";
        private double _lastProgress = 0;
        private bool _lastWordSync = false;
        private int _lastOffset = 0;
        private bool _initialSent = false;

        public ResoniteWSSession(ResoniteWSServer server) : base(server) 
        {
            WMService = new WindowsMediaService(this, server);
        }

        public void SetLyricsService(LyricsService lyricsService)
        {
            LyricsService = lyricsService;
            if (LyricsService != null)
            {
                LyricsService.OnLyricUpdate += OnLyricUpdate;
                LyricsService.OnStatusChanged += OnStatusChanged;
            }
        }

        private void OnLyricUpdate(string lyric, double progress)
        {
            // Send lyric if changed (not null)
            if (lyric != null && lyric != _lastLyric)
            {
                _lastLyric = lyric;
                SendText($"lyric:{lyric}");
            }
            
            // Always send progress
            SendText($"prog:{progress:F3}");
        }

        private void OnStatusChanged(string key, string value)
        {
            SendText($"{key}:{value}");
        }

        public override void OnWsConnected(HttpRequest request)
        {
            base.OnWsConnected(request);
            ResoniteWSServer.ConnectedCount++;
            Console.WriteLine($"[WebSocket] Resonite clients connected: {ResoniteWSServer.ConnectedCount}");

            // Send initial state on connect
            SendInitialState();

            // Start position timer (every 1 second)
            _positionTimer = new Timer(1000);
            _positionTimer.Elapsed += SendPosition;
            _positionTimer.Start();
        }

        private void SendInitialState()
        {
            _initialSent = true;
            
            // Send media info
            if (WMService?.CurrentMediaProperties != null)
            {
                var props = WMService.CurrentMediaProperties;
                
                _lastTitle = props.Title ?? "";
                _lastArtist = props.Artist ?? "";
                _lastAlbum = props.AlbumTitle ?? "";
                
                SendText($"title:{_lastTitle}");
                SendText($"artist:{_lastArtist}");
                SendText($"album:{_lastAlbum}");
            }
            
            // Send playback info
            if (WMService?.CurrentMediaSession != null)
            {
                var playback = WMService.CurrentMediaSession.GetPlaybackInfo();
                var timeline = WMService.CurrentMediaSession.GetTimelineProperties();
                
                _lastDuration = (long)timeline.EndTime.TotalMilliseconds;
                _lastStatus = playback.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _lastShuffle = playback.IsShuffleActive ?? false;
                _lastRepeat = playback.AutoRepeatMode?.ToString()?.ToLower() ?? "none";
                _lastSource = WMService.CurrentMediaSession.SourceAppUserModelId ?? "";
                
                // Clean up source name
                if (_lastSource.Contains("."))
                    _lastSource = _lastSource.Split('.')[0];
                
                SendText($"dur:{_lastDuration}");
                SendText($"status:{_lastStatus.ToString().ToLower()}");
                SendText($"shuffle:{_lastShuffle.ToString().ToLower()}");
                SendText($"repeat:{_lastRepeat}");
                SendText($"source:{_lastSource}");
                
                // Position
                SendText($"pos:{(long)timeline.Position.TotalMilliseconds}");
            }
            
            // Send lyrics service info
            if (LyricsService != null)
            {
                _lastLyricSrc = LyricsService.CurrentSource;
                _lastWordSync = LyricsService.WordSyncEnabled;
                _lastOffset = LyricsService.CurrentOffset;
                
                SendText($"lyricsrc:{_lastLyricSrc}");
                SendText($"wordsync:{_lastWordSync.ToString().ToLower()}");
                SendText($"offset:{_lastOffset}");
            }
            
            // Cover URL (if cover server is running)
            var coverUrl = CoverServer.GetCurrentCoverUrl();
            if (!string.IsNullOrEmpty(coverUrl))
            {
                _lastCover = coverUrl;
                SendText($"cover:{_lastCover}");
            }
        }

        private void SendPosition(object sender, ElapsedEventArgs e)
        {
            if (WMService?.CurrentMediaSession == null) return;
            
            try
            {
                var timeline = WMService.CurrentMediaSession.GetTimelineProperties();
                SendText($"pos:{(long)timeline.Position.TotalMilliseconds}");
            }
            catch { }
        }

        public void SendMediaUpdate()
        {
            if (!_initialSent) return;
            if (WMService?.CurrentMediaProperties == null) return;
            
            try
            {
                var props = WMService.CurrentMediaProperties;
                
                // Only send if changed
                if (props.Title != _lastTitle)
                {
                    _lastTitle = props.Title ?? "";
                    SendText($"title:{_lastTitle}");
                }
                if (props.Artist != _lastArtist)
                {
                    _lastArtist = props.Artist ?? "";
                    SendText($"artist:{_lastArtist}");
                }
                if (props.AlbumTitle != _lastAlbum)
                {
                    _lastAlbum = props.AlbumTitle ?? "";
                    SendText($"album:{_lastAlbum}");
                }
                
                // Update duration and source on song change
                if (WMService?.CurrentMediaSession != null)
                {
                    var timeline = WMService.CurrentMediaSession.GetTimelineProperties();
                    var newDuration = (long)timeline.EndTime.TotalMilliseconds;
                    if (newDuration != _lastDuration)
                    {
                        _lastDuration = newDuration;
                        SendText($"dur:{_lastDuration}");
                    }
                    
                    var newSource = WMService.CurrentMediaSession.SourceAppUserModelId ?? "";
                    if (newSource.Contains("."))
                        newSource = newSource.Split('.')[0];
                    if (newSource != _lastSource)
                    {
                        _lastSource = newSource;
                        SendText($"source:{_lastSource}");
                    }
                }
                
                // Check for new cover
                var coverUrl = CoverServer.GetCurrentCoverUrl();
                if (coverUrl != _lastCover)
                {
                    _lastCover = coverUrl ?? "";
                    SendText($"cover:{_lastCover}");
                }
            }
            catch { }
        }

        public void SendPlaybackUpdate()
        {
            if (!_initialSent) return;
            if (WMService?.CurrentMediaSession == null) return;
            
            try
            {
                var playback = WMService.CurrentMediaSession.GetPlaybackInfo();
                
                var newStatus = playback.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (newStatus != _lastStatus)
                {
                    _lastStatus = newStatus;
                    SendText($"status:{_lastStatus.ToString().ToLower()}");
                }
                
                var newShuffle = playback.IsShuffleActive ?? false;
                if (newShuffle != _lastShuffle)
                {
                    _lastShuffle = newShuffle;
                    SendText($"shuffle:{_lastShuffle.ToString().ToLower()}");
                }
                
                var newRepeat = playback.AutoRepeatMode?.ToString()?.ToLower() ?? "none";
                if (newRepeat != _lastRepeat)
                {
                    _lastRepeat = newRepeat;
                    SendText($"repeat:{_lastRepeat}");
                }
            }
            catch { }
        }

        public void SendLyricsSourceUpdate(string source)
        {
            if (!_initialSent) return;
            if (source != _lastLyricSrc)
            {
                _lastLyricSrc = source;
                SendText($"lyricsrc:{_lastLyricSrc}");
            }
        }

        public void SendSettingUpdate(string key, string value)
        {
            if (!_initialSent) return;
            SendText($"{key}:{value}");
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            string msg = Encoding.UTF8.GetString(buffer, (int)offset, (int)size).Trim();
            var msgLower = msg.ToLowerInvariant();
            
            switch (msgLower)
            {
                // Media controls
                case "forceupdatemedia":
                    SendMediaUpdate();
                    break;
                case "playmedia":
                case "play":
                    _ = WMService.TryMediaControl(MediaControlType.Play);
                    break;
                case "pausemedia":
                case "pause":
                    _ = WMService.TryMediaControl(MediaControlType.Pause);
                    break;
                case "stopmedia":
                case "stop":
                    _ = WMService.TryMediaControl(MediaControlType.Stop);
                    break;
                case "skiptonextmedia":
                case "next":
                    _ = WMService.TryMediaControl(MediaControlType.Skip);
                    break;
                case "skiptopreviousmedia":
                case "prev":
                case "previous":
                    _ = WMService.TryMediaControl(MediaControlType.Previous);
                    break;
                    
                // Lyrics controls (forwarded to LyricsService)
                case "wordsync:on":
                    LyricsService?.EnableWordSync();
                    SendText("wordsync:true");
                    break;
                case "wordsync:off":
                    LyricsService?.DisableWordSync();
                    SendText("wordsync:false");
                    break;
                case "toggle:wordsync":
                case "w":
                    LyricsService?.ToggleWordSync();
                    SendText($"wordsync:{LyricsService?.WordSyncEnabled.ToString().ToLower()}");
                    break;
                case "toggle:offline":
                case "o":
                    LyricsService?.ToggleOfflineMode();
                    SendText($"offline:{LyricsService?.OfflineEnabled.ToString().ToLower()}");
                    break;
                case "toggle:cjk":
                case "c":
                    LyricsService?.ToggleCjkFilter();
                    SendText($"cjk:{LyricsService?.CjkFilterEnabled.ToString().ToLower()}");
                    break;
                case "toggle:plain":
                case "p":
                    LyricsService?.TogglePlainFallback();
                    SendText($"plain:{LyricsService?.PlainFallbackEnabled.ToString().ToLower()}");
                    break;
                case "nextlyrics":
                case "n":
                    LyricsService?.NextLyrics();
                    SendText($"lyricsrc:{LyricsService?.CurrentSource}");
                    break;
                case "refresh":
                case "r":
                    LyricsService?.RefreshLyrics();
                    break;
                case "clearcache":
                case "x":
                    LyricsService?.ClearCacheAndRefresh();
                    break;
                case "offset:+50":
                case "+":
                    LyricsService?.AdjustOffset(50);
                    SendText($"offset:{LyricsService?.CurrentOffset}");
                    break;
                case "offset:-50":
                case "-":
                    LyricsService?.AdjustOffset(-50);
                    SendText($"offset:{LyricsService?.CurrentOffset}");
                    break;
                case "offset:+500":
                    LyricsService?.AdjustOffset(500);
                    SendText($"offset:{LyricsService?.CurrentOffset}");
                    break;
                case "offset:-500":
                    LyricsService?.AdjustOffset(-500);
                    SendText($"offset:{LyricsService?.CurrentOffset}");
                    break;
                case "offset:save":
                case "s":
                    LyricsService?.SaveOffset();
                    break;
                    
                // Status/data requests
                case "getstatus":
                case "status":
                case "?":
                    SendInitialState();
                    break;
                case "getfulllyrics":
                    SendFullLyrics();
                    break;
                    
                // Help
                case "help":
                case "h":
                    SendText("commands:play,pause,next,prev,stop,w,o,c,p,n,r,x,+,-,s,?,getfulllyrics");
                    break;
                    
                default:
                    // Check for custom offset
                    if (msgLower.StartsWith("offset:"))
                    {
                        var offsetStr = msg.Substring(7);
                        if (int.TryParse(offsetStr, out int customOffset))
                        {
                            LyricsService?.AdjustOffset(customOffset);
                            SendText($"offset:{LyricsService?.CurrentOffset}");
                        }
                    }
                    break;
            }
        }

        private void SendFullLyrics()
        {
            var fullLyrics = LyricsService?.GetFullLyricsText();
            if (!string.IsNullOrEmpty(fullLyrics))
            {
                SendText($"fulllyrics:{fullLyrics}");
            }
            else
            {
                SendText("fulllyrics:");
            }
        }

        public override void OnWsDisconnected()
        {
            base.OnWsDisconnected();
            ResoniteWSServer.ConnectedCount = Math.Max(0, ResoniteWSServer.ConnectedCount - 1);
            Console.WriteLine($"[WebSocket] Resonite clients connected: {ResoniteWSServer.ConnectedCount}");
            
            _positionTimer?.Stop();
            _positionTimer?.Dispose();
            
            if (LyricsService != null)
            {
                LyricsService.OnLyricUpdate -= OnLyricUpdate;
                LyricsService.OnStatusChanged -= OnStatusChanged;
            }
            
            WMService.Dispose();
        }
    }
}
