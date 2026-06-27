using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public class DatabaseIndex
    {
        // Key: "artist|title" (lowercase), Value: lyrics_id
        private Dictionary<string, long> _index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded => _index.Count > 0;

        public static string GetIndexPath(string databasePath)
        {
            return Path.ChangeExtension(databasePath, ".index");
        }

        // Build index from database (run once, takes a while)
        public static void BuildIndex(string databasePath, string indexPath)
        {
            Console.WriteLine($"[Index] Building index from database...");
            var startTime = DateTime.UtcNow;

            var index = new Dictionary<string, long>();

            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT t.name_lower, t.artist_name_lower, l.id, l.instrumental
                FROM lyrics l
                INNER JOIN tracks t ON l.track_id = t.id
                WHERE l.has_synced_lyrics = 1 AND l.instrumental = 0
            ";

            int count = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string title = reader.GetString(0);
                string artist = reader.GetString(1);
                long lyricsId = reader.GetInt64(2);

                string key = $"{artist}|{title}";
                if (!index.ContainsKey(key))
                {
                    index[key] = lyricsId;
                    count++;

                    if (count % 100000 == 0)
                    {
                        Console.WriteLine($"[Index] Processed {count:N0} tracks...");
                    }
                }
            }

            // Save to file
            var indexData = new IndexData
            {
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                TotalEntries = index.Count,
                Entries = index
            };

            var json = JsonSerializer.Serialize(indexData);
            File.WriteAllText(indexPath, json);

            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            Console.WriteLine($"[Index] Built index with {count:N0} entries in {elapsed:F1}s");
            Console.WriteLine($"[Index] Saved to: {indexPath}");
        }

        // Load index from file (fast)
        public bool LoadIndex(string indexPath)
        {
            try
            {
                if (!File.Exists(indexPath))
                {
                    Console.WriteLine($"[Index] Index file not found: {indexPath}");
                    return false;
                }

                Console.WriteLine($"[Index] Loading index from: {indexPath}");
                var startTime = DateTime.UtcNow;

                var json = File.ReadAllText(indexPath);
                var indexData = JsonSerializer.Deserialize<IndexData>(json);

                if (indexData == null)
                {
                    Console.WriteLine("[Index] Failed to deserialize index");
                    return false;
                }

                _index = indexData.Entries ?? new Dictionary<string, long>();

                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                Console.WriteLine($"[Index] Loaded {_index.Count:N0} entries in {elapsed:F0}ms");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Index] Error loading index: {ex.Message}");
                return false;
            }
        }

        public bool TryGetLyricsId(string artist, string title, out long lyricsId)
        {
            string key = $"{artist?.ToLowerInvariant()}|{title?.ToLowerInvariant()}";
            return _index.TryGetValue(key, out lyricsId);
        }

        /// <summary>
        /// Find all matching entries based on partial matches
        /// Returns list of (id, title, artist, duration) tuples
        /// </summary>
        public List<(long id, string title, string artist, double duration)> FindAllMatches(string title, string artist)
        {
            var results = new List<(long id, string title, string artist, double duration)>();
            
            var searchTitle = title?.ToLowerInvariant()?.Trim() ?? "";
            var searchArtist = artist?.ToLowerInvariant()?.Trim() ?? "";
            
            // Need at least 3 chars to search
            if (searchTitle.Length < 3) return results;
            
            // First try exact match
            string exactKey = $"{searchArtist}|{searchTitle}";
            if (_index.TryGetValue(exactKey, out var exactId))
            {
                results.Add((exactId, title, artist, 0));
            }
            
            // Then try partial matches (limit search for performance)
            int count = 0;
            foreach (var kvp in _index)
            {
                if (count >= 20) break; // Limit to 20 partial matches
                
                var parts = kvp.Key.Split('|');
                if (parts.Length != 2) continue;
                
                var indexArtist = parts[0];
                var indexTitle = parts[1];
                
                // Skip exact match (already added)
                if (indexArtist == searchArtist && indexTitle == searchTitle) continue;
                
                // Require stronger matches - title must be very similar
                bool titleMatch = false;
                
                // Exact title match (different artist)
                if (indexTitle == searchTitle)
                {
                    titleMatch = true;
                }
                // Search title contains full index title (or vice versa) but must be substantial
                else if (searchTitle.Length >= 5 && indexTitle.Length >= 5)
                {
                    if (indexTitle.Contains(searchTitle) || searchTitle.Contains(indexTitle))
                    {
                        titleMatch = true;
                    }
                }
                
                // Artist must also match reasonably
                bool artistMatch = false;
                if (!string.IsNullOrEmpty(searchArtist) && searchArtist.Length >= 2)
                {
                    artistMatch = indexArtist == searchArtist || 
                                  indexArtist.Contains(searchArtist) || 
                                  searchArtist.Contains(indexArtist);
                }
                
                if (titleMatch && artistMatch)
                {
                    results.Add((kvp.Value, indexTitle, indexArtist, 0));
                    count++;
                }
            }
            
            return results;
        }

        private class IndexData
        {
            public int Version { get; set; }
            public DateTime CreatedAt { get; set; }
            public int TotalEntries { get; set; }
            public Dictionary<string, long> Entries { get; set; }
        }
    }
}
