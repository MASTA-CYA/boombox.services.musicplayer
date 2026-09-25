using Microsoft.Extensions.Logging;
using MusicPlayer.Common;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace MusicPlayer.LyricsManagement
{
    // Thin wrapper around lrclib.net's public GET /api/get endpoint - the free, open, purpose-built synced-
    // lyrics database chosen as the app's primary lyrics source. Every other "convenient" lyrics API turns out
    // to just be scraping Genius/Musixmatch/NetEase under the hood (against their terms of service); LRCLIB is
    // the one actually built and licensed for this, with no API key required for lookups.
    internal static class LrclibClient
    {
        private const string BASE_URL = "https://lrclib.net/api/get";

        private static readonly ILogger _logger = AppLogger.CreateLogger(typeof(LrclibClient).FullName);
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // LRCLIB asks API consumers to identify themselves via User-Agent rather than requiring an API key
            // - reasonable to honor given this is a free, donation-run service; this is a low-volume, single-
            // user, self-hosted client (one lazy lookup per track, cached forever after - see LyricsManager),
            // not a redistributed public service making bulk requests.
            client.DefaultRequestHeaders.Add("User-Agent", "Boombox/1.0 (self-hosted personal music player)");

            return client;
        }

        // Returns null both when nothing matches (LRCLIB returns 404, not an error body, for "no result" -
        // that's not a failure worth logging, just "nothing found from this source" for LyricsManager to fall
        // through past) and on any genuine network/parse failure (logged as a Warning and swallowed - a lookup
        // failing here shouldn't block the other fallback sources from being tried).
        public static async Task<LrclibResponse> GetAsync(string trackName, string artistName, string albumName, double durationSeconds)
        {
            try
            {
                var url = BASE_URL
                    + $"?track_name={Uri.EscapeDataString(trackName ?? string.Empty)}"
                    + $"&artist_name={Uri.EscapeDataString(artistName ?? string.Empty)}"
                    + (string.IsNullOrWhiteSpace(albumName) ? string.Empty : $"&album_name={Uri.EscapeDataString(albumName)}")
                    + $"&duration={(int)Math.Round(durationSeconds)}";

                using (var response = await _httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode) return null;

                    var json = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<LrclibResponse>(json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to query LRCLIB for \"{TrackName}\" by \"{ArtistName}\"", trackName, artistName);
                return null;
            }
        }
    }
}
