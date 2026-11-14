using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public static class LocalDatabaseFetcher
    {
        private static string _databasePath = null;
        private static bool _databaseExists = false;
        private static SqliteConnection _connection = null;
        private static DatabaseIndex _index = new DatabaseIndex();

        public static void Initialize(string databasePath)
        {
            _databasePath = databasePath;
            _databaseExists = !string.IsNullOrEmpty(databasePath) && File.Exists(databasePath);

            if (_databaseExists)
            {
                try
                {
                    // Check if index exists
                    string indexPath = DatabaseIndex.GetIndexPath(databasePath);

                    if (!File.Exists(indexPath))
                    {
                        Console.WriteLine($"[LocalDB] Index not found. Building index (this will take a while)...");
                        Console.WriteLine($"[LocalDB] This only needs to be done once!");
                        DatabaseIndex.BuildIndex(databasePath, indexPath);
                    }

                    // Load index into RAM
                    if (!_index.LoadIndex(indexPath))
                    {
                        Console.WriteLine($"[LocalDB] Failed to load index, database will not be used");
                        _databaseExists = false;
                        return;
                    }

                    // Open database connection for lyrics retrieval
                    var connectionString = new SqliteConnectionStringBuilder
                    {
                        DataSource = _databasePath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Shared,
                    }.ToString();

                    _connection = new SqliteConnection(connectionString);
                    _connection.Open();

                    // Set pragmas for better read performance (read-only safe)
                    using (var pragmaCmd = _connection.CreateCommand())
                    {
                        pragmaCmd.CommandText = @"
                            PRAGMA temp_store = MEMORY;
                            PRAGMA mmap_size = 268435456;
                            PRAGMA cache_size = -64000;
                        ";
                        pragmaCmd.ExecuteNonQuery();
                    }

                    Console.WriteLine($"[LocalDB] Database ready with in-memory index");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalDB] Failed to initialize: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"[LocalDB] Inner exception: {ex.InnerException.Message}");
                    }
                    _databaseExists = false;
                    Cleanup();
                }
            }
            else
            {
                Console.WriteLine($"[LocalDB] Database not found at: {databasePath}");
            }
        }

        public static bool IsAvailable()
        {
            return _databaseExists && _connection != null && _index.IsLoaded;
        }

        public static List<LyricsLine> GetLyrics(string title, string artist)
        {
            if (!IsAvailable())
                return null;

            try
            {
                // Step 1: Check in-memory index (instant!)
                if (!_index.TryGetLyricsId(artist, title, out long lyricsId))
                {
                    return new List<LyricsLine>(); // Not found in index
                }

                // Step 2: Fetch only the lyrics text by ID (minimal disk access)
                using var lyricsCmd = _connection.CreateCommand();
                lyricsCmd.CommandText = "SELECT synced_lyrics, plain_lyrics FROM lyrics WHERE id = $id";
                lyricsCmd.Parameters.AddWithValue("$id", lyricsId);

                using var lyricsReader = lyricsCmd.ExecuteReader();
                if (lyricsReader.Read())
                {
                    string syncedLyrics = lyricsReader.IsDBNull(0) ? null : lyricsReader.GetString(0);
                    string plainLyrics = lyricsReader.IsDBNull(1) ? null : lyricsReader.GetString(1);

                    var lrc = syncedLyrics ?? plainLyrics ?? "";
                    if (string.IsNullOrWhiteSpace(lrc))
                    {
                        return new List<LyricsLine>();
                    }

                    // Check CJK filter
                    if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(lrc))
                    {
                        return new List<LyricsLine>();
                    }

                    return ParseLrc(lrc);
                }

                return new List<LyricsLine>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalDB] Query error: {ex.Message}");
                return null;
            }
        }

        public static void Cleanup()
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }

        /// <summary>
        /// Get all matching lyrics from local database (for scoring/selection)
        /// </summary>
        public static List<LyricsResult> GetAllLyrics(string title, string artist, int durationMs = 0)
        {
            var results = new List<LyricsResult>();
            
            if (!_databaseExists || _connection == null) return results;

            try
            {
                // Get all matching IDs from index
                var matchingIds = _index.FindAllMatches(title, artist);
                
                foreach (var (id, matchTitle, matchArtist, duration) in matchingIds.Take(10)) // Limit to 10 results
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "SELECT synced_lyrics, plain_lyrics FROM lyrics WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);

                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        string syncedLyrics = reader.IsDBNull(0) ? null : reader.GetString(0);
                        string plainLyrics = reader.IsDBNull(1) ? null : reader.GetString(1);

                        var lrc = syncedLyrics ?? "";
                        bool isPlain = string.IsNullOrWhiteSpace(syncedLyrics);
                        if (isPlain) lrc = plainLyrics ?? "";
                        
                        if (string.IsNullOrWhiteSpace(lrc)) continue;

                        // Check CJK filter
                        if (LyricsFetcher.FilterCjkLyrics && IsMostlyCJK(lrc)) continue;

                        var lines = ParseLrc(lrc);
                        if (lines.Count == 0) continue;

                        results.Add(new LyricsResult
                        {
                            Lines = lines,
                            Source = "localdb",
                            Title = matchTitle,
                            Artist = matchArtist,
                            IsPlain = isPlain
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalDB] GetAllLyrics error: {ex.Message}");
            }

            return results;
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
