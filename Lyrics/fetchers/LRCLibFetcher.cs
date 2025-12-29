using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public static class LRCLibFetcher
    {
        private class SearchResult
        {
            public long id { get; set; }
            public string trackName { get; set; }
            public string artistName { get; set; }
            public string albumName { get; set; }
            public double duration { get; set; }
            public bool instrumental { get; set; }
            public string syncedLyrics { get; set; }
            public string plainLyrics { get; set; }
        }

        private class LyricGet
        {
            public long id { get; set; }
            public string syncedLyrics { get; set; }
            public string plainLyrics { get; set; }
        }

        /// <summary>
        /// Get all synced lyrics results (for cycling through alternatives)
        /// </summary>
        public static List<LyricsResult> GetAllLyrics(string title, string artist, int durationMs = 0)
        {
            var results = new List<LyricsResult>();
            
            try
            {
                using var wc = new WebClient();
                wc.Headers[HttpRequestHeader.UserAgent] = "OpenMediaBridge";

                var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title ?? string.Empty)}&artist_name={Uri.EscapeDataString(artist ?? string.Empty)}";

                var json = wc.DownloadString(url);
                var searchResults = JsonSerializer.Deserialize<List<SearchResult>>(json) ?? new List<SearchResult>();
                
                foreach (var result in searchResults)
                {
                    // Skip instrumental tracks
                    if (result.instrumental) continue;

                    var getJson = wc.DownloadString($"https://lrclib.net/api/get/{result.id}");
                    var get = JsonSerializer.Deserialize<LyricGet>(getJson);
                    var lrc = get?.syncedLyrics ?? "";
                    
                    if (string.IsNullOrWhiteSpace(lrc)) continue;

                    // Check if lyrics are mostly CJK (>30% CJK characters)
                    if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(lrc)) continue;

                    var parsed = ParseLrc(lrc);
                    if (parsed.Count > 0)
                    {
                        results.Add(new LyricsResult
                        {
                            Lines = parsed,
                            Source = "lrclib",
                            Artist = result.artistName ?? artist,
                            Title = result.trackName ?? title,
                            Album = result.albumName ?? "",
                            IsEstimated = false,
                            IsPlain = false
                        });
                    }
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Get plain lyrics with estimated timing (fallback when no synced lyrics found)
        /// </summary>
        public static List<LyricsResult> GetPlainLyrics(string title, string artist, int durationMs = 0)
        {
            var results = new List<LyricsResult>();
            
            try
            {
                using var wc = new WebClient();
                wc.Headers[HttpRequestHeader.UserAgent] = "OpenMediaBridge";

                var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title ?? string.Empty)}&artist_name={Uri.EscapeDataString(artist ?? string.Empty)}";

                var json = wc.DownloadString(url);
                var searchResults = JsonSerializer.Deserialize<List<SearchResult>>(json) ?? new List<SearchResult>();
                
                foreach (var result in searchResults)
                {
                    // Skip instrumental tracks
                    if (result.instrumental) continue;

                    var getJson = wc.DownloadString($"https://lrclib.net/api/get/{result.id}");
                    var get = JsonSerializer.Deserialize<LyricGet>(getJson);
                    
                    // Only get plain lyrics (skip if synced exists)
                    var plain = get?.plainLyrics ?? "";
                    if (string.IsNullOrWhiteSpace(plain)) continue;
                    if (!string.IsNullOrWhiteSpace(get?.syncedLyrics)) continue; // Has synced, skip

                    // Check if lyrics are mostly CJK
                    if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(plain)) continue;

                    // Use duration from result or parameter
                    int songDurationMs = durationMs > 0 ? durationMs : (int)(result.duration * 1000);
                    
                    var parsed = ParsePlainWithTiming(plain, songDurationMs);
                    if (parsed.Count > 0)
                    {
                        results.Add(new LyricsResult
                        {
                            Lines = parsed,
                            Source = "lrclib",
                            Artist = result.artistName ?? artist,
                            Title = result.trackName ?? title,
                            Album = result.albumName ?? "",
                            IsEstimated = true,
                            IsPlain = true
                        });
                    }
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Original single-result method for backwards compatibility
        /// </summary>
        public static List<LyricsLine> GetLyrics(string title, string artist, int durationMs = 0)
        {
            var allResults = GetAllLyrics(title, artist, durationMs);
            return allResults.FirstOrDefault()?.Lines ?? new List<LyricsLine>();
        }

        /// <summary>
        /// Parse plain text lyrics and estimate timing based on character count
        /// </summary>
        private static List<LyricsLine> ParsePlainWithTiming(string plain, int durationMs)
        {
            var result = new List<LyricsLine>();
            var lines = (plain ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            // Filter out empty lines and section markers
            var validLines = lines
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l => !IsSectionMarker(l))
                .ToList();

            if (validLines.Count == 0) return result;

            // Calculate character weights for timing
            // Longer lines get more time
            var charCounts = validLines.Select(l => Math.Max(1, l.Length)).ToList();
            int totalChars = charCounts.Sum();

            // Reserve some time at start and end (5% each)
            int startBufferMs = (int)(durationMs * 0.05);
            int endBufferMs = (int)(durationMs * 0.10);
            int availableMs = durationMs - startBufferMs - endBufferMs;
            
            if (availableMs < 1000) availableMs = durationMs; // Fallback for very short songs

            // Minimum and maximum time per line
            const int MinLineMs = 1500;
            const int MaxLineMs = 8000;
            const int GapMs = 200; // Gap between lines

            long currentTime = startBufferMs;
            
            for (int i = 0; i < validLines.Count; i++)
            {
                // Calculate time based on character weight
                double weight = (double)charCounts[i] / totalChars;
                int lineMs = (int)(availableMs * weight);
                
                // Clamp to reasonable bounds
                lineMs = Math.Clamp(lineMs, MinLineMs, MaxLineMs);

                result.Add(new LyricsLine
                {
                    Time = currentTime,
                    Text = validLines[i]
                });

                currentTime += lineMs + GapMs;
                
                // Don't exceed song duration
                if (currentTime > durationMs - 500) break;
            }

            return result;
        }

        private static bool IsSectionMarker(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;
            
            var lower = line.ToLowerInvariant().Trim();
            
            // Common section markers
            string[] markers = {
                "verse", "chorus", "bridge", "outro", "intro", "hook",
                "pre-chorus", "prechorus", "refrain", "interlude",
                "instrumental", "solo", "breakdown"
            };

            // Check if line is just a section marker (with optional number)
            foreach (var marker in markers)
            {
                if (lower == marker) return true;
                if (lower.StartsWith(marker + " ")) return true;
                if (lower.StartsWith("[") && lower.Contains(marker)) return true;
            }

            // Check for [Section] style markers
            if (lower.StartsWith("[") && lower.EndsWith("]")) return true;

            return false;
        }

        private static bool IsMostlyCJK(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            int totalChars = 0;
            int cjkChars = 0;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) || c > 127)
                {
                    totalChars++;
                    if ((c >= 0x4E00 && c <= 0x9FFF) ||  // Chinese
                        (c >= 0x3040 && c <= 0x309F) ||  // Hiragana
                        (c >= 0x30A0 && c <= 0x30FF) ||  // Katakana
                        (c >= 0xAC00 && c <= 0xD7AF))    // Korean
                    {
                        cjkChars++;
                    }
                }
            }

            return totalChars > 0 && ((double)cjkChars / totalChars) > 0.3;
        }

        private static List<LyricsLine> ParseLrc(string lrc)
        {
            var result = new List<LyricsLine>();
            var lines = (lrc ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");
            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                int min = int.Parse(match.Groups[1].Value);
                int sec = int.Parse(match.Groups[2].Value);
                int ms = int.Parse(match.Groups[3].Value.PadRight(3, '0'));
                string text = match.Groups[4].Value.Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                result.Add(new LyricsLine
                {
                    Time = min * 60000 + sec * 1000 + ms,
                    Text = text
                });
            }

            return result.OrderBy(l => l.Time).ToList();
        }
    }
}
