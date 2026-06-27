using System.Diagnostics;

namespace OpenMediaBridge.Services
{
    public class LinuxMprisService : IMediaService, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
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
            var raw = await RunPlayerctlAsync(_format);

            if (string.IsNullOrEmpty(raw))
            {
                if (CurrentMediaProperties != null)
                {
                    CurrentPlayerName = "";
                    CurrentMediaProperties = null;
                    WSSession.SendMediaUpdate();
                }
                return;
            }

            var p = raw.Split('\t');
            string Get(int i) => i < p.Length ? p[i] : "";

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

            if (playerName != CurrentPlayerName)
            {
                CurrentPlayerName = playerName;
                Log($"[MPRIS] Active player: {playerName}");
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
                await CoverServer.UpdateCoverAsync(newMedia);
                WSSession.SendMediaUpdate();
            }
            else if (playbackChanged)
            {
                WSSession.SendPlaybackUpdate();
            }
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
            if (cmd != null) await RunPlayerctlAsync(cmd);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
