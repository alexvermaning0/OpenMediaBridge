using OpenMediaBridge.Lyrics.Fetchers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Timers;
using Timer = System.Timers.Timer;

namespace OpenMediaBridge.Services
{
    public class LyricsService
    {
        private readonly IMediaService _wmService;
        private readonly Timer _timer;
        private readonly LyricsFetcher _lyricsFetcher;
        private readonly List<string> _disabledSources;

        private string _currentLyric = "";
        private string _lastTitle = "";
        private string _lastArtist = "";
        private string _currentSource = "None";
        private DateTime _lastLyricUpdateTime = DateTime.MinValue;
        private DateTime _lastBroadcastTime = DateTime.MinValue;

        // position simulation
        private long _lastKnownPosition = 0;
        private DateTime _lastPositionUpdateTime = DateTime.MinValue;
        private bool _isPlaying = false;

        // modes & settings
        private bool _wordSyncMode = false;
        private bool _offlineMode = false;
        private bool _plainLyricsFallback = false;
        private bool _cjkFilter = true;

        // translation
        private bool _translationEnabled = false;
        private List<LyricsLine> _translatedLines = null;
        private string _translatedForKey = "";
        private string _translatedForSource = "";
        private bool _translationPending = false;
        private string _translationTargetLang = "en";
        private string _libreTranslateUrl = "https://libretranslate.com";
        private string _translationApiKey = "";
        private bool _showLanguageSelect = false;

        private static readonly (string Code, string Name, ConsoleKey Key, string KeyLabel)[] _languageOptions =
        {
            ("en", "English",    ConsoleKey.E,  "E"),
            ("ar", "Arabic",     ConsoleKey.D1, "1"),
            ("zh", "Chinese",    ConsoleKey.D2, "2"),
            ("nl", "Dutch",      ConsoleKey.D,  "D"),
            ("fr", "French",     ConsoleKey.D3, "3"),
            ("de", "German",     ConsoleKey.D4, "4"),
            ("it", "Italian",    ConsoleKey.D5, "5"),
            ("ja", "Japanese",   ConsoleKey.D6, "6"),
            ("ko", "Korean",     ConsoleKey.D7, "7"),
            ("pt", "Portuguese", ConsoleKey.D8, "8"),
            ("ru", "Russian",    ConsoleKey.D9, "9"),
            ("es", "Spanish",    ConsoleKey.D0, "0"),
        };

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
        public bool TranslationEnabled => _translationEnabled;
        public string TranslationTargetLang => _translationTargetLang;

        public LyricsService(IMediaService wmService)
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
            _translationEnabled = _wmService?.Config?.TranslationEnabled ?? false;
            _translationTargetLang = _wmService?.Config?.TranslationTargetLang ?? "en";
            _libreTranslateUrl = _wmService?.Config?.LibreTranslateUrl ?? "https://libretranslate.com";
            _translationApiKey = _wmService?.Config?.TranslationApiKey ?? "";

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

            if (_showHelp)
            {
                UpdateConsole("", "", 0);
                return;
            }

            // Check if we have a valid session
            if (_wmService == null || !_wmService.HasActiveSession || _wmService.CurrentMediaProperties == null)
            {
                UpdateConsole("", "", 0);
                return;
            }

            var props = _wmService.CurrentMediaProperties;
            var rawTitle = props.Title ?? "";
            var rawArtist = props.Artist ?? "";

            // Get source app to determine if we need YouTube-style parsing
            var sourceApp = _wmService.CurrentSourceApp ?? "";

            // Check if source is a browser
            var browserApps = new[] { "chrome", "firefox", "brave", "edge", "opera", "safari", "chromium", "vivaldi" };
            bool isBrowser = browserApps.Any(b => sourceApp.ToLowerInvariant().Contains(b));

            // Parse title/artist based on source app
            var (title, artist) = ParseMediaTitle(rawTitle, rawArtist, sourceApp);

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
            {
                UpdateConsole("", "", 0);
                return;
            }

            // Get timeline info
            MediaTimelineInfo timeline;
            MediaPlaybackInfo playbackInfo;

            try
            {
                timeline = _wmService.GetTimelineInfo();
                playbackInfo = _wmService.GetPlaybackInfo();
            }
            catch
            {
                return;
            }

            var smtcPos = (long)timeline.Position.TotalMilliseconds;
            var smtcPosWithOffset = smtcPos + _currentOffset;

            // Position simulation for smoother lyrics
            long simulatedPosition;
            if (playbackInfo.IsPlaying)
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
                if (isBrowser && (rawTitle != title || rawArtist != artist))
                {
                    DebugLog($"Parsed: \"{rawTitle}\" → title=\"{title}\" artist=\"{artist}\"");
                }
                DebugLog($"Fetching lyrics: {title} - {artist} (browser: {isBrowser})");
                _currentSource = ""; // Reset so lyricsrc: always fires after fetch, even if source name is unchanged
                _lyricsFetcher.FetchLyrics(title, artist, (int)timeline.Duration.TotalMilliseconds, isBrowser);
                _lastTitle = title;
                _lastArtist = artist;
                _currentLyric = "";
                _lastLyricUpdateTime = DateTime.MinValue;
                _translatedForKey = "";
                _translatedForSource = "";
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
                if (_translationEnabled && _currentSource != "None")
                    TriggerTranslationIfNeeded(sourceChanged: true);
            }

            bool shouldUpdate = false;
            string formattedLyric = "";

            if (_disabledSources.Contains(_currentSource.ToLowerInvariant()))
            {
                formattedLyric = "";
            }
            else if (_wordSyncMode && !(_translationEnabled && _translatedLines != null))
            {
                // Add a lead to compensate for server tick (50ms) + WS transport (~100ms) latency,
                // so the first word highlight reaches the client closer to when the audio hits it.
                // Word sync is skipped when translation has lines ready — highlighting individual
                // words makes no sense after translation (different word count and order).
                const int WordSyncLeadMs = 125;
                formattedLyric = _lyricsFetcher.GetCurrentLineWordSync(simulatedPosition + WordSyncLeadMs);
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
            else if (_translationEnabled)
            {
                if (_translatedLines != null)
                {
                    int idx = _translatedLines.FindLastIndex(l => l.Time <= simulatedPosition);
                    string newLine = idx >= 0 ? _translatedLines[idx].Text : "";
                    if (newLine != _currentLyric)
                    {
                        _currentLyric = newLine;
                        _lastLyricUpdateTime = DateTime.UtcNow;
                        shouldUpdate = true;
                    }
                    else if (_isPlaying && !string.IsNullOrEmpty(_currentLyric) &&
                             (DateTime.UtcNow - _lastLyricUpdateTime).TotalMilliseconds > 5000)
                    {
                        _currentLyric = "";
                        shouldUpdate = true;
                    }
                }
                else if (_translationPending && !string.IsNullOrEmpty(_currentLyric))
                {
                    _currentLyric = "";
                    shouldUpdate = true;
                }
                formattedLyric = _currentLyric;
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
            double durationMs = timeline.Duration.TotalMilliseconds > 0
                ? timeline.Duration.TotalMilliseconds
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

                    // Language select intercepts all keys
                    if (_showLanguageSelect)
                    {
                        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.L)
                        {
                            _showLanguageSelect = false;
                        }
                        else
                        {
                            foreach (var lang in _languageOptions)
                            {
                                if (key.Key == lang.Key)
                                {
                                    SetTranslationLanguage(lang.Code);
                                    _showLanguageSelect = false;
                                    break;
                                }
                            }
                        }
                        continue;
                    }

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
                        case ConsoleKey.L:
                            _showLanguageSelect = true;
                            _showHelp = false;
                            break;

                        case ConsoleKey.T:
                            _translationEnabled = !_translationEnabled;
                            DebugLog($"Translation: {(_translationEnabled ? "ON" : "OFF")} ({_translationTargetLang})");
                            OnStatusChanged?.Invoke("translate", _translationEnabled.ToString().ToLower());
                            if (_translationEnabled)
                                TriggerTranslationIfNeeded();
                            break;

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
                            // Refetch to apply the change
                            _lyricsFetcher.ForceRefetch();
                            break;

                        case ConsoleKey.C:
                            _cjkFilter = !_cjkFilter;
                            LyricsFetcher.FilterCjkLyrics = _cjkFilter;
                            DebugLog($"CJK filter: {(_cjkFilter ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("cjk", _cjkFilter.ToString().ToLower());
                            // Refetch to apply the change
                            _lyricsFetcher.ForceRefetch();
                            break;

                        case ConsoleKey.P:
                            _plainLyricsFallback = !_plainLyricsFallback;
                            LyricsFetcher.PlainLyricsFallback = _plainLyricsFallback;
                            DebugLog($"Plain lyrics fallback: {(_plainLyricsFallback ? "ON" : "OFF")}");
                            OnStatusChanged?.Invoke("plain", _plainLyricsFallback.ToString().ToLower());
                            // Refetch to apply the change
                            _lyricsFetcher.ForceRefetch();
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
                            _lyricsFetcher.ForceRefetch();
                            DebugLog("Forcing lyrics re-fetch...");
                            break;

                        case ConsoleKey.X:
                            // Clear cache for current song and refetch
                            ClearCurrentSongCache();
                            _lyricsFetcher.ForceRefetch();
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
                    // The TUI owns the screen now; stop echoing log lines to the
                    // console (the debug pane is the live view from here on).
                    OpenMediaBridge.Logging.Log.EchoToConsole = false;
                }

                Console.SetCursorPosition(0, 0);

                if (_showHelp)
                {
                    WriteHelpScreen();
                    return;
                }

                if (_showLanguageSelect)
                {
                    WriteLanguageSelectScreen();
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
                var recent = OpenMediaBridge.Logging.Log.Recent();
                for (int i = recent.Count - 1; i >= 0 && logLine < 10; i--)
                {
                    WriteConsoleLine(" - " + recent[i]);
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

            Console.Write("[T] ");
            if (_translationPending)
                Console.ForegroundColor = ConsoleColor.Yellow;
            else
                Console.ForegroundColor = _translationEnabled ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"Trans({_translationTargetLang})");
            Console.ResetColor();
            Console.Write(" [L]  ");

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
            WriteConsoleLine("║  T         - Toggle translation (LibreTranslate)             ║");
            WriteConsoleLine("║  L         - Select translation language                     ║");
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

        private void DebugLog(string message) => OpenMediaBridge.Logging.Log.Debug(message);

        // Lets platform-specific media services (e.g. LinuxMprisService) route their
        // own log messages into the same shared log shown in the console TUI.
        public void AddDebugLog(string message) => OpenMediaBridge.Logging.Log.Debug(message);

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
        public void ToggleTranslation()
        {
            _translationEnabled = !_translationEnabled;
            DebugLog($"Translation: {(_translationEnabled ? "ON" : "OFF")} ({_translationTargetLang})");
            OnStatusChanged?.Invoke("translate", _translationEnabled.ToString().ToLower());
            // If turning off, or turning on with translated lines already ready, refresh templates now
            if (!_translationEnabled || _translatedLines != null)
                OnStatusChanged?.Invoke("lyricsrc", _currentSource);
            if (_translationEnabled)
                TriggerTranslationIfNeeded();
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
            if (_translationEnabled && _translatedLines != null && _translatedLines.Count > 0)
                return string.Join("\n", _translatedLines.Select(l => l.Text));
            return _lyricsFetcher.GetFullLyricsText();
        }

        /// <summary>
        /// Parse title to extract song title and artist
        /// Only does YouTube-style parsing for browsers, uses raw values for music apps
        /// </summary>
        private (string title, string artist) ParseMediaTitle(string rawTitle, string rawArtist, string sourceApp)
        {
            if (string.IsNullOrEmpty(rawTitle))
                return (rawTitle, rawArtist);

            string title = rawTitle;
            string artist = rawArtist ?? "";

            // Check if source is a browser (needs YouTube-style parsing)
            var browserApps = new[] { "chrome", "firefox", "brave", "edge", "opera", "safari", "chromium", "vivaldi" };
            bool isBrowser = browserApps.Any(b => sourceApp.ToLowerInvariant().Contains(b));

            // If not a browser (Spotify, Apple Music, etc.), just return as-is
            if (!isBrowser)
            {
                return (title, artist);
            }

            // Browser detected - do YouTube-style parsing

            // Extract artist from Japanese brackets 【Artist】 at the START of title
            var bracketMatch = System.Text.RegularExpressions.Regex.Match(title, @"^【(.+?)】\s*");
            if (bracketMatch.Success)
            {
                artist = bracketMatch.Groups[1].Value.Trim();
                title = title.Substring(bracketMatch.Length).Trim();
            }

            // Extract title from Japanese quotes「Title」
            var quoteMatch = System.Text.RegularExpressions.Regex.Match(title, @"「(.+?)」");
            if (quoteMatch.Success)
            {
                // If we found a title in quotes, extract it
                string quotedTitle = quoteMatch.Groups[1].Value.Trim();
                // The part before the quote might be the artist
                string beforeQuote = title.Substring(0, quoteMatch.Index).Trim();
                if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(beforeQuote))
                {
                    artist = beforeQuote;
                }
                title = quotedTitle;
            }

            // Extract title from Japanese brackets『Title』
            var bracketMatch2 = System.Text.RegularExpressions.Regex.Match(title, @"『(.+?)』");
            if (bracketMatch2.Success)
            {
                string bracketedTitle = bracketMatch2.Groups[1].Value.Trim();
                string beforeBracket = title.Substring(0, bracketMatch2.Index).Trim();
                // Clean trailing punctuation from artist part
                beforeBracket = beforeBracket.TrimEnd('.', '。', ' ');
                if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(beforeBracket))
                {
                    artist = beforeBracket;
                }
                title = bracketedTitle;
            }

            // Remove common suffixes
            var suffixPatterns = new[]
            {
                @"\s*\(Official\s*(Music\s*)?(Video|Audio|Lyric\s*Video|Visualizer|MV)?\)",
                @"\s*\[Official\s*(Music\s*)?(Video|Audio|Lyric\s*Video|Visualizer|MV)?\]",
                @"\s*\(Lyric\s*Video\)",
                @"\s*\[Lyric\s*Video\]",
                @"\s*\(Audio\)",
                @"\s*\[Audio\]",
                @"\s*\(HD\)",
                @"\s*\[HD\]",
                @"\s*\(HQ\)",
                @"\s*\[HQ\]",
                @"\s*\(4K\)",
                @"\s*\[4K\]",
                @"\s*\(Lyrics\)",
                @"\s*\[Lyrics\]",
                @"\s*\(MV\)",
                @"\s*\[MV\]",
                @"\s*\(PV\)",
                @"\s*\[PV\]",
                @"\s*\(Live.*?\)",
                @"\s*\[Live.*?\]",
                @"\s*MV\s*$",              // MV at end
                @"\s*×\s*.*$",        // × and everything after (× TV Anime...)
                @"\s*\(ZUTOMAYO.*?\)",     // (ZUTOMAYO...)
                @"\s*【.*?】",     // 【...】 remaining
                @"\s*「.*?」",     // 「...」 remaining
                @"\s*『.*?』",     // 『...』 remaining
            };

            foreach (var pattern in suffixPatterns)
            {
                title = System.Text.RegularExpressions.Regex.Replace(
                    title, pattern, "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            title = title.Trim();

            // Handle " / " - dual language titles, take first part
            if (title.Contains(" / "))
            {
                title = title.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            }

            // Try to extract artist with " - " separator (only if we don't have artist yet)
            if (string.IsNullOrEmpty(artist) && title.Contains(" - "))
            {
                var parts = title.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    string p1 = parts[0].Trim();
                    string p2 = parts[1].Trim();

                    // Check if either has feat./ft. - that indicates artist side
                    bool p2HasFeat = System.Text.RegularExpressions.Regex.IsMatch(
                        p2, @"(feat\.|ft\.|featuring)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (p2HasFeat)
                    {
                        // "Title - Artist feat. X"
                        title = p1;
                        artist = p2;
                    }
                    else
                    {
                        // Assume "Artist - Title" (more common)
                        artist = p1;
                        title = p2;
                    }
                }
            }

            // Clean feat. from title and artist
            title = System.Text.RegularExpressions.Regex.Replace(
                title, @"\s*(feat\.|ft\.|featuring)\s*.*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            if (!string.IsNullOrEmpty(artist))
            {
                artist = System.Text.RegularExpressions.Regex.Replace(
                    artist, @"\s*(feat\.|ft\.|featuring)\s*.*$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }

            // Clean quotes (including fancy quotes)
            title = title.Trim('"', '\'', '“', '”', '‘', '’');

            return (title, artist);
        }

        public void SetTranslationLanguage(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode) || langCode == _translationTargetLang) return;
            _translationTargetLang = langCode.ToLowerInvariant();
            _translatedLines = null;
            _translatedForKey = "";
            _translatedForSource = "";
            DebugLog($"Translation language: {_translationTargetLang}");
            OnStatusChanged?.Invoke("translatelang", _translationTargetLang);
            // Revert templates to original while new translation loads
            OnStatusChanged?.Invoke("lyricsrc", _currentSource);
            if (_translationEnabled)
                TriggerTranslationIfNeeded();
        }

        private void WriteLanguageSelectScreen()
        {
            var current = Array.Find(_languageOptions, l => l.Code == _translationTargetLang);
            var currentDisplay = current.Name != null
                ? $"{current.Name} ({current.Code})"
                : _translationTargetLang;

            WriteConsoleLine("╔══════════════════════════════════════════════════════════════╗");
            WriteConsoleLine("║              Select Translation Language                     ║");
            WriteConsoleLine("╠══════════════════════════════════════════════════════════════╣");
            WriteConsoleLine($"║  Current: {currentDisplay,-52}║");
            WriteConsoleLine("╠══════════════════════════════════════════════════════════════╣");

            int half = (_languageOptions.Length + 1) / 2;
            for (int i = 0; i < half; i++)
            {
                int rightIdx = i + half;
                WriteLangRow(i, rightIdx < _languageOptions.Length ? rightIdx : -1);
            }

            WriteConsoleLine("╠══════════════════════════════════════════════════════════════╣");
            WriteConsoleLine("║  Press key to select  •  L or Esc to return                 ║");
            WriteConsoleLine("╚══════════════════════════════════════════════════════════════╝");
            WriteConsoleLine("");
        }

        private void WriteLangRow(int leftIdx, int rightIdx)
        {
            try
            {
                Console.Write("║  ");
                WriteLangEntry(_languageOptions[leftIdx]);
                Console.Write("    ");
                if (rightIdx >= 0)
                    WriteLangEntry(_languageOptions[rightIdx]);
                else
                    Console.Write(new string(' ', 20));
                int remaining = Console.WindowWidth - Console.CursorLeft - 2;
                if (remaining > 0)
                    Console.Write(new string(' ', remaining));
                Console.WriteLine("║");
            }
            catch { }
        }

        private void WriteLangEntry((string Code, string Name, ConsoleKey Key, string KeyLabel) lang)
        {
            bool isCurrent = lang.Code == _translationTargetLang;
            if (isCurrent) Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"[{lang.KeyLabel}] {lang.Name,-11}({lang.Code})");
            Console.ResetColor();
        }

        private void TriggerTranslationIfNeeded(bool sourceChanged = false)
        {
            if (!_translationEnabled) return;

            var key = $"{_lastArtist}|{_lastTitle}";
            var currentSource = _lyricsFetcher.CurrentSource ?? "None";

            if (string.IsNullOrEmpty(_lastTitle)) return;
            if (_translationPending) return;

            // Skip if we already translated this exact song+source combo
            if (key == _translatedForKey && currentSource == _translatedForSource && !sourceChanged)
                return;

            // When source changed for the same song, clear stale translation
            // but DON'T null _translatedLines yet — keep showing old translation
            // until the new one is ready
            if (sourceChanged && key == _translatedForKey)
            {
                _translatedForKey = "";
                _translatedForSource = "";
            }

            // Check translation cache first
            if (CacheHelper.TryLoadTranslated(
                LyricsFetcher.CacheFolder, _lastArtist, _lastTitle, _translationTargetLang,
                out var cachedLines))
            {
                _translatedLines = cachedLines;
                _translatedForKey = key;
                _translatedForSource = currentSource;
                DebugLog($"Translation loaded from cache ({_translationTargetLang})");
                return;
            }

            var rawLines = _lyricsFetcher.GetCurrentRawLines();
            if (rawLines == null || rawLines.Count == 0) return;

            _translationPending = true;
            var capturedKey = key;
            var capturedSource = currentSource;
            var capturedArtist = _lastArtist;
            var capturedTitle = _lastTitle;
            var capturedLines = rawLines.ToList();

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    DebugLog($"Translating {capturedLines.Count} lines → {_translationTargetLang}...");
                    var translated = await TranslationService.TranslateLyricsAsync(
                        capturedLines, _translationTargetLang, _libreTranslateUrl, _translationApiKey);

                    if ($"{_lastArtist}|{_lastTitle}" == capturedKey)
                    {
                        _translatedLines = translated;
                        _translatedForKey = capturedKey;
                        _translatedForSource = capturedSource;
                        CacheHelper.SaveTranslated(
                            LyricsFetcher.CacheFolder, capturedArtist, capturedTitle,
                            _translationTargetLang, translated);
                        DebugLog($"Translation complete ({_translationTargetLang})");
                        OnStatusChanged?.Invoke("lyricsrc", _currentSource);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Translation failed: {ex.Message}");
                }
                finally
                {
                    _translationPending = false;
                }
            });
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            Console.CursorVisible = true;
        }
    }
}
