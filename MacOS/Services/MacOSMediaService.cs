using System.Diagnostics;

namespace OpenMediaBridge.Services
{
    public class MacOSMediaService : IMediaService, IDisposable
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

        public Action<string>? LogCallback { get; set; }

        public ResoniteWSSession WSSession { get; }
        public ResoniteWSServer Server { get; }
        public Config Config { get; }

        private void Log(string msg)
        {
            if (LogCallback != null) LogCallback(msg);
            else Console.WriteLine(msg);
        }

        public MacOSMediaService(ResoniteWSSession session, ResoniteWSServer server)
        {
            WSSession = session;
            Server = server;
            Config = server.Config;
        }

        public void Start()
        {
            _ = Task.Run(() => PollLoop(_cts.Token));
            Log("[macOS] Started media monitoring");
        }

        private async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UpdateStateAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log($"[macOS] Poll error: {ex.Message}"); }

                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task UpdateStateAsync()
        {
            var playerInfo = await GetCurrentPlayerInfoAsync();

            if (playerInfo == null)
            {
                if (CurrentMediaProperties != null)
                {
                    CurrentMediaProperties = null;
                    CurrentPlayerName = "";
                    _lastProcessedSong = "";
                    WSSession.SendMediaUpdate();
                }
                return;
            }

            var songKey = $"{playerInfo.Title}|{playerInfo.Artist}";
            bool isPlaying = playerInfo.IsPlaying;

            lock (_updateLock)
            {
                if (songKey == _lastProcessedSong && isPlaying == _lastIsPlaying)
                    return;

                _lastProcessedSong = songKey;
                _lastIsPlaying = isPlaying;
            }

            CurrentPlayerName = playerInfo.PlayerName;
            CurrentMediaProperties = new MediaProperties
            {
                Title = playerInfo.Title,
                Artist = playerInfo.Artist,
                AlbumTitle = playerInfo.Album
            };

            _currentPlaybackInfo = new MediaPlaybackInfo
            {
                IsPlaying = isPlaying
            };

            _currentTimelineInfo = new MediaTimelineInfo
            {
                Position = TimeSpan.FromMilliseconds(playerInfo.Position),
                Duration = TimeSpan.FromMilliseconds(playerInfo.Duration)
            };

            await CoverServer.UpdateCoverAsync(CurrentMediaProperties);
            WSSession.SendMediaUpdate();
        }

        private async Task<PlayerInfo?> GetCurrentPlayerInfoAsync()
        {
            var players = new[] { "Spotify", "Music", "iTunesHelper" };

            foreach (var player in players)
            {
                var info = await QueryPlayerAsync(player);
                if (info != null && !string.IsNullOrEmpty(info.Title))
                {
                    return info;
                }
            }

            return null;
        }

        private async Task<PlayerInfo?> QueryPlayerAsync(string player)
        {
            try
            {
                string script = player switch
                {
                    "Spotify" => GetSpotifyScript(),
                    "Music" => GetMusicScript(),
                    "iTunesHelper" => GetItunesScript(),
                    _ => ""
                };

                if (string.IsNullOrEmpty(script))
                    return null;

                var output = await RunAppleScriptAsync(script);

                if (string.IsNullOrEmpty(output) || output.Contains("error"))
                    return null;

                var parts = output.Split('\t');
                if (parts.Length < 6)
                    return null;

                if (!int.TryParse(parts[4], out int duration) || duration <= 0)
                    return null;
                if (!int.TryParse(parts[5], out int position) || position < 0)
                    return null;

                return new PlayerInfo
                {
                    PlayerName = player,
                    Title = parts[0].Trim(),
                    Artist = parts[1].Trim(),
                    Album = parts[2].Trim(),
                    IsPlaying = parts[3].Trim().Equals("playing", StringComparison.OrdinalIgnoreCase),
                    Duration = duration,
                    Position = position
                };
            }
            catch (Exception ex)
            {
                Log($"[macOS] Error querying {player}: {ex.Message}");
                return null;
            }
        }

        private string GetSpotifyScript()
        {
            return @"
on run
    try
        tell application ""Spotify""
            set isRunning to running
        end tell
        
        if not isRunning then
            return """"
        end if
        
        tell application ""Spotify""
            set trackName to name of current track
            set artistName to artist of current track
            set albumName to album of current track
            set playerState to player state
            set trackDuration to duration of current track
            set trackPosition to player position
            
            set playState to ""stopped""
            if playerState is playing then
                set playState to ""playing""
            else if playerState is paused then
                set playState to ""paused""
            end if
            
            set output to trackName & ""\t"" & artistName & ""\t"" & albumName & ""\t"" & playState & ""\t"" & (trackDuration div 1000) & ""\t"" & (trackPosition as integer)
            return output
        end tell
    on error
        return """"
    end try
end run";
        }

        private string GetMusicScript()
        {
            return @"
on run
    try
        tell application ""Music""
            if not running then
                return """"
            end if
            
            set trackName to name of current track
            set artistName to artist of current track
            set albumName to album of current track
            set playerState to player state
            set trackDuration to duration of current track
            set trackPosition to player position
            
            set playState to ""stopped""
            if playerState is playing then
                set playState to ""playing""
            else if playerState is paused then
                set playState to ""paused""
            end if
            
            set output to trackName & ""\t"" & artistName & ""\t"" & albumName & ""\t"" & playState & ""\t"" & (trackDuration as integer) & ""\t"" & (trackPosition as integer)
            return output
        end tell
    on error
        return """"
    end try
end run";
        }

        private string GetItunesScript()
        {
            return @"
on run
    try
        tell application ""iTunes""
            if not running then
                return """"
            end if
            
            set trackName to name of current track
            set artistName to artist of current track
            set albumName to album of current track
            set playerState to player state
            set trackDuration to duration of current track
            set trackPosition to player position
            
            set playState to ""stopped""
            if playerState is playing then
                set playState to ""playing""
            else if playerState is paused then
                set playState to ""paused""
            end if
            
            set output to trackName & ""\t"" & artistName & ""\t"" & albumName & ""\t"" & playState & ""\t"" & (trackDuration as integer) & ""\t"" & (trackPosition as integer)
            return output
        end tell
    on error
        return """"
    end try
end run";
        }

        private async Task<string> RunAppleScriptAsync(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("osascript")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "-e", script }
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return "";

                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var result = await Task.WhenAny(outputTask, Task.Delay(5000));

                if (result != outputTask)
                {
                    try { proc.Kill(); }
                    catch { }
                    return "";
                }

                if (!proc.WaitForExit(1000))
                {
                    try { proc.Kill(); }
                    catch { }
                    return "";
                }

                return await outputTask;
            }
            catch
            {
                return "";
            }
        }

        public MediaPlaybackInfo GetPlaybackInfo()
        {
            lock (_updateLock)
            {
                return _currentPlaybackInfo;
            }
        }

        public MediaTimelineInfo GetTimelineInfo()
        {
            lock (_updateLock)
            {
                return _currentTimelineInfo;
            }
        }

        public async Task TryMediaControl(MediaControlType type)
        {
            if (string.IsNullOrEmpty(CurrentPlayerName))
                return;

            string script = type switch
            {
                MediaControlType.Play => GetPlayScript(),
                MediaControlType.Pause => GetPauseScript(),
                MediaControlType.Stop => GetStopScript(),
                MediaControlType.Skip => GetNextTrackScript(),
                MediaControlType.Previous => GetPreviousTrackScript(),
                _ => ""
            };

            if (!string.IsNullOrEmpty(script))
            {
                _ = await RunAppleScriptAsync(script);
            }
        }

        private string GetPlayScript()
        {
            return $@"
tell application ""{CurrentPlayerName}""
    play
end tell";
        }

        private string GetPauseScript()
        {
            return $@"
tell application ""{CurrentPlayerName}""
    pause
end tell";
        }

        private string GetStopScript()
        {
            return $@"
tell application ""{CurrentPlayerName}""
    stop
end tell";
        }

        private string GetNextTrackScript()
        {
            return $@"
tell application ""{CurrentPlayerName}""
    next track
end tell";
        }

        private string GetPreviousTrackScript()
        {
            return $@"
tell application ""{CurrentPlayerName}""
    previous track
end tell";
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private class PlayerInfo
        {
            public string PlayerName { get; set; } = "";
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public bool IsPlaying { get; set; }
            public int Duration { get; set; }
            public int Position { get; set; }
        }
    }
}
