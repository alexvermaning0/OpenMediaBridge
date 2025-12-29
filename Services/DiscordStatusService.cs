using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenMediaBridge.Services
{
    /// <summary>
    /// Optional Discord status updater - shows current lyrics in Discord custom status.
    /// Only active if discord_token is set in config.
    /// </summary>
    public class DiscordStatusService
    {
        private readonly HttpClient _httpClient;
        private readonly string _token;
        private readonly string _emoji;
        
        private string _lastLyric = "";
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _enabled = false;

        public bool IsEnabled => _enabled;

        public DiscordStatusService(Config config)
        {
            _token = config?.DiscordToken ?? "";
            _emoji = config?.DiscordEmoji ?? "🎶";
            
            if (!string.IsNullOrEmpty(_token))
            {
                _httpClient = new HttpClient();
                _httpClient.DefaultRequestHeaders.Add("Authorization", _token);
                _enabled = true;
            }
        }

        /// <summary>
        /// Update Discord status with current lyric line
        /// </summary>
        public async Task UpdateLyric(string lyric)
        {
            if (!_enabled) return;
            
            // Handle empty/null lyrics - clear status
            if (string.IsNullOrWhiteSpace(lyric))
            {
                if (!string.IsNullOrEmpty(_lastLyric))
                {
                    await ClearStatus();
                    _lastLyric = "";
                }
                return;
            }

            // Strip color tags if present (from word sync)
            lyric = lyric
                .Replace("<color=yellow>", "")
                .Replace("<color=white>", "")
                .Replace("</color>", "")
                .Trim();

            if (string.IsNullOrWhiteSpace(lyric))
            {
                if (!string.IsNullOrEmpty(_lastLyric))
                {
                    await ClearStatus();
                    _lastLyric = "";
                }
                return;
            }

            // Rate limiting - don't update more than once per second
            if ((DateTime.UtcNow - _lastUpdate).TotalMilliseconds < 1000)
                return;

            // Only update if lyric changed
            if (lyric == _lastLyric)
                return;

            _lastLyric = lyric;
            _lastUpdate = DateTime.UtcNow;

            // Format status (no prefix needed - Discord shows emoji separately)
            string status = lyric;
            
            // Discord status limit is 128 characters
            if (status.Length > 128)
                status = status.Substring(0, 125) + "...";

            await SetStatus(status);
        }

        private async Task SetStatus(string text)
        {
            try
            {
                var requestBody = new
                {
                    custom_status = new
                    {
                        text = text,
                        emoji_name = _emoji,
                        emoji_id = (string)null,
                        expires_at = DateTime.UtcNow.AddMinutes(5).ToString("o")
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Patch, "https://discord.com/api/v10/users/@me/settings")
                {
                    Content = content
                };

                var response = await _httpClient.SendAsync(request);
            }
            catch { }
        }

        private async Task ClearStatus()
        {
            try
            {
                var requestBody = new
                {
                    custom_status = (object)null
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Patch, "https://discord.com/api/v10/users/@me/settings")
                {
                    Content = content
                };

                await _httpClient.SendAsync(request);
            }
            catch { }
        }

        /// <summary>
        /// Clear status on shutdown
        /// </summary>
        public async Task Stop()
        {
            if (_enabled)
            {
                await ClearStatus();
            }
        }
    }
}
