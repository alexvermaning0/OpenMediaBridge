using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace OpenMediaBridge.Services
{
    public static class CoverServer
    {
        // Public cover URL (from iTunes/Deezer)
        private static string _publicCoverUrl = "";
        private static readonly HttpClient _httpClient = new HttpClient();
        
        // Default cover image when none found
        private const string DEFAULT_COVER_URL = "https://demo.tutorialzine.com/2015/03/html5-music-player/assets/img/default.png";

        static CoverServer()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "OpenMediaBridge");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        public static void Start(int port = 8081)
        {
            // No longer needed - using public cover art APIs
        }

        public static void Stop()
        {
            // No longer needed
        }

        /// <summary>
        /// Update cover - returns the new cover URL (waits for fetch to complete)
        /// </summary>
        public static async Task<string> UpdateCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties mediaProps)
        {
            if (mediaProps == null)
            {
                _publicCoverUrl = DEFAULT_COVER_URL;
                return _publicCoverUrl;
            }

            var title = mediaProps.Title ?? "";
            var artist = mediaProps.Artist ?? "";

            // Fetch new cover URL
            var newUrl = await GetPublicCoverUrl(title, artist, mediaProps.AlbumTitle);
            
            _publicCoverUrl = string.IsNullOrEmpty(newUrl) ? DEFAULT_COVER_URL : newUrl;

            return _publicCoverUrl;
        }

        /// <summary>
        /// Legacy sync method - just updates without waiting
        /// </summary>
        public static async Task UpdateCover(GlobalSystemMediaTransportControlsSessionMediaProperties mediaProps)
        {
            await UpdateCoverAsync(mediaProps);
        }

        /// <summary>
        /// Try to get a public cover URL from iTunes or Deezer
        /// </summary>
        private static async Task<string> GetPublicCoverUrl(string title, string artist, string album)
        {
            // Try iTunes first
            var itunesUrl = await GetITunesCover(title, artist, album);
            if (!string.IsNullOrEmpty(itunesUrl))
                return itunesUrl;

            // Try Deezer as fallback
            var deezerUrl = await GetDeezerCover(title, artist);
            if (!string.IsNullOrEmpty(deezerUrl))
                return deezerUrl;

            return "";
        }

        private static async Task<string> GetITunesCover(string title, string artist, string album)
        {
            try
            {
                // Search by track + artist
                var query = Uri.EscapeDataString($"{title} {artist}");
                var url = $"https://itunes.apple.com/search?term={query}&media=music&entity=song&limit=5";
                
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                
                var results = doc.RootElement.GetProperty("results");
                
                foreach (var result in results.EnumerateArray())
                {
                    // Try to match artist name
                    var resultArtist = result.GetProperty("artistName").GetString() ?? "";
                    var resultTrack = result.GetProperty("trackName").GetString() ?? "";
                    
                    // Basic matching - case insensitive contains
                    if (resultArtist.Contains(artist, StringComparison.OrdinalIgnoreCase) ||
                        artist.Contains(resultArtist, StringComparison.OrdinalIgnoreCase) ||
                        resultTrack.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        var artworkUrl = result.GetProperty("artworkUrl100").GetString() ?? "";
                        
                        // Get higher resolution (600x600)
                        if (!string.IsNullOrEmpty(artworkUrl))
                        {
                            return artworkUrl.Replace("100x100", "600x600");
                        }
                    }
                }
            }
            catch { }

            return "";
        }

        private static async Task<string> GetDeezerCover(string title, string artist)
        {
            try
            {
                var query = Uri.EscapeDataString($"{title} {artist}");
                var url = $"https://api.deezer.com/search?q={query}&limit=5";
                
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                
                var data = doc.RootElement.GetProperty("data");
                
                foreach (var result in data.EnumerateArray())
                {
                    var resultArtist = result.GetProperty("artist").GetProperty("name").GetString() ?? "";
                    var resultTitle = result.GetProperty("title").GetString() ?? "";
                    
                    if (resultArtist.Contains(artist, StringComparison.OrdinalIgnoreCase) ||
                        artist.Contains(resultArtist, StringComparison.OrdinalIgnoreCase) ||
                        resultTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        var albumObj = result.GetProperty("album");
                        var coverUrl = albumObj.GetProperty("cover_xl").GetString() ?? 
                                      albumObj.GetProperty("cover_big").GetString() ?? "";
                        
                        if (!string.IsNullOrEmpty(coverUrl))
                            return coverUrl;
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// Get the current cover URL
        /// </summary>
        public static string GetCurrentCoverUrl()
        {
            return string.IsNullOrEmpty(_publicCoverUrl) ? DEFAULT_COVER_URL : _publicCoverUrl;
        }

        public static bool HasCover => !string.IsNullOrEmpty(_publicCoverUrl) && _publicCoverUrl != DEFAULT_COVER_URL;
    }
}
