using OpenMediaBridge.Lyrics.Fetchers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Timers;
using Timer = System.Timers.Timer;
using Windows.Media.Control;

namespace OpenMediaBridge.Services
{
    public class LyricsService
    {
        private readonly WindowsMediaService _wmService;
        private readonly Timer _timer;
        private readonly LyricsFetcher _lyricsFetcher;
        private readonly List<string> _disabledSources;

        private string _currentLyric = "";
        private string _lastTitle = "";
        private string _lastArtist = "";
        private string _currentSource = "None";
        private DateTime _lastLyricUpdateTime = DateTime.MinValue;
        private DateTime _lastBroadcastTime = DateTime.MinValue;
        private readonly Queue<string> _debugLog = new();

        // position simulation
        private long _lastKnownPosition = 0;
        private DateTime _lastPositionUpdateTime = DateTime.MinValue;
        private bool _isPlaying = false;

        // modes & settings
        private bool _wordSyncMode = false;
        private bool _offlineMode = false;
        private bool _plainLyricsFallback = false;
        private bool _cjkFilter = true;

        // offset with visual feedback
        private int _currentOffset = 0;
        private DateTime _offsetChangedTime = DateTime.MinValue;
        private DateTime _offsetSavedTime = DateTime.MinValue;

        // multiple lyrics sources
        private int _currentLyricsIndex = 0;
        private int _totalLyricsAvailable = 1;

        // help menu
        private bool _showHelp = false;

        // console
        private bool _consoleInitialized = false;

        // quit signal
        public event Action OnQuitRequested;
        
        // Events for WebSocket updates
        public event Action<string, double> OnLyricUpdate;
        public event Action<string, string> OnStatusChanged;

        // Public properties for WebSocket access
        public string CurrentSource => _currentSource;
        public bool WordSyncEnabled => _wordSyncMode;
        public bool OfflineEnabled => _offlineMode;
        public bool CjkFilterEnabled => _cjkFilter;
        public bool PlainFallbackEnabled => _plainLyricsFallback;
        public int CurrentOffset => _currentOffset;

        public LyricsService(WindowsMediaService wmService)
        {
            _wmService = wmService;
            _lyricsFetcher = new LyricsFetcher();
            LyricsFetcher.CacheFolder = _wmService?.Config?.CacheFolder ?? "cache";
            LyricsFetcher.FilterCjkLyrics = _wmService?.Config?.FilterCjkLyrics ?? true;
            LyricsFetcher.OfflineMode = _wmService?.Config?.OfflineMode ?? false;
            LyricsFetcher.PlainLyricsFallback = _wmService?.Config?.PlainLyricsFallback ?? false;

            // Initialize state from config
            _currentOffset = _wmService?.Config?.OffsetMs ?? 0;
            _offlineMode = _wmService?.Config?.OfflineMode ?? false;
            _cjkFilter = _wmService?.Config?.FilterCjkLyrics ?? true;
            _plainLyricsFallback = _wmService?.Config?.PlainLyricsFallback ?? false;

            // Hook up the fetcher logging to our debug log
            LyricsFetcher.SetLogCallback(DebugLog);

            _disabledSources = wmService.Config?.DisableLyricsFor?
                .Select(x => x.ToLowerInvariant())
                .ToList() ?? new List<string>();

            _timer = new Timer(50); // Faster tick for responsive input
            _timer.Elapsed += Tick;
            _timer.Start();

            DebugLog("LyricsService initialized");
        }

        private void Tick(object sender, ElapsedEventArgs e)
        {
            // Handle keyboard input
            ProcessKeyboardInput();

            if (_showHelp) return;

            // Check if we have a valid session
            if (_wmService?.CurrentMediaSession == null || _wmService?.CurrentMediaProperties == null)
            {
                UpdateConsole("", "", 0);
                return;
            }

            var props = _wmService.CurrentMediaProperties;
            var title = props.Title ?? "";
            var artist = props.Artist ?? "";

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
            {
                UpdateConsole("", "", 0);
                return;
            }

            // Get timeline info
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo;
            
            try
            {
                timeline = _wmService.CurrentMediaSession.GetTimelineProperties();
                playbackInfo = _wmService.CurrentMediaSession.GetPlaybackInfo();
            }
            catch
            {
                return;
            }

            var smtcPos = (long)timeline.Position.TotalMilliseconds;
            var smtcPosWithOffset = smtcPos + _currentOffset;

            // Position simulation for smoother lyrics
            long simulatedPosition;
            if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                if (!_isPlaying)
                {
                    _isPlaying = true;
                    _lastKnownPosition = smtcPosWithOffset;
                    _lastPositionUpdateTime = DateTime.UtcNow;
                }
                else
                {
                    long difference = smtcPosWithOffset - _lastKnownPosition;
                    if (difference > 500 || (Math.Abs(difference) > 1500 && _lyricsFetcher.NeedsNewSong(title, artist)))
                    {
                        _lastKnownPosition = smtcPosWithOffset;
                        _lastPositionUpdateTime = DateTime.UtcNow;
                    }
                }
                simulatedPosition = _lastKnownPosition + (long)(DateTime.UtcNow - _lastPositionUpdateTime).TotalMilliseconds;
            }
            else
            {
                _isPlaying = false;
                _lastKnownPosition = smtcPosWithOffset;
                simulatedPosition = _lastKnownPosition;
            }

            // new song fetch?
            if (_lyricsFetcher.NeedsNewSong(title, artist))
            {
                DebugLog($"Fetching lyrics: {title} - {artist}");
                _lyricsFetcher.FetchLyrics(title, artist, (int)timeline.EndTime.TotalMilliseconds);
                _lastTitle = title;
                _lastArtist = artist;
                _currentLyric = "";
                _lastLyricUpdateTime = DateTime.MinValue;
            }
            else if (title != _lastTitle || artist != _lastArtist)
            {
                _lastTitle = title;
                _lastArtist = artist;
            }

            // Update source info from fetcher (may change progressively)
            var newSource = _lyricsFetcher.CurrentSource ?? "None";
            if (newSource != _currentSource)
            {
                _currentSource = newSource;
                _totalLyricsAvailable = _lyricsFetcher.TotalResults;
                _currentLyricsIndex = _lyricsFetcher.CurrentIndex;
                DebugLog($"Lyrics source: {_currentSource}");
                OnStatusChanged?.Invoke("lyricsrc", _currentSource);
            }

            bool shouldUpdate = false;
            string formattedLyric = "";

            if (_disabledSources.Contains(_currentSource.ToLowerInvariant()))
            {
                formattedLyric = "";
            }
            else if (_wordSyncMode)
            {
                formattedLyric = _lyricsFetcher.GetCurrentLineWordSync(simulatedPosition);
                if (formattedLyric != _currentLyric)
                {
                    _currentLyric = formattedLyric;
                    _lastLyricUpdateTime = DateTime.UtcNow;
                    shouldUpdate = true;
                }
                else if (_isPlaying && !string.IsNullOrEmpty(_currentLyric) &&
                         (DateTime.UtcNow - _lastLyricUpdateTime).TotalMilliseconds > 5000)
                {
                    // Clear lyrics after 5 seconds of no change (only when playing)
                    _currentLyric = "";
                    formattedLyric = "";
                    shouldUpdate = true;
                }
            }
            else
            {
                string newLine = _lyricsFetcher.GetCurrentLine(simulatedPosition);
                if (newLine != _currentLyric)
                {
                    _currentLyric = newLine;
                    _lastLyricUpdateTime = DateTime.UtcNow;
                    shouldUpdate = true;
                }
                else if (_isPlaying && !string.IsNullOrEmpty(_currentLyric) &&
                         (DateTime.UtcNow - _lastLyricUpdateTime).TotalMilliseconds > 5000)
                {
                    // Clear lyrics after 5 seconds of no change (only when playing)
                    _currentLyric = "";
                    shouldUpdate = true;
                }

                formattedLyric = _currentLyric;
            }

            // progress
            double durationMs = timeline.EndTime.TotalMilliseconds > 0
                ? timeline.EndTime.TotalMilliseconds
                : _lyricsFetcher.GetSongLength();

            double progress = durationMs > 0
                ? Math.Min(Math.Max(simulatedPosition, 0) / durationMs, 1.0)
                : 0;

            // broadcast lyric when changed, progress only when playing
            if (shouldUpdate)
            {
                OnLyricUpdate?.Invoke(formattedLyric ?? "", progress);
                _lastBroadcastTime = DateTime.UtcNow;
            }
            else if (_isPlaying && (DateTime.UtcNow - _lastBroadcastTime).TotalMilliseconds >= 1000)
            {
                // Send progress update even if lyric hasn't changed (every 1 second, only when playing)
                OnLyricUpdate?.Invoke(null, progress); // null = don't update lyric, just progress
                _lastBroadcastTime = DateTime.UtcNow;
            }

            // update console display
            UpdateConsole(_lastTitle, _lastArtist, simulatedPosition);
        }

        private void ProcessKeyboardInput()
        {
            try
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    // Quit keys
                    if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape)
                    {
                        OnQuitRequested?.Invoke();
                        return;
                    }

                    // Help toggle
                    if (key.Key == ConsoleKey.H)
                    {
                        _showHelp = !_showHelp;
                        continue;
                    }

                    if (_showHelp) continue;

                    switch (key.Key)
                    {
                        // Offset controls
                        case ConsoleKey.Add:
                        case ConsoleKey.OemPlus:
                            int increment = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? 500 : 50;
                            _currentOffset += increment;
                            _offsetChangedTime = DateTime.UtcNow;
                            DebugLog($"Offset: {_currentOffset} ms (+{increment})");
                            OnStatusChanged?.Invoke("offset", _currentOffset.ToString());
                            break;

                        case ConsoleKey.Subtract:
                        case ConsoleKey.OemMinus:
                            int decrement = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? 500 : 50;
                            _currentOffset -= decrement;
                            _offsetChangedTime = DateTime.UtcNow;
                            DebugLog($"Offset: {_currentOffset} ms (-{decrement})");
                            OnStatusChanged?.Invoke("offset", _currentOffset.ToString());
                            break;

                        case ConsoleKey.S:
                            SaveOffsetToConfig();
                            _offsetSavedTime = DateTime.UtcNow;
                            DebugLog($"Offset saved to config: {_currentOffset} ms");
                            break;

                        // Mode toggles
                        case ConsoleKey.W:
                            _wordSyncMode = !_wordSyncMode;
                            DebugLog($"Word sync: {(_wordSyncMode ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("wordsync", _wordSyncMode.ToString().ToLower());
                            break;

                        case ConsoleKey.O:
                            _offlineMode = !_offlineMode;
                            LyricsFetcher.OfflineMode = _offlineMode;
                            DebugLog($"Offline mode: {(_offlineMode ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("offline", _offlineMode.ToString().ToLower());
                            break;

                        case ConsoleKey.C:
                            _cjkFilter = !_cjkFilter;
                            LyricsFetcher.FilterCjkLyrics = _cjkFilter;
                            DebugLog($"CJK filter: {(_cjkFilter ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("cjk", _cjkFilter.ToString().ToLower());
                            break;

                        case ConsoleKey.P:
                            _plainLyricsFallback = !_plainLyricsFallback;
                            LyricsFetcher.PlainLyricsFallback = _plainLyricsFallback;
                            DebugLog($"Plain lyrics fallback: {(_plainLyricsFallback ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("plain", _plainLyricsFallback.ToString().ToLower());
                            break;

                        // Lyrics controls
                        case ConsoleKey.N:
                            _lyricsFetcher.NextLyrics();
                            _currentSource = _lyricsFetcher.CurrentSource;
                            _totalLyricsAvailable = _lyricsFetcher.TotalResults;
                            _currentLyricsIndex = _lyricsFetcher.CurrentIndex;
                            OnStatusChanged?.Invoke("lyricsrc", _currentSource);
                            break;

                        case ConsoleKey.R:
                            // Force re-fetch
                            _lastTitle = "";
                            _lastArtist = "";
                            DebugLog("Forcing lyrics re-fetch...");
                            break;

                        case ConsoleKey.X:
                            // Clear cache for current song
                            ClearCurrentSongCache();
                            break;
                    }
                }
            }
            catch { } // Ignore input errors
        }

        private void SaveOffsetToConfig()
        {
            try
            {
                if (File.Exists("config.json"))
                {
                    var json = File.ReadAllText("config.json");
                    var config = JsonSerializer.Deserialize<Config>(json);
                    if (config != null)
                    {
                        config.OffsetMs = _currentOffset;
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        File.WriteAllText("config.json", JsonSerializer.Serialize(config, options));
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to save offset: {ex.Message}");
            }
        }

        private void ClearCurrentSongCache()
        {
            try
            {
                _lyricsFetcher.ClearCache(_lastTitle, _lastArtist);
                _lastTitle = "";
                _lastArtist = "";
                DebugLog("Cache cleared, re-fetching...");
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to clear cache: {ex.Message}");
            }
        }

        private void UpdateConsole(string title, string artist, long position)
        {
            try
            {
                if (!_consoleInitialized)
                {
                    Console.Clear();
                    Console.CursorVisible = false;
                    _consoleInitialized = true;
                }

                Console.SetCursorPosition(0, 0);

                if (_showHelp)
                {
                    WriteHelpScreen();
                    return;
                }

                // Main display (no box header)
                WriteConsoleLine($"🎵 Now Playing: {title} - {artist}");
                WriteSourceLine();
                WriteConsoleLine($"🕒 Position: {FormatTime(position)}");
                WriteOffsetLine();

                Console.Write("🎤 Lyric: ");
                WriteLyricWithColorInline(_currentLyric);
                Console.WriteLine();

                WriteConsoleLine("");

                // Status bar with colored toggles
                WriteStatusBar();

                WriteConsoleLine("");
                WriteConsoleLine("📋 Debug Log:");

                int logLine = 0;
                foreach (var line in _debugLog.Reverse())
                {
                    WriteConsoleLine(" - " + line);
                    logLine++;
                }

                for (int i = logLine; i < 10; i++)
                {
                    WriteConsoleLine(new string(' ', Console.WindowWidth - 1));
                }
            }
            catch { }
        }

        private void WriteSourceLine()
        {
            Console.Write("📡 Source: ");

            bool isPlain = _currentSource.Contains("plain") || _currentSource.Contains("estimated");
            if (isPlain)
                Console.ForegroundColor = ConsoleColor.DarkYellow;
            else if (_currentSource == "None")
                Console.ForegroundColor = ConsoleColor.Red;
            else
                Console.ForegroundColor = ConsoleColor.Green;

            Console.Write(_currentSource);
            Console.ResetColor();

            if (_totalLyricsAvailable > 1)
                Console.Write($" ({_currentLyricsIndex + 1}/{_totalLyricsAvailable})");

            // Pad rest of line
            int remaining = Console.WindowWidth - Console.CursorLeft - 1;
            if (remaining > 0)
                Console.Write(new string(' ', remaining));
            Console.WriteLine();
        }

        private void WriteOffsetLine()
        {
            Console.Write("⏱️  Offset: ");

            // Show offset with color based on recency
            if ((DateTime.UtcNow - _offsetChangedTime).TotalSeconds < 2)
                Console.ForegroundColor = ConsoleColor.Yellow;
            else if ((DateTime.UtcNow - _offsetSavedTime).TotalSeconds < 2)
                Console.ForegroundColor = ConsoleColor.Green;

            Console.Write($"{_currentOffset} ms");
            Console.ResetColor();

            if ((DateTime.UtcNow - _offsetSavedTime).TotalSeconds < 2)
                Console.Write(" (saved!)");

            int remaining = Console.WindowWidth - Console.CursorLeft - 1;
            if (remaining > 0)
                Console.Write(new string(' ', remaining));
            Console.WriteLine();
        }

        private void WriteStatusBar()
        {
            Console.Write("[H] Help  [N] Next  ");
            
            Console.Write("[O] ");
            Console.ForegroundColor = _offlineMode ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write("Offline");
            Console.ResetColor();
            Console.Write("  ");
            
            Console.Write("[W] ");
            Console.ForegroundColor = _wordSyncMode ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write("Word");
            Console.ResetColor();
            Console.Write("  ");
            
            Console.Write("[C] ");
            Console.ForegroundColor = _cjkFilter ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write("CJK");
            Console.ResetColor();
            Console.Write("  ");
            
            Console.Write("[P] ");
            Console.ForegroundColor = _plainLyricsFallback ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write("Plain");
            Console.ResetColor();
            Console.Write("  ");
            
            Console.Write("[Q] Quit");

            int remaining = Console.WindowWidth - Console.CursorLeft - 1;
            if (remaining > 0)
                Console.Write(new string(' ', remaining));
            Console.WriteLine();
        }

        private void WriteToggle(string key, string name, bool enabled)
        {
            // Keep for compatibility but not used anymore
            Console.Write(key + ":");
            Console.ForegroundColor = enabled ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(name);
            Console.ResetColor();
        }

        private void WriteLyricWithColorInline(string lyric)
        {
            if (string.IsNullOrEmpty(lyric))
            {
                Console.Write(new string(' ', Console.WindowWidth - 15));
                return;
            }

            // Check if lyric has color tags (word sync mode)
            if (lyric.Contains("<color="))
            {
                // Parse color tags for word sync
                int i = 0;
                while (i < lyric.Length)
                {
                    if (lyric.Substring(i).StartsWith("<color=yellow>"))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        i += 14;
                    }
                    else if (lyric.Substring(i).StartsWith("<color=white>"))
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        i += 13;
                    }
                    else if (lyric.Substring(i).StartsWith("</color>"))
                    {
                        Console.ResetColor();
                        i += 8;
                    }
                    else
                    {
                        Console.Write(lyric[i]);
                        i++;
                    }
                }
                Console.ResetColor();
            }
            else
            {
                // No word sync - show entire lyric in yellow
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(lyric);
                Console.ResetColor();
            }

            // Pad rest
            int curPos = Console.CursorLeft;
            int toPad = Console.WindowWidth - curPos - 1;
            if (toPad > 0)
                Console.Write(new string(' ', toPad));
        }

        private void WriteHelpScreen()
        {
            WriteConsoleLine("╔══════════════════════════════════════════════════════════════╗");
            WriteConsoleLine("║                    Keyboard Shortcuts                        ║");
            WriteConsoleLine("╠══════════════════════════════════════════════════════════════╣");
            WriteConsoleLine("║  W         - Toggle word sync mode                           ║");
            WriteConsoleLine("║  O         - Toggle offline mode                             ║");
            WriteConsoleLine("║  C         - Toggle CJK lyrics filter                        ║");
            WriteConsoleLine("║  P         - Toggle plain lyrics fallback                    ║");
            WriteConsoleLine("║  N         - Next lyrics source                              ║");
            WriteConsoleLine("║  R         - Refresh/re-fetch lyrics                         ║");
            WriteConsoleLine("║  X         - Clear cache for current song                    ║");
            WriteConsoleLine("║  +/-       - Adjust offset by 50ms                           ║");
            WriteConsoleLine("║  Shift+/-  - Adjust offset by 500ms                          ║");
            WriteConsoleLine("║  S         - Save offset to config                           ║");
            WriteConsoleLine("║  H         - Toggle this help screen                         ║");
            WriteConsoleLine("║  Q/Esc     - Quit                                            ║");
            WriteConsoleLine("╚══════════════════════════════════════════════════════════════╝");
            WriteConsoleLine("");
            WriteConsoleLine("Press H to return...");
        }

        private void WriteConsoleLine(string text)
        {
            try
            {
                int width = Console.WindowWidth - 1;
                if (text.Length >= width)
                    Console.WriteLine(text.Substring(0, width));
                else
                    Console.WriteLine(text + new string(' ', width - text.Length));
            }
            catch { }
        }

        private string FormatTime(long ms)
        {
            long absMs = Math.Abs(ms);
            long minutes = absMs / 60000;
            long seconds = (absMs % 60000) / 1000;
            return $"{(ms < 0 ? "-" : "")}{minutes}:{seconds:D2}";
        }

        private void DebugLog(string message)
        {
            _debugLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (_debugLog.Count > 10)
                _debugLog.Dequeue();
        }

        // Public methods for WebSocket control
        public void EnableWordSync() => _wordSyncMode = true;
        public void DisableWordSync() => _wordSyncMode = false;
        public void ToggleWordSync() => _wordSyncMode = !_wordSyncMode;
        public void ToggleOfflineMode()
        {
            _offlineMode = !_offlineMode;
            LyricsFetcher.OfflineMode = _offlineMode;
        }
        public void ToggleCjkFilter()
        {
            _cjkFilter = !_cjkFilter;
            LyricsFetcher.FilterCjkLyrics = _cjkFilter;
        }
        public void TogglePlainFallback()
        {
            _plainLyricsFallback = !_plainLyricsFallback;
            LyricsFetcher.PlainLyricsFallback = _plainLyricsFallback;
        }
        public void NextLyrics()
        {
            _lyricsFetcher.NextLyrics();
            _currentSource = _lyricsFetcher.CurrentSource;
            _totalLyricsAvailable = _lyricsFetcher.TotalResults;
            _currentLyricsIndex = _lyricsFetcher.CurrentIndex;
        }
        public void RefreshLyrics()
        {
            _lastTitle = "";
            _lastArtist = "";
        }
        public void ClearCacheAndRefresh()
        {
            ClearCurrentSongCache();
        }
        public void AdjustOffset(int delta)
        {
            _currentOffset += delta;
            _offsetChangedTime = DateTime.UtcNow;
        }
        public void SaveOffset()
        {
            SaveOffsetToConfig();
            _offsetSavedTime = DateTime.UtcNow;
        }
        public string GetFullLyricsText()
        {
            return _lyricsFetcher.GetFullLyricsText();
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            Console.CursorVisible = true;
        }
    }
}
