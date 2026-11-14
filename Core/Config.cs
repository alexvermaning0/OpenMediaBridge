using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenMediaBridge
{
    public class Config
    {
        [JsonPropertyName("port")]
        public int Port { get; set; }

        // Players to never use at all.
        [JsonPropertyName("ignorePlayers")]
        public string[] IgnorePlayers { get; set; }

        // Players to use only when nothing else is active — e.g. a browser you
        // want as a fallback for radio, but which should yield to Spotify or any
        // other app whenever one of those is playing. A playing deprioritized
        // player still beats a paused normal one.
        [JsonPropertyName("deprioritizePlayers")]
        public string[] DeprioritizePlayers { get; set; } = System.Array.Empty<string>();

        [JsonPropertyName("lyrics_port")]
        public int LyricsPort { get; set; } = 6555;

        [JsonPropertyName("cover_port")]
        public int CoverPort { get; set; } = 8081;

        public List<string> DisableLyricsFor { get; set; } = new();

        [JsonPropertyName("offset_ms")]
        public int OffsetMs { get; set; } = 0;

        [JsonPropertyName("cache_folder")]
        public string CacheFolder { get; set; } = "cache";

        [JsonPropertyName("filter_cjk_lyrics")]
        public bool FilterCjkLyrics { get; set; } = true;

        [JsonPropertyName("offline_mode")]
        public bool OfflineMode { get; set; } = false;

        [JsonPropertyName("lrclib_database_path")]
        public string LrclibDatabasePath { get; set; } = "db.sqlite3";

        [JsonPropertyName("plain_lyrics_fallback")]
        public bool PlainLyricsFallback { get; set; } = false;

        // Discord integration (optional - leave token empty to disable)
        [JsonPropertyName("discord_token")]
        public string DiscordToken { get; set; } = "";

        [JsonPropertyName("discord_emoji")]
        public string DiscordEmoji { get; set; } = "🎶";

        [JsonPropertyName("discord_show_prefix")]
        public bool DiscordShowPrefix { get; set; } = true;

        // Translation (LibreTranslate-compatible endpoint — set api_key if your instance requires one)
        [JsonPropertyName("translation_enabled")]
        public bool TranslationEnabled { get; set; } = false;

        [JsonPropertyName("translation_target_lang")]
        public string TranslationTargetLang { get; set; } = "en";

        [JsonPropertyName("translation_libretranslate_url")]
        public string LibreTranslateUrl { get; set; } = "https://translate.minekingshosting.nl";

        [JsonPropertyName("translation_api_key")]
        public string TranslationApiKey { get; set; } = "";

        // Colors used to render the full-lyrics karaoke view (lyricsview:), as
        // rich-text markup understood by Resonite/TMP overlays. Past = lines
        // already sung, current = the active line (also bolded), upcoming = the
        // rest.
        [JsonPropertyName("lyricsview_past_color")]
        public string LyricsViewPastColor { get; set; } = "#808080";

        [JsonPropertyName("lyricsview_current_color")]
        public string LyricsViewCurrentColor { get; set; } = "#FFE100";

        [JsonPropertyName("lyricsview_upcoming_color")]
        public string LyricsViewUpcomingColor { get; set; } = "#FFFFFF";

        // When a live stream reports only a station name (no track), look that
        // station up in stations.json and resolve the real now-playing. On by
        // default; set false to disable the station lookups entirely.
        [JsonPropertyName("station_lookup")]
        public bool StationLookup { get; set; } = true;

        // Show a live stream's station name even when it has no stations.json
        // entry yet (so it's visible to add). On by default; set false to only
        // show configured stations (e.g. to keep other live tabs hidden).
        [JsonPropertyName("show_unmatched_streams")]
        public bool ShowUnmatchedStreams { get; set; } = true;
    }
}
