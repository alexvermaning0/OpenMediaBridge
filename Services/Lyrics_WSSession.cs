using NetCoreServer;
using System;

namespace OpenMediaBridge.Services
{
    public class LyricsWSSession : WsSession
    {
        private readonly LyricsService _lyricsService;

        public LyricsWSSession(LyricsWSServer server, LyricsService lyricsService) : base(server)
        {
            _lyricsService = lyricsService;
            _lyricsService.OnLyricUpdate += SendLyric;
        }

        private void SendLyric(string lyric, double progress)
        {
            // Send lyric if not null (null means progress-only update)
            if (lyric != null)
            {
                SendText($"lyric:{lyric}");
            }
            SendText($"prog:{progress:F3}");
        }

        public override void OnWsConnected(HttpRequest request)
        {
            base.OnWsConnected(request);
            LyricsWSServer.ConnectedCount++;
            Console.WriteLine($"[WebSocket] Lyrics clients connected: {LyricsWSServer.ConnectedCount}");
            
            // Send initial state
            SendText($"wordsync:{_lyricsService.WordSyncEnabled.ToString().ToLower()}");
            SendText($"lyricsrc:{_lyricsService.CurrentSource}");
            SendText($"offset:{_lyricsService.CurrentOffset}");
        }

        public override void OnWsDisconnected()
        {
            base.OnWsDisconnected();
            LyricsWSServer.ConnectedCount = Math.Max(0, LyricsWSServer.ConnectedCount - 1);
            Console.WriteLine($"[WebSocket] Lyrics clients connected: {LyricsWSServer.ConnectedCount}");
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var msg = System.Text.Encoding.UTF8.GetString(buffer, (int)offset, (int)size).Trim();
            var msgLower = msg.ToLowerInvariant();

            // Word sync toggle (legacy commands)
            if (msgLower == "wordsync:on")
            {
                _lyricsService.EnableWordSync();
                SendText("wordsync:true");
                return;
            }
            if (msgLower == "wordsync:off")
            {
                _lyricsService.DisableWordSync();
                SendText("wordsync:false");
                return;
            }

            // Toggle commands
            if (msgLower == "toggle:wordsync" || msgLower == "w")
            {
                _lyricsService.ToggleWordSync();
                SendText($"wordsync:{_lyricsService.WordSyncEnabled.ToString().ToLower()}");
                return;
            }
            if (msgLower == "toggle:offline" || msgLower == "o")
            {
                _lyricsService.ToggleOfflineMode();
                SendText($"offline:{_lyricsService.OfflineEnabled.ToString().ToLower()}");
                return;
            }
            if (msgLower == "toggle:cjk" || msgLower == "c")
            {
                _lyricsService.ToggleCjkFilter();
                SendText($"cjk:{_lyricsService.CjkFilterEnabled.ToString().ToLower()}");
                return;
            }
            if (msgLower == "toggle:plain" || msgLower == "p")
            {
                _lyricsService.TogglePlainFallback();
                SendText($"plain:{_lyricsService.PlainFallbackEnabled.ToString().ToLower()}");
                return;
            }

            // Lyrics navigation
            if (msgLower == "next" || msgLower == "n")
            {
                _lyricsService.NextLyrics();
                SendText($"lyricsrc:{_lyricsService.CurrentSource}");
                return;
            }
            if (msgLower == "refresh" || msgLower == "r")
            {
                _lyricsService.RefreshLyrics();
                SendText("lyrics:refreshing");
                return;
            }
            if (msgLower == "clearcache" || msgLower == "x")
            {
                _lyricsService.ClearCacheAndRefresh();
                SendText("cache:cleared");
                return;
            }

            // Offset adjustment
            if (msgLower == "offset:+50" || msgLower == "+")
            {
                _lyricsService.AdjustOffset(50);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower == "offset:-50" || msgLower == "-")
            {
                _lyricsService.AdjustOffset(-50);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower == "offset:+500")
            {
                _lyricsService.AdjustOffset(500);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower == "offset:-500")
            {
                _lyricsService.AdjustOffset(-500);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower.StartsWith("offset:"))
            {
                var offsetStr = msg.Substring(7);
                if (int.TryParse(offsetStr, out int customOffset))
                {
                    _lyricsService.AdjustOffset(customOffset);
                    SendText($"offset:{_lyricsService.CurrentOffset}");
                }
                return;
            }
            if (msgLower == "offset:save" || msgLower == "s")
            {
                _lyricsService.SaveOffset();
                SendText("offset:saved");
                return;
            }

            // Full lyrics request
            if (msgLower == "getfulllyrics")
            {
                var fullLyrics = _lyricsService.GetFullLyricsText();
                SendText($"fulllyrics:{fullLyrics}");
                return;
            }

            // Status request
            if (msgLower == "status" || msgLower == "?")
            {
                SendText($"wordsync:{_lyricsService.WordSyncEnabled.ToString().ToLower()}");
                SendText($"lyricsrc:{_lyricsService.CurrentSource}");
                SendText($"offset:{_lyricsService.CurrentOffset}");
                SendText($"offline:{_lyricsService.OfflineEnabled.ToString().ToLower()}");
                SendText($"cjk:{_lyricsService.CjkFilterEnabled.ToString().ToLower()}");
                SendText($"plain:{_lyricsService.PlainFallbackEnabled.ToString().ToLower()}");
                return;
            }

            // Help
            if (msgLower == "help" || msgLower == "h")
            {
                SendText("commands:w,o,c,p,n,r,x,+,-,s,?,getfulllyrics");
                return;
            }
        }
    }
}
