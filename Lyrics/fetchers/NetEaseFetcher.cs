using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public static class NetEaseFetcher
    {
        // Debug logging action - set by LyricsFetcher
        public static Action<string> DebugLog { get; set; }

        public static List<LyricsLine> GetLyrics(string title, string artist)
        {
            try
            {
                var searchQuery = $"{title}-{artist}";
                DebugLog?.Invoke($"  NetEase query: \"{searchQuery}\"");

                using var wc = new WebClient();
                wc.Headers[HttpRequestHeader.Referer] = "https://music.163.com";
                wc.Headers.Add("Cookie", "appver=2.0.2");

                string query = wc.DownloadString($"https://music.163.com/api/search/get?s={Uri.EscapeDataString(searchQuery)}&type=1&limit=1");
                var idMatch = Regex.Match(query, "\"id\":(\\d+)");
                if (!idMatch.Success) 
                {
                    DebugLog?.Invoke($"  NetEase: no results");
                    return null;
                }

                string id = idMatch.Groups[1].Value;
                DebugLog?.Invoke($"  NetEase: found id={id}");
                
                string lyricsJson = wc.DownloadString($"https://music.163.com/api/song/lyric?os=pc&id={id}&lv=-1&kv=-1&tv=-1");

                var lrcMatch = Regex.Match(lyricsJson, "\"lyric\":\"(.*?)\"", RegexOptions.Singleline);
                if (!lrcMatch.Success) 
                {
                    DebugLog?.Invoke($"  NetEase: no lyrics for id={id}");
                    return null;
                }

                string decoded = WebUtility.HtmlDecode(lrcMatch.Groups[1].Value).Replace("\\n", "\n");

                // Check if lyrics are mostly CJK (>30% CJK characters)
                if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(decoded)) 
                {
                    DebugLog?.Invoke($"  NetEase: filtered (CJK)");
                    return null;
                }

                var parsed = ParseLRC(decoded);
                DebugLog?.Invoke($"  NetEase: got {parsed?.Count ?? 0} lines");
                return parsed;
            }
            catch (Exception ex)
            {
                DebugLog?.Invoke($"  NetEase error: {ex.Message}");
                return null;
            }
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

        // Not found phrases to filter out (using unicode escapes for compatibility)
        private static readonly string[] NotFoundPhrases = new[]
        {
            "\u7EAF\u97F3\u4E50\uFF0C\u8BF7\u6B23\u8D4F",           // 纯音乐，请欣赏
            "\u6B64\u6B4C\u66F2\u4E3A\u6CA1\u6709\u586B\u8BCD\u7684\u7EAF\u97F3\u4E50", // 此歌曲为没有填词的纯音乐
            "\u6682\u65F6\u6CA1\u6709\u6B4C\u8BCD",                 // 暂时没有歌词
            "\u6CA1\u6709\u627E\u5230\u6B4C\u8BCD",                 // 没有找到歌词
            "\u672A\u627E\u5230\u6B4C\u8BCD",                       // 未找到歌词
            "\u6682\u65E0\u6B4C\u8BCD",                             // 暂无歌词
        };

        public static List<LyricsLine> ParseLRC(string raw)
        {
            var result = new List<LyricsLine>();
            var lines = raw.Split('\n');
            var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})]");

            foreach (var line in lines)
            {
                var matches = regex.Matches(line);
                string text = regex.Replace(line, "").Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;
                
                // Skip "not found" messages
                if (NotFoundPhrases.Any(phrase => text.Contains(phrase))) continue;

                foreach (Match match in matches)
                {
                    int min = int.Parse(match.Groups[1].Value);
                    int sec = int.Parse(match.Groups[2].Value);
                    int ms = int.Parse(match.Groups[3].Value.PadRight(3, '0'));

                    result.Add(new LyricsLine
                    {
                        Time = min * 60000 + sec * 1000 + ms,
                        Text = text
                    });
                }
            }

            return result.OrderBy(l => l.Time).ToList();
        }
    }
}
