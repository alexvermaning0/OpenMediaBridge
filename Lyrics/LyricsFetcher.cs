using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenMediaBridge.Lyrics.Fetchers
{
    public class LyricsLine
    {
        public long Time { get; set; }
        public string Text { get; set; }
    }

    public class LyricsResult
    {
        public List<LyricsLine> Lines { get; set; } = new();
        public string Source { get; set; } = "None";
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public bool IsEstimated { get; set; } = false;
        public bool IsPlain { get; set; } = false;
    }

    public class LyricsFetcher
    {
        private List<LyricsResult> _allResults = new();
        private int _currentIndex = 0;
        private string _currentTitle = "";
        private string _currentArtist = "";
        public string CurrentSource { get; private set; } = "None";

        // set from LyricsService
        public static string CacheFolder { get; set; } = "cache";
        public static bool FilterCjkLyrics { get; set; } = true;
        public static bool OfflineMode { get; set; } = false;
        public static bool PlainLyricsFallback { get; set; } = false;

        // Logging callback
        private static Action<string> _logCallback;
        public static void SetLogCallback(Action<string> callback)
        {
            _logCallback = callback;
            // Also set on fetchers
            LRCLibFetcher.DebugLog = callback;
            NetEaseFetcher.DebugLog = callback;
        }

        private static void Log(string message)
        {
            _logCallback?.Invoke(message);
        }

        public int CurrentIndex => _currentIndex;
        public int TotalResults => _allResults.Count;
        public bool HasMultipleResults => _allResults.Count > 1;

        public bool NeedsNewSong(string title, string artist)
        {
            return !string.Equals(title ?? "", _currentTitle ?? "", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(artist ?? "", _currentArtist ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Force a refetch by clearing internal state
        /// </summary>
        public void ForceRefetch()
        {
            _currentTitle = "";
            _currentArtist = "";
            _allResults.Clear();
            _currentIndex = 0;
            CurrentSource = "None";
        }

        public void NextLyrics()
        {
            if (_allResults.Count <= 1) return;
            
            _currentIndex = (_currentIndex + 1) % _allResults.Count;
            var result = _allResults[_currentIndex];
            CurrentSource = FormatSource(result);
            Log($"Switched to lyrics {_currentIndex + 1}/{_allResults.Count}: {result.Source}");
        }

        public void PreviousLyrics()
        {
            if (_allResults.Count <= 1) return;
            
            _currentIndex = (_currentIndex - 1 + _allResults.Count) % _allResults.Count;
            var result = _allResults[_currentIndex];
            CurrentSource = FormatSource(result);
            Log($"Switched to lyrics {_currentIndex + 1}/{_allResults.Count}: {result.Source}");
        }

        private string FormatSource(LyricsResult result)
        {
            string source = result.Source;
            if (result.IsEstimated)
                source += " (estimated)";
            else if (result.IsPlain)
                source += " (plain)";
            return source;
        }

        // Track current fetch to allow cancellation
        private int _fetchId = 0;

        public void FetchLyrics(string title, string artist, int durationMs = 0, bool isBrowser = false)
        {
            // Store what we're fetching for - used to validate results
            var fetchTitle = title ?? "";
            var fetchArtist = artist ?? "";
            
            _currentTitle = fetchTitle;
            _currentArtist = fetchArtist;
            _allResults = new List<LyricsResult>();
            _currentIndex = 0;
            CurrentSource = "None";
            
            // Increment fetch ID to invalidate any ongoing fetches
            var thisFetchId = ++_fetchId;

            // Higher threshold for normal apps, lower for browsers (YouTube titles are messy)
            int minScoreThreshold = isBrowser ? 200 : 800;
            
            // Check if this fetch is still valid
            bool IsStillValid() => thisFetchId == _fetchId && 
                                   fetchTitle == _currentTitle && 
                                   fetchArtist == _currentArtist;

            void AddResult(LyricsResult result)
            {
                // Check if song changed during fetch
                if (!IsStillValid()) return;
                
                // Check for empty fingerprint
                var fingerprint = GetLyricsFingerprint(result.Lines);
                if (string.IsNullOrEmpty(fingerprint)) return;
                
                // Calculate score first
                int newScore = ScoreResult(result, fetchTitle, fetchArtist, durationMs);
                
                // Reject very low-scoring results (except cache which was previously verified)
                if (result.Source != "cache" && result.Source != "localdb" && newScore < minScoreThreshold)
                {
                    Log($"✗ Rejected: {result.Source} (score: {newScore} < {minScoreThreshold})");
                    return;
                }
                
                // Check for duplicates
                foreach (var existing in _allResults)
                {
                    var existingFp = GetLyricsFingerprint(existing.Lines);
                    if (fingerprint == existingFp) return;
                }
                
                // Add to results
                _allResults.Add(result);
                
                // If this is the first result or better than current, use it
                if (_allResults.Count == 1)
                {
                    _currentIndex = 0;
                    CurrentSource = FormatSource(result);
                    Log($"✓ Using: {result.Source} (score: {newScore})");
                }
                else
                {
                    // Check if new result is better than current
                    int currentScore = ScoreResult(_allResults[_currentIndex], fetchTitle, fetchArtist, durationMs);
                    if (newScore > currentScore)
                    {
                        _currentIndex = _allResults.Count - 1;
                        CurrentSource = FormatSource(result);
                        Log($"✓ Better match: {result.Source} (score: {newScore} > {currentScore})");
                    }
                    else
                    {
                        Log($"✓ Found alt: {result.Source} (score: {newScore})");
                    }
                }
            }

            // 1) cache - highest priority, use immediately
            Log("Trying: Cache");
            if (CacheHelper.TryLoad(CacheFolder, fetchArtist, fetchTitle, out var cached))
            {
                var cacheResult = new LyricsResult
                {
                    Lines = cached,
                    Source = "cache",
                    Artist = fetchArtist,
                    Title = fetchTitle
                };
                AddResult(cacheResult);
            }
            else
            {
                Log("✗ Not in cache");
            }

            // 2) local database
            if (IsStillValid() && LocalDatabaseFetcher.IsAvailable())
            {
                Log("Trying: Local Database");
                var localResults = LocalDatabaseFetcher.GetAllLyrics(fetchTitle, fetchArtist, durationMs);
                foreach (var localResult in localResults.Where(r => r.Lines.Count > 0))
                {
                    if (!IsStillValid()) break;
                    AddResult(localResult);
                }
                
                if (localResults.Count == 0)
                    Log("✗ Local DB found nothing");
            }

            // If offline mode, skip online sources
            if (OfflineMode)
            {
                Log("Offline mode - skipping online sources");
            }
            else if (IsStillValid())
            {
                // 3) lrclib API - get all synced results
                Log("Trying: LRCLib API");
                var lrclibResults = LRCLibFetcher.GetAllLyrics(fetchTitle, fetchArtist, durationMs);
                foreach (var lrcResult in lrclibResults.Where(r => r.Lines.Count > 0))
                {
                    if (!IsStillValid()) break;
                    AddResult(lrcResult);
                }
                
                if (lrclibResults.Count == 0)
                    Log("✗ LRCLib found nothing");

                // 4) netease - only if still valid
                if (IsStillValid())
                {
                    Log("Trying: NetEase");
                    var netease = NetEaseFetcher.GetLyrics(fetchTitle, fetchArtist);
                    if (netease != null && netease.Count > 0)
                    {
                        var result = new LyricsResult
                        {
                            Lines = netease,
                            Source = "netease",
                            Artist = fetchArtist,
                            Title = fetchTitle
                        };
                        AddResult(result);
                    }
                    else
                    {
                        Log("✗ NetEase found nothing");
                    }
                }

                // 5) Plain lyrics fallback (if enabled and still valid)
                if (IsStillValid() && PlainLyricsFallback)
                {
                    Log("Trying: Plain lyrics fallback");
                    var plainResults = LRCLibFetcher.GetPlainLyrics(fetchTitle, fetchArtist, durationMs);
                    foreach (var plainResult in plainResults.Where(r => r.Lines.Count > 0))
                    {
                        if (!IsStillValid()) break;
                        AddResult(plainResult);
                    }
                }
            }

            // Check if fetch was cancelled
            if (!IsStillValid())
            {
                Log("Fetch cancelled - song changed");
                return;
            }

            // Final summary
            if (_allResults.Count > 1)
            {
                Log($"Found {_allResults.Count} sources - press N to cycle");
            }
            else if (_allResults.Count == 0)
            {
                CurrentSource = "None";
                Log("✗ No lyrics found");
            }
            
            // Cache the best result if not from cache
            if (_allResults.Count > 0 && _allResults[_currentIndex].Source != "cache")
            {
                CacheHelper.Save(CacheFolder, fetchArtist, fetchTitle, 
                    _allResults[_currentIndex].Lines, _allResults[_currentIndex].Source);
            }
        }

        private int ScoreResult(LyricsResult result, string targetTitle, string targetArtist, int targetDurationMs)
        {
            int score = 0;
            
            // Source priority (keeps order when other scores are equal)
            // cache > localdb > lrclib > netease > plain
            switch (result.Source?.ToLowerInvariant())
            {
                case "cache": score += 50; break;
                case "localdb": score += 40; break;
                case "lrclib": score += 30; break;
                case "lrclib (local)": score += 40; break;
                case "netease": score += 20; break;
                default: score += 10; break;
            }
            
            // Title similarity (0-1000 points)
            score += (int)(GetSimilarity(result.Title ?? "", targetTitle) * 1000);
            
            // Artist similarity (0-500 points)
            score += (int)(GetSimilarity(result.Artist ?? "", targetArtist) * 500);
            
            // Duration match (0-500 points) - if we have duration info
            if (targetDurationMs > 0 && result.Lines.Count > 0)
            {
                var lastLineTime = result.Lines.LastOrDefault()?.Time ?? 0;
                if (lastLineTime > 0)
                {
                    // Calculate how close the lyrics duration is to target
                    double durationRatio = Math.Min(lastLineTime, targetDurationMs) / 
                                          (double)Math.Max(lastLineTime, targetDurationMs);
                    score += (int)(durationRatio * 500);
                }
            }
            
            // Bonus for having more lines (suggests more complete lyrics)
            score += Math.Min(result.Lines.Count, 100);
            
            // Penalty for estimated/plain lyrics
            if (result.IsEstimated) score -= 200;
            if (result.IsPlain) score -= 300;
            
            return score;
        }

        private double GetSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            
            a = a.ToLowerInvariant().Trim();
            b = b.ToLowerInvariant().Trim();
            
            if (a == b) return 1.0;
            if (a.Contains(b) || b.Contains(a)) return 0.8;
            
            // Simple word overlap score
            var wordsA = a.Split(new[] { ' ', '-', '_', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            var wordsB = b.Split(new[] { ' ', '-', '_', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (wordsA.Length == 0 || wordsB.Length == 0) return 0;
            
            int matches = wordsA.Count(wa => wordsB.Any(wb => wb.Contains(wa) || wa.Contains(wb)));
            return matches / (double)Math.Max(wordsA.Length, wordsB.Length);
        }

        private string GetLyricsFingerprint(List<LyricsLine> lines)
        {
            if (lines == null || lines.Count == 0) return "";
            
            // Use first 5 non-empty lines as fingerprint
            var texts = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .Take(5)
                .Select(l => l.Text.ToLowerInvariant().Trim());
            
            return string.Join("|", texts);
        }

        private static bool IsCJKChar(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) ||  // CJK Unified Ideographs (Chinese)
                   (c >= 0x3040 && c <= 0x309F) ||  // Hiragana (Japanese)
                   (c >= 0x30A0 && c <= 0x30FF) ||  // Katakana (Japanese)
                   (c >= 0xAC00 && c <= 0xD7AF);    // Hangul (Korean)
        }

        private static List<string> TokenizeForWordSync(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            
            // Check if text contains CJK characters
            bool hasCJK = text.Any(IsCJKChar);
            
            if (!hasCJK)
            {
                // Normal space-based tokenization for non-CJK
                return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            
            // For CJK: split each character individually
            var tokens = new List<string>();
            var currentToken = new System.Text.StringBuilder();
            
            foreach (char c in text)
            {
                bool isCJK = IsCJKChar(c);
                bool isSpace = char.IsWhiteSpace(c);
                
                if (isSpace)
                {
                    // Flush any pending token
                    if (currentToken.Length > 0)
                    {
                        tokens.Add(currentToken.ToString());
                        currentToken.Clear();
                    }
                }
                else if (isCJK)
                {
                    // Flush any pending non-CJK token
                    if (currentToken.Length > 0)
                    {
                        tokens.Add(currentToken.ToString());
                        currentToken.Clear();
                    }
                    // Each CJK character is its own token
                    tokens.Add(c.ToString());
                }
                else
                {
                    // Non-CJK, non-space: accumulate (for mixed content like romaji)
                    currentToken.Append(c);
                }
            }
            
            // Flush final token
            if (currentToken.Length > 0)
                tokens.Add(currentToken.ToString());
            
            return tokens;
        }

        private bool IsDuplicateResult(LyricsResult newResult)
        {
            foreach (var existing in _allResults)
            {
                // Compare first few lines to detect duplicates
                if (existing.Lines.Count > 0 && newResult.Lines.Count > 0)
                {
                    int compareCount = Math.Min(3, Math.Min(existing.Lines.Count, newResult.Lines.Count));
                    bool same = true;
                    for (int i = 0; i < compareCount; i++)
                    {
                        if (existing.Lines[i].Text != newResult.Lines[i].Text)
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same) return true;
                }
            }
            return false;
        }

        private List<LyricsLine> GetCurrentLines()
        {
            if (_allResults.Count == 0) return new List<LyricsLine>();
            return _allResults[_currentIndex].Lines;
        }

        public string GetCurrentLine(long positionMs)
        {
            var lines = GetCurrentLines();
            if (lines == null || lines.Count == 0) return "";
            int idx = lines.FindLastIndex(l => l.Time <= positionMs);
            if (idx < 0) return "";
            return lines[idx].Text;
        }

        // Improved per-word sync with punctuation pauses + long-pause handling
        public string GetCurrentLineWordSync(long positionMs)
        {
            var _lyrics = GetCurrentLines();
            if (_lyrics == null || _lyrics.Count == 0) return "";

            int idx = _lyrics.FindLastIndex(l => l.Time <= positionMs);
            if (idx < 0 || idx >= _lyrics.Count - 1) return "";

            var current = _lyrics[idx];
            var next = _lyrics[idx + 1];

            long interval = next.Time - current.Time;
            if (interval <= 0) return "";

            // ---- Tunables ----
            const int LongPauseThresholdMs = 2000;
            const double SpokenPortionCap = 0.75;
            const int MaxWordMs = 500;
            const int MinWordMs = 120;

            // punctuation pauses
            const int CommaPauseMs = 150;
            const int MidPauseMs = 180;
            const int FullStopPauseMs = 250;

            // ---- Tokenize & weight ----
            var text = current.Text ?? "";
            var rawTokens = TokenizeForWordSync(text);
            if (rawTokens.Count == 0) return "";

            int WeightOf(string s)
            {
                if (string.IsNullOrEmpty(s)) return 1;
                int w = 0;
                foreach (var ch in s)
                    if (char.IsLetterOrDigit(ch)) w++;
                return Math.Max(1, w);
            }

            var weights = rawTokens.Select(WeightOf).ToArray();
            int totalWeight = Math.Max(1, weights.Sum());

            // ---- Allowed content window ----
            long allowedContentMs;
            if (interval >= LongPauseThresholdMs)
            {
                long capByPortion = (long)(interval * SpokenPortionCap);
                long capByPerToken = (long)rawTokens.Count * MaxWordMs;
                allowedContentMs = Math.Min(capByPortion, capByPerToken);
                allowedContentMs = Math.Max(allowedContentMs, Math.Min(interval, rawTokens.Count * MinWordMs));
            }
            else
            {
                allowedContentMs = interval;
            }

            // ---- Initial highlight durations ----
            var idealDurations = new double[rawTokens.Count];
            for (int i = 0; i < rawTokens.Count; i++)
                idealDurations[i] = allowedContentMs * (weights[i] / (double)totalWeight);

            var highlightDur = new double[rawTokens.Count];
            for (int i = 0; i < rawTokens.Count; i++)
                highlightDur[i] = Math.Clamp(idealDurations[i], MinWordMs, MaxWordMs);

            // ---- Pause after punctuation ----
            int PauseForToken(string token)
            {
                if (string.IsNullOrEmpty(token)) return 0;
                char last = token[token.Length - 1];
                if (last == ',') return CommaPauseMs;
                if (last == ';' || last == ':') return MidPauseMs;
                if (last == '.' || last == '!' || last == '?') return FullStopPauseMs;
                return 0;
            }

            var pauseAfter = new int[rawTokens.Count];
            for (int i = 0; i < rawTokens.Count; i++)
                pauseAfter[i] = PauseForToken(rawTokens[i]);

            double baseSum = highlightDur.Sum();
            int pauseSum = pauseAfter.Sum();
            double totalWithPauses = baseSum + pauseSum;

            if (totalWithPauses > allowedContentMs && baseSum > 0)
            {
                double scale = (allowedContentMs - pauseSum) / baseSum;
                scale = Math.Clamp(scale, 0.2, 1.0);
                for (int i = 0; i < highlightDur.Length; i++)
                    highlightDur[i] *= scale;

                baseSum = highlightDur.Sum();
                totalWithPauses = baseSum + pauseSum;
            }

            // ---- Build cumulative segments ----
            var segEnds = new double[rawTokens.Count * 2];
            int seg = 0;
            double acc = 0;
            for (int i = 0; i < rawTokens.Count; i++)
            {
                acc += highlightDur[i];
                segEnds[seg++] = acc;
                acc += pauseAfter[i];
                segEnds[seg++] = acc;
            }

            // ---- Decide what to show ----
            long elapsed = positionMs - current.Time;
            if (elapsed < 0) return "";

            if (elapsed >= (long)totalWithPauses)
                return "";

            int segIndex = Array.FindIndex(segEnds, end => elapsed <= end);
            if (segIndex < 0) segIndex = segEnds.Length - 1;

            int tokenIndex;
            if ((segIndex % 2) == 1)
            {
                // During pause - keep the previous word highlighted
                tokenIndex = segIndex / 2;
            }
            else
            {
                // During word highlight
                tokenIndex = segIndex / 2;
            }
            
            if (tokenIndex >= rawTokens.Count) tokenIndex = rawTokens.Count - 1;

            var rebuilt = new List<string>(rawTokens.Count);
            for (int i = 0; i < rawTokens.Count; i++)
            {
                if (i == tokenIndex)
                    rebuilt.Add("<color=yellow>" + rawTokens[i] + "</color>");
                else
                    rebuilt.Add(rawTokens[i]);
            }
            return string.Join(" ", rebuilt);
        }

        public long GetSongLength()
        {
            var lines = GetCurrentLines();
            return lines?.LastOrDefault()?.Time ?? 0;
        }

        public void ClearCache(string title, string artist)
        {
            try
            {
                CacheHelper.ClearCache(title, artist);
            }
            catch { }
        }

        public string GetFullLyricsText()
        {
            var lines = GetCurrentLines();
            if (lines == null || lines.Count == 0)
                return "";
            
            return string.Join("\n", lines.Select(l => l.Text));
        }
    }
}
