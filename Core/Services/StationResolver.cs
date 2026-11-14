#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenMediaBridge.Services
{
    // One station handler from stations.json. A live stream whose player title
    // contains Match is looked up at Url and parsed per Type.
    public class StationEntry
    {
        [JsonPropertyName("match")] public string Match { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "delimited"; // "delimited" | "json"
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 10;

        // delimited: split the body by SectionSep, take Section, split that by
        // FieldSep, and read the artist/title/cover field indexes (cover -1 = none).
        [JsonPropertyName("sectionSep")] public string SectionSep { get; set; } = "#";
        [JsonPropertyName("section")] public int Section { get; set; } = 1;
        [JsonPropertyName("fieldSep")] public string FieldSep { get; set; } = ";";
        [JsonPropertyName("artistField")] public int ArtistField { get; set; } = 0;
        [JsonPropertyName("titleField")] public int TitleField { get; set; } = 1;
        [JsonPropertyName("coverField")] public int CoverField { get; set; } = -1;

        // json: dot-paths into the response, e.g. "now_playing.song.artist".
        [JsonPropertyName("artistPath")] public string ArtistPath { get; set; } = "";
        [JsonPropertyName("titlePath")] public string TitlePath { get; set; } = "";
        [JsonPropertyName("coverPath")] public string CoverPath { get; set; } = "";
    }

    // Default stations.json written on first run — a working RADIONL Kids
    // example that resolves its now-playing without any extra setup.
    public static class StationDefaults
    {
        public const string Json = @"{
  ""stations"": [
    {
      ""match"": ""RADIONL Kids"",
      ""url"": ""https://radionlkids.nl/wp-content/themes/kids/checkLaatstGespeeld.php"",
      ""type"": ""delimited"",
      ""sectionSep"": ""#"",
      ""section"": 1,
      ""fieldSep"": "";"",
      ""artistField"": 0,
      ""titleField"": 1,
      ""coverField"": 2,
      ""pollSeconds"": 10,
      ""enabled"": true
    }
  ]
}
";
    }

    // Resolves "now playing" for live-radio streams that don't publish track
    // metadata, by polling a per-station endpoint defined in stations.json.
    // Fetches are cached and refreshed in the background per station.
    public class StationResolver
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private readonly List<StationEntry> _stations = new();
        private readonly ConcurrentDictionary<StationEntry, (MediaProperties props, DateTime at)> _cache = new();
        private readonly ConcurrentDictionary<StationEntry, bool> _inflight = new();

        public Action<string>? LogCallback { get; set; }
        private void Log(string m) => LogCallback?.Invoke(m);

        public IReadOnlyList<StationEntry> Stations => _stations;

        private class StationFile { [JsonPropertyName("stations")] public List<StationEntry>? Stations { get; set; } }

        public StationResolver(string stationsPath)
        {
            try
            {
                if (!File.Exists(stationsPath)) return;
                var doc = JsonSerializer.Deserialize<StationFile>(File.ReadAllText(stationsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc?.Stations != null)
                    _stations.AddRange(doc.Stations.Where(s =>
                        s != null && s.Enabled &&
                        !string.IsNullOrWhiteSpace(s.Match) && !string.IsNullOrWhiteSpace(s.Url)));
                Log($"[Stations] Loaded {_stations.Count} station(s) from {stationsPath}");
            }
            catch (Exception ex) { Log($"[Stations] Failed to load {stationsPath}: {ex.Message}"); }
        }

        // First station whose Match text appears in the given player title.
        public StationEntry? Match(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            foreach (var s in _stations)
                if (title.IndexOf(s.Match, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            return null;
        }

        // Latest known now-playing for a station, refreshing in the background
        // when the cached value is older than the station's poll interval.
        // Returns null until the first fetch lands.
        public MediaProperties? Resolve(StationEntry e)
        {
            bool haveCache = _cache.TryGetValue(e, out var c);
            bool stale = !haveCache || (DateTime.UtcNow - c.at).TotalSeconds >= Math.Max(2, e.PollSeconds);
            if (stale && _inflight.TryAdd(e, true))
                _ = RefreshAsync(e);
            return haveCache ? c.props : null;
        }

        private async Task RefreshAsync(StationEntry e)
        {
            try
            {
                var body = await _http.GetStringAsync(e.Url);
                var props = Parse(e, body);
                if (props != null)
                    _cache[e] = (props, DateTime.UtcNow);
                else if (_cache.TryGetValue(e, out var old))
                    _cache[e] = (old.props, DateTime.UtcNow); // keep last, back off
            }
            catch (Exception ex) { Log($"[Stations] {e.Match}: {ex.Message}"); }
            finally { _inflight.TryRemove(e, out _); }
        }

        private MediaProperties? Parse(StationEntry e, string body)
        {
            try
            {
                string artist, title, cover;
                if (e.Type.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(body);
                    artist = JsonPath(doc.RootElement, e.ArtistPath);
                    title = JsonPath(doc.RootElement, e.TitlePath);
                    cover = JsonPath(doc.RootElement, e.CoverPath);
                }
                else
                {
                    var sections = e.SectionSep.Length > 0
                        ? body.Split(new[] { e.SectionSep }, StringSplitOptions.None)
                        : new[] { body };
                    var sec = (e.Section >= 0 && e.Section < sections.Length) ? sections[e.Section] : sections[0];
                    var fields = e.FieldSep.Length > 0
                        ? sec.Split(new[] { e.FieldSep }, StringSplitOptions.None)
                        : new[] { sec };
                    string Field(int i) => (i >= 0 && i < fields.Length) ? fields[i] : "";
                    artist = Field(e.ArtistField);
                    title = Field(e.TitleField);
                    cover = Field(e.CoverField);
                }

                artist = Clean(artist);
                title = Clean(title);
                cover = (cover ?? "").Trim();
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist)) return null;

                return new MediaProperties
                {
                    Title = title,
                    Artist = artist,
                    AlbumTitle = "",
                    ArtUrl = IsRealCover(cover) ? cover : ""
                };
            }
            catch (Exception ex) { Log($"[Stations] parse {e.Match}: {ex.Message}"); return null; }
        }

        private static string Clean(string s) => WebUtility.HtmlDecode((s ?? "").Trim());

        // Many stations hand back a placeholder graphic when they have no real
        // cover; treat those as "no cover" so the normal art lookup can run.
        private static bool IsRealCover(string c) =>
            !string.IsNullOrWhiteSpace(c) &&
            c.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            c.IndexOf("no_album", StringComparison.OrdinalIgnoreCase) < 0;

        // Navigate a dot-path, supporting object keys and numeric array indexes
        // (e.g. "0.now_playing.song.artist" for an array-of-stations response).
        private static string JsonPath(JsonElement root, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var el = root;
            foreach (var part in path.Split('.'))
            {
                if (el.ValueKind == JsonValueKind.Array && int.TryParse(part, out int idx))
                {
                    if (idx < 0 || idx >= el.GetArrayLength()) return "";
                    el = el[idx];
                }
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(part, out var next))
                {
                    el = next;
                }
                else return "";
            }
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
        }
    }
}
