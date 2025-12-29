using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public static class CacheHelper
    {
        public static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        public static string ComputeHash(string input)
        {
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static string GetCachePath(string cacheFolder, string artist, string title)
        {
            Directory.CreateDirectory(cacheFolder);
            var readable = $"{Sanitize(artist)}-{Sanitize(title)}";
            var hash = ComputeHash(artist + "|" + title);
            return Path.Combine(cacheFolder, $"{readable}-{hash}.json");
        }

        public static bool TryLoad(string cacheFolder, string artist, string title, out List<LyricsLine> lines)
        {
            lines = null;
            try
            {
                var path = GetCachePath(cacheFolder, artist, title);
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                var payload = JsonSerializer.Deserialize<CachePayload>(json);
                lines = payload?.Lines ?? new List<LyricsLine>();
                return lines.Count > 0;
            }
            catch { }
            return false;
        }

        public static void Save(string cacheFolder, string artist, string title, List<LyricsLine> lines, string source)
        {
            try
            {
                var path = GetCachePath(cacheFolder, artist, title);
                var payload = new CachePayload
                {
                    Artist = artist,
                    Title = title,
                    Source = source,
                    SavedAt = DateTime.UtcNow,
                    Lines = lines?.OrderBy(l => l.Time).ToList() ?? new List<LyricsLine>()
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public static void ClearCache(string title, string artist)
        {
            try
            {
                var path = GetCachePath(LyricsFetcher.CacheFolder, artist, title);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        public class CachePayload
        {
            public string Artist { get; set; }
            public string Title { get; set; }
            public string Source { get; set; }
            public DateTime SavedAt { get; set; }
            public List<LyricsLine> Lines { get; set; }
        }
    }
}
