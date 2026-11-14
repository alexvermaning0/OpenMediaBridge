using System.Diagnostics;

namespace OpenMediaBridge.Services
{
    public class LinuxMprisService : IMediaService, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _started;
        private string _lastProcessedSong = "";
        private bool _lastIsPlaying = false;
        private readonly object _updateLock = new();

        private MediaPlaybackInfo _currentPlaybackInfo = new();
        private MediaTimelineInfo _currentTimelineInfo = new();

        public MediaProperties? CurrentMediaProperties { get; private set; }
        public string CurrentPlayerName { get; private set; } = "";
        public string CurrentSourceApp => CurrentPlayerName;
        public bool HasActiveSession => CurrentMediaProperties != null;

        // Set by LyricsService so MPRIS messages appear in the debug log TUI.
        public Action<string>? LogCallback { get; set; }

        // Optional: resolves now-playing for known radio stations that only
        // report a station name (set from Program.cs when station_lookup is on).
        public StationResolver? StationResolver { get; set; }

        public ResoniteWSSession WSSession { get; }
        public ResoniteWSServer Server { get; }
        public Config Config { get; }

        private void Log(string msg)
        {
            if (LogCallback != null) LogCallback(msg);
            else Console.WriteLine(msg);
        }

        public LinuxMprisService(ResoniteWSSession session, ResoniteWSServer server)
        {
            WSSession = session;
            Server = server;
            Config = server.Config;
        }

        public void Start()
        {
            // A single instance is shared across all sessions (see Program.cs),
            // and every connect calls Start() — poll only once.
            if (_started) return;
            _started = true;

            if (!EnsurePlayerctl())
            {
                Log("[MPRIS] Media controls unavailable — install playerctl and restart.");
                return;
            }
            _ = Task.Run(() => PollLoop(_cts.Token));
            Log("[MPRIS] Started (playerctl backend)");
        }

        // Returns true if playerctl is ready to use.
        private bool EnsurePlayerctl()
        {
            if (IsInPath("playerctl")) return true;

            Log("[MPRIS] playerctl not found — attempting automatic install...");

            // (package-manager, install-args[])
            (string pm, string[] args)[] candidates =
            [
                ("apt-get", ["install", "-y", "playerctl"]),
                ("apt",     ["install", "-y", "playerctl"]),
                ("dnf",     ["install", "-y", "playerctl"]),
                ("pacman",  ["-S", "--noconfirm", "playerctl"]),
                ("zypper",  ["install", "-y", "playerctl"]),
                ("apk",     ["add", "playerctl"]),
            ];

            foreach (var (pm, pmArgs) in candidates)
            {
                if (!IsInPath(pm)) continue;

                // Try without sudo first (running as root), then with sudo.
                foreach (var useSudo in new[] { false, true })
                {
                    string exe      = useSudo ? "sudo" : pm;
                    string[] fullArgs = useSudo ? ["-n", pm, ..pmArgs] : pmArgs;

                    Log($"[MPRIS] Running: {exe} {string.Join(' ', fullArgs)}");
                    int code = RunSync(exe, fullArgs);
                    if (code == 0 && IsInPath("playerctl"))
                    {
                        Log("[MPRIS] playerctl installed successfully.");
                        return true;
                    }
                }
                break; // found a package manager but install failed — don't try others
            }

            Log("[MPRIS] Could not install playerctl automatically. " +
                "Please run: sudo apt install playerctl  (or equivalent for your distro)");
            return false;
        }

        private static bool IsInPath(string binary)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(':').Any(dir =>
                File.Exists(Path.Combine(dir, binary)));
        }

        private static int RunSync(string exe, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                return proc?.ExitCode ?? 1;
            }
            catch { return 1; }
        }

        private async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UpdateStateAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log($"[MPRIS] Poll error: {ex.Message}"); }

                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Prefix playerctl args with `-i player1,player2` for any players listed
        // in Config.IgnorePlayers, so e.g. a browser's MPRIS session doesn't get
        // picked over the real music player.
        // The player we last chose to display, used to target transport controls
        // at that exact player (so controls act on what's on screen).
        private string _activePlayer = "";

        private bool IsIgnored(string player) =>
            Config?.IgnorePlayers?.Any(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase)) ?? false;

        private bool IsDeprioritized(string player) =>
            Config?.DeprioritizePlayers?.Any(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase)) ?? false;

        // Prefix playerctl args with `-p <player>` so a control acts on the
        // player we're currently displaying, not whatever playerctl defaults to.
        private string[] WithPlayer(params string[] args) =>
            string.IsNullOrEmpty(_activePlayer) ? args : new[] { "-p", _activePlayer }.Concat(args).ToArray();

        // Single playerctl call returns all fields tab-separated.
        private static readonly string[] _format = [
            "metadata", "--format",
            "{{playerName}}\t{{status}}\t{{title}}\t{{artist}}\t{{album}}" +
            "\t{{mpris:artUrl}}\t{{mpris:length}}\t{{position}}\t{{shuffle}}\t{{loop}}"
        ];

        private async Task<string> RunPlayerctlAsync(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("playerctl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc == null) return "";
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                var output = await stdoutTask;
                var error  = await stderrTask;
                if (proc.ExitCode != 0)
                {
                    var msg = error.Trim();
                    if (!string.IsNullOrEmpty(msg))
                        Log($"[MPRIS] playerctl: {msg}");
                    return "";
                }
                return output.Trim();
            }
            catch (Exception ex)
            {
                Log($"[MPRIS] Failed to run playerctl: {ex.Message}");
                return "";
            }
        }

        private async Task UpdateStateAsync()
        {
            // Query every player at once and rank them, so a playing app beats a
            // paused one and a normal app beats a deprioritized one (e.g. a
            // browser). Ignored players are excluded entirely.
            var allRaw = await RunPlayerctlAsync(new[] { "-a" }.Concat(_format).ToArray());

            string[]? best = null;
            int bestScore = int.MinValue;
            foreach (var line in (allRaw ?? "").Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                var pl = f[0].Trim();
                var st = f[1].Trim();
                var ti = f[2].Trim();
                if (string.IsNullOrEmpty(pl) || IsIgnored(pl)) continue;
                if (string.IsNullOrEmpty(ti) && st != "Playing" && st != "Paused") continue;

                int score = st == "Playing" ? 2 : (st == "Paused" ? 0 : -2);
                if (!IsDeprioritized(pl)) score += 1;
                if (score > bestScore) { bestScore = score; best = f; }
            }

            if (best == null)
            {
                // No usable player. A hard-ignored browser might still be playing
                // a known station, so peek for that before clearing.
                if (await TryStationAsync()) return;

                if (CurrentMediaProperties != null)
                {
                    CurrentPlayerName = "";
                    _activePlayer = "";
                    CurrentMediaProperties = null;
                    Server.ForEachSession(s => s.SendMediaUpdate());
                }
                return;
            }

            string Get(int i) => i < best.Length ? best[i].Trim() : "";

            var playerName  = Get(0);
            var status      = Get(1);
            var title       = Get(2);
            var artist      = Get(3);
            var album       = Get(4);
            var artUrl      = Get(5);
            var lengthStr   = Get(6);
            var posStr      = Get(7);
            var shuffleStr  = Get(8);
            var loopStr     = Get(9);

            _activePlayer = playerName;

            // Station enhancement: a configured station resolves to its real
            // now-playing; an unmatched live stream keeps its station-name title
            // and gets a one-time "add it" hint. Applies to whichever player won.
            var sourceLabel = playerName;
            if (StationResolver != null)
            {
                var entry = StationResolver.Match(title);
                if (entry != null)
                {
                    var resolved = StationResolver.Resolve(entry);
                    if (resolved != null && (!string.IsNullOrEmpty(resolved.Title) || !string.IsNullOrEmpty(resolved.Artist)))
                    {
                        title = resolved.Title;
                        artist = resolved.Artist;
                        if (!string.IsNullOrEmpty(resolved.ArtUrl)) artUrl = resolved.ArtUrl;
                    }
                    sourceLabel = entry.Match;
                }
                else if (lengthStr == "9223372036854775807" && string.IsNullOrEmpty(artist))
                {
                    if (_stationHints.Add(title))
                        Log($"[Stations] Live stream with no entry: \"{title}\" — add it in stations.json or via the plugin");
                }
            }

            if (sourceLabel != CurrentPlayerName)
            {
                CurrentPlayerName = sourceLabel;
                Log($"[MPRIS] Active player: {playerName} ({status})");
            }

            var newMedia = new MediaProperties
            {
                Title      = title,
                Artist     = artist,
                AlbumTitle = album,
                ArtUrl     = artUrl,
            };

            var newPlayback = new MediaPlaybackInfo
            {
                IsPlaying      = status == "Playing",
                IsShuffleActive = shuffleStr == "On",
                RepeatMode     = loopStr.ToLowerInvariant() switch
                {
                    "track"    => "track",
                    "playlist" => "playlist",
                    _          => "none"
                }
            };

            // Both mpris:length and {{position}} in playerctl's format template
            // are reported in microseconds.
            var duration = long.TryParse(lengthStr, out var lenUs)
                ? TimeSpan.FromMicroseconds(lenUs) : TimeSpan.Zero;
            var position = long.TryParse(posStr, out var posUs)
                ? TimeSpan.FromMicroseconds(posUs) : TimeSpan.Zero;

            _currentTimelineInfo = new MediaTimelineInfo
            {
                Position = position,
                Duration = duration
            };

            var songKey = $"{title}|{artist}";
            bool songChanged;
            lock (_updateLock)
            {
                songChanged = songKey != _lastProcessedSong && !string.IsNullOrEmpty(title);
                if (songChanged) _lastProcessedSong = songKey;
            }

            bool playbackChanged = newPlayback.IsPlaying != _lastIsPlaying;
            _lastIsPlaying = newPlayback.IsPlaying;

            CurrentMediaProperties = newMedia;
            _currentPlaybackInfo   = newPlayback;

            if (songChanged)
            {
                // Push title/artist/state to clients immediately. Cover art is a
                // network lookup (up to ~10s if the APIs are slow) — fetching it
                // in the background instead of awaiting it here keeps metadata and
                // the poll loop from stalling on a song change. When the cover
                // resolves, re-push so the new URL goes out.
                Server.ForEachSession(s =>
                {
                    s.SendMediaUpdate();
                    // Also push play/shuffle/repeat so a client that just connected
                    // gets full playback state on the first metadata push, not only
                    // after the next toggle.
                    s.SendPlaybackUpdate();
                });
                _ = CoverServer.UpdateCoverAsync(newMedia).ContinueWith(
                    _ => Server.ForEachSession(s => s.SendMediaUpdate()),
                    TaskScheduler.Default);
            }
            else if (playbackChanged)
            {
                Server.ForEachSession(s => s.SendPlaybackUpdate());
            }
        }

        // Surface a radio station playing in any (even ignored) player. A matched
        // station resolves its real now-playing; an unmatched live stream shows
        // its station name (when show_unmatched_streams is on) so it's visible to
        // add. Returns true when something was emitted.
        private async Task<bool> TryStationAsync()
        {
            if (StationResolver == null) return false;

            var detected = await DetectStationAsync();
            if (detected == null) return false;

            var (entry, rawTitle) = detected.Value;

            string title, artist = "", cover = "";
            if (entry != null)
            {
                var resolved = StationResolver.Resolve(entry);
                title = resolved?.Title ?? "";
                artist = resolved?.Artist ?? "";
                cover = resolved?.ArtUrl ?? "";
                // Until the first lookup lands, at least show the station name.
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
                    title = rawTitle;
            }
            else
            {
                // Unmatched live stream — show its name only when enabled.
                if (!(Config?.ShowUnmatchedStreams ?? true)) return false;
                title = rawTitle;
            }

            var label = entry?.Match ?? rawTitle;
            if (CurrentPlayerName != label)
            {
                CurrentPlayerName = label;
                Log($"[Stations] {label}: {artist} - {title}");
            }

            var media = new MediaProperties { Title = title, Artist = artist, AlbumTitle = "", ArtUrl = cover };
            _currentPlaybackInfo = new MediaPlaybackInfo { IsPlaying = true };
            // Radio has no seekable position/duration.
            _currentTimelineInfo = new MediaTimelineInfo { Position = TimeSpan.Zero, Duration = TimeSpan.Zero };

            var songKey = $"{title}|{artist}";
            bool songChanged;
            lock (_updateLock)
            {
                songChanged = songKey != _lastProcessedSong && !string.IsNullOrEmpty(title);
                if (songChanged) _lastProcessedSong = songKey;
            }
            bool wasPlaying = _lastIsPlaying;
            _lastIsPlaying = true;

            CurrentMediaProperties = media;

            if (songChanged)
            {
                Server.ForEachSession(s => { s.SendMediaUpdate(); s.SendPlaybackUpdate(); });
                // Station covers are often placeholders; let the normal art
                // lookup fill one in by artist/title when none was provided.
                _ = CoverServer.UpdateCoverAsync(media).ContinueWith(
                    _ => Server.ForEachSession(s => s.SendMediaUpdate()), TaskScheduler.Default);
            }
            else if (!wasPlaying)
            {
                Server.ForEachSession(s => s.SendPlaybackUpdate());
            }
            return true;
        }

        // Distinct unmatched live-stream titles we've already hinted about, so
        // the "add a station" tip is logged once each, not every poll.
        private readonly HashSet<string> _stationHints = new();

        // Look across every player (ignoring the ignore-list, since the whole
        // point is to catch radio in an otherwise-ignored browser). Returns a
        // matched station entry, or (null, title) for an unmatched live stream
        // (infinite mpris:length) so the name can still be surfaced. Ordinary
        // videos have a real length and are skipped.
        private async Task<(StationEntry? entry, string rawTitle)?> DetectStationAsync()
        {
            var listRaw = await RunPlayerctlAsync("-a", "metadata", "--format",
                "{{playerName}}\t{{status}}\t{{title}}\t{{mpris:length}}");
            if (string.IsNullOrEmpty(listRaw)) return null;

            string? liveTitle = null;
            foreach (var line in listRaw.Split('\n'))
            {
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                if (!p[1].Trim().Equals("Playing", StringComparison.OrdinalIgnoreCase)) continue;
                var rawTitle = p[2].Trim();
                if (string.IsNullOrEmpty(rawTitle)) continue;

                var entry = StationResolver!.Match(rawTitle);
                if (entry != null) { _activePlayer = p[0].Trim(); return (entry, rawTitle); }

                var len = p.Length > 3 ? p[3].Trim() : "";
                if (len == "9223372036854775807" && liveTitle == null)
                {
                    liveTitle = rawTitle;
                    _activePlayer = p[0].Trim();
                }
            }

            if (liveTitle != null)
            {
                if (_stationHints.Add(liveTitle))
                    Log($"[Stations] Live stream with no entry: \"{liveTitle}\" — add it in stations.json or via the plugin");
                return (null, liveTitle);
            }
            return null;
        }

        public MediaPlaybackInfo GetPlaybackInfo() => _currentPlaybackInfo;
        public MediaTimelineInfo GetTimelineInfo() => _currentTimelineInfo;

        public async Task TryMediaControl(MediaControlType type)
        {
            var cmd = type switch
            {
                MediaControlType.Play     => "play",
                MediaControlType.Pause    => "pause",
                MediaControlType.Stop     => "stop",
                MediaControlType.Skip     => "next",
                MediaControlType.Previous => "previous",
                _                         => null
            };
            if (cmd != null) await RunPlayerctlAsync(WithPlayer(cmd));
        }

        public async Task SeekAsync(TimeSpan position)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            // playerctl takes an absolute position in (fractional) seconds.
            var seconds = position.TotalSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
            await RunPlayerctlAsync(WithPlayer("position", seconds));
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
