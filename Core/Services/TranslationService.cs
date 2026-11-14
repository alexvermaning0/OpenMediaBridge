using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OpenMediaBridge.Lyrics.Fetchers;

namespace OpenMediaBridge.Services
{
    public static class TranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Translates a list of lyric lines via a LibreTranslate-compatible endpoint.
        /// All lines are sent in a single request (joined by \n) to minimise API calls.
        /// Original timestamps are preserved on the returned lines.
        /// </summary>
        public static async Task<List<LyricsLine>> TranslateLyricsAsync(
            List<LyricsLine> lines,
            string targetLang,
            string endpoint,
            string apiKey = "")
        {
            if (lines == null || lines.Count == 0)
                return new List<LyricsLine>();

            var texts = lines.Select(l => string.IsNullOrWhiteSpace(l.Text) ? " " : l.Text).ToList();
            var joined = string.Join("\n", texts);

            var requestBody = new TranslateRequest
            {
                Q = joined,
                Source = "auto",
                Target = targetLang,
                Format = "text",
                ApiKey = apiKey ?? ""
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = endpoint.TrimEnd('/') + "/translate";
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TranslateResponse>(responseJson);

            var translatedTexts = result?.TranslatedText?.Split('\n') ?? Array.Empty<string>();

            var output = new List<LyricsLine>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                output.Add(new LyricsLine
                {
                    Time = lines[i].Time,
                    Text = i < translatedTexts.Length ? translatedTexts[i].Trim() : lines[i].Text
                });
            }
            return output;
        }

        private class TranslateRequest
        {
            [JsonPropertyName("q")]
            public string Q { get; set; }
            [JsonPropertyName("source")]
            public string Source { get; set; }
            [JsonPropertyName("target")]
            public string Target { get; set; }
            [JsonPropertyName("format")]
            public string Format { get; set; }
            [JsonPropertyName("api_key")]
            public string ApiKey { get; set; }
        }

        private class TranslateResponse
        {
            [JsonPropertyName("translatedText")]
            public string TranslatedText { get; set; }
        }
    }
}
