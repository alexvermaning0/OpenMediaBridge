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
            // Live status pushes (lyricsrc, wordsync, translate, offset, and the
            // lyricsview karaoke block) are raised by the service as they change;
            // without this subscription they'd only reach a client on connect or
            // as an echo of its own command.
            _lyricsService.OnStatusChanged += SendStatus;
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

        private void SendStatus(string key, string value)
        {
            SendText($"{key}:{value}");
        }

        public override void OnWsConnected(HttpRequest request)
        {
            base.OnWsConnected(request);
            LyricsWSServer.ConnectedCount++;
            Console.WriteLine($"[WebSocket] Lyrics clients connected: {LyricsWSServer.ConnectedCount}");
            
            // Send initial state (mirror the full set the status/? request sends,
            // so a client has every toggle's value on connect, not only after ?).
            SendText($"wordsync:{_lyricsService.WordSyncEnabled.ToString().ToLower()}");
            SendText($"offline:{_lyricsService.OfflineEnabled.ToString().ToLower()}");
            SendText($"cjk:{_lyricsService.CjkFilterEnabled.ToString().ToLower()}");
            SendText($"plain:{_lyricsService.PlainFallbackEnabled.ToString().ToLower()}");
            SendText($"lyricsrc:{_lyricsService.CurrentSource}");
            SendText($"lyricsrcnum:{_lyricsService.GetSourceNum()}");
            SendText($"offset:{_lyricsService.CurrentOffset}");
            SendText($"translate:{_lyricsService.TranslationEnabled.ToString().ToLower()}");
            SendText($"translatelang:{_lyricsService.TranslationTargetLang}");
            SendText($"viewcolors:{_lyricsService.ViewColors}");

            // Prime the karaoke view (and its current line index) so a client
            // that connects mid-song gets both immediately, not only on the next
            // line change.
            var view = _lyricsService.GetLyricsView();
            if (!string.IsNullOrEmpty(view))
                SendText($"lyricsview:{view}");
            SendText($"lyricsindex:{_lyricsService.GetCurrentLineIndex()}");
        }

        public override void OnWsDisconnected()
        {
            base.OnWsDisconnected();
            LyricsWSServer.ConnectedCount = Math.Max(0, LyricsWSServer.ConnectedCount - 1);
            Console.WriteLine($"[WebSocket] Lyrics clients connected: {LyricsWSServer.ConnectedCount}");

            // Unsubscribe so a disconnected session isn't kept alive by the
            // service's event and doesn't try to send on a closed socket.
            _lyricsService.OnLyricUpdate -= SendLyric;
            _lyricsService.OnStatusChanged -= SendStatus;
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
            if (msgLower == "toggle:translation" || msgLower == "t")
            {
                _lyricsService.ToggleTranslation();
                SendText($"translate:{_lyricsService.TranslationEnabled.ToString().ToLower()}");
                return;
            }
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

            // Offset adjustment. Bare +/- nudge by 50; save persists to config.
            if (msgLower == "+")
            {
                _lyricsService.AdjustOffset(50);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower == "-")
            {
                _lyricsService.AdjustOffset(-50);
                SendText($"offset:{_lyricsService.CurrentOffset}");
                return;
            }
            if (msgLower == "offset:save" || msgLower == "s")
            {
                _lyricsService.SaveOffset();
                SendText("offset:saved");
                return;
            }
            // offset:<n> adjusts by any signed integer of milliseconds, e.g.
            // offset:+50, offset:-500, offset:120. The legacy fixed steps
            // (offset:+50/-50/+500/-500) are just specific values of this, so
            // they keep working; the leading sign is optional. Checked after
            // "offset:save" so that word isn't parsed as a number.
            if (msgLower.StartsWith("offset:"))
            {
                if (int.TryParse(msg.Substring(7).Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int customOffset))
                {
                    _lyricsService.AdjustOffset(customOffset);
                    SendText($"offset:{_lyricsService.CurrentOffset}");
                }
                return;
            }

            // Full lyrics request
            if (msgLower == "getfulllyrics")
            {
                var fullLyrics = _lyricsService.GetFullLyricsText();
                SendText($"fulllyrics:{fullLyrics}");
                return;
            }

            // Full lyrics as a highlighted rich-text block (karaoke view)
            if (msgLower == "getlyricsview")
            {
                SendText($"lyricsview:{_lyricsService.GetLyricsView()}");
                return;
            }

            // Current line index within the karaoke view (-1 = no current line)
            if (msgLower == "getlyricsindex")
            {
                SendText($"lyricsindex:{_lyricsService.GetCurrentLineIndex()}");
                return;
            }

            // Status request
            if (msgLower == "status" || msgLower == "?")
            {
                SendText($"wordsync:{_lyricsService.WordSyncEnabled.ToString().ToLower()}");
                SendText($"lyricsrc:{_lyricsService.CurrentSource}");
                SendText($"lyricsrcnum:{_lyricsService.GetSourceNum()}");
                SendText($"offset:{_lyricsService.CurrentOffset}");
                SendText($"offline:{_lyricsService.OfflineEnabled.ToString().ToLower()}");
                SendText($"cjk:{_lyricsService.CjkFilterEnabled.ToString().ToLower()}");
                SendText($"plain:{_lyricsService.PlainFallbackEnabled.ToString().ToLower()}");
                SendText($"translate:{_lyricsService.TranslationEnabled.ToString().ToLower()}");
                SendText($"translatelang:{_lyricsService.TranslationTargetLang}");
                SendText($"viewcolors:{_lyricsService.ViewColors}");
                return;
            }

            // Theme the karaoke view: viewcolors:<past>,<current>,<upcoming>.
            // Empty fields keep the current color. Use the original (not lowered)
            // string so hex/named colors aren't mangled. SetViewColors re-pushes
            // lyricsview and viewcolors to every lyrics client.
            if (msgLower.StartsWith("viewcolors:"))
            {
                var parts = msg.Substring("viewcolors:".Length).Split(',');
                _lyricsService.SetViewColors(
                    parts.Length > 0 ? parts[0] : "",
                    parts.Length > 1 ? parts[1] : "",
                    parts.Length > 2 ? parts[2] : "");
                return;
            }

            // Language selection
            if (msgLower.StartsWith("lang:"))
            {
                var langCode = msg.Substring(5).Trim();
                _lyricsService.SetTranslationLanguage(langCode);
                SendText($"translatelang:{_lyricsService.TranslationTargetLang}");
                return;
            }

            // Help
            if (msgLower == "help" || msgLower == "h")
            {
                SendText("commands:t,w,o,c,p,n,r,x,+,-,s,?,getfulllyrics,getlyricsview,getlyricsindex,viewcolors:<past>,<current>,<upcoming>");
                return;
            }
        }
    }
}
