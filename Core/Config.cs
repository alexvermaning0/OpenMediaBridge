using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenMediaBridge
{
    public class Config
    {
        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("ignorePlayers")]
        public string[] IgnorePlayers { get; set; }

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
    }
}
