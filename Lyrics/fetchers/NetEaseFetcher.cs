using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public static class NetEaseFetcher
    {
        public static List<LyricsLine> GetLyrics(string title, string artist)
        {
            try
            {
                using var wc = new WebClient();
                wc.Headers[HttpRequestHeader.Referer] = "https://music.163.com";
                wc.Headers.Add("Cookie", "appver=2.0.2");

                string query = wc.DownloadString($"https://music.163.com/api/search/get?s={Uri.EscapeDataString(title + "-" + artist)}&type=1&limit=1");
                var idMatch = Regex.Match(query, "\"id\":(\\d+)");
                if (!idMatch.Success) return null;

                string id = idMatch.Groups[1].Value;
                string lyricsJson = wc.DownloadString($"https://music.163.com/api/song/lyric?os=pc&id={id}&lv=-1&kv=-1&tv=-1");

                var lrcMatch = Regex.Match(lyricsJson, "\"lyric\":\"(.*?)\"", RegexOptions.Singleline);
                if (!lrcMatch.Success) return null;

                string decoded = WebUtility.HtmlDecode(lrcMatch.Groups[1].Value).Replace("\\n", "\n");

                // Check if lyrics are mostly CJK (>30% CJK characters)
                if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(decoded)) return null;

                return ParseLRC(decoded);
            }
            catch
            {
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
