using Microsoft.Extensions.Logging;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement.Models;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MusicPlayer.Common
{
    public class ServerHttpClient
    {
        private const string START_PLAYBACK_INFORMATION = "Broadcast/StartPlaybackInformation";
        private const string STOP_PLAYBACK_INFORMATION = "Broadcast/StopPlaybackInformation";
        private const string START_PLAYLIST_INFORMATION = "Broadcast/StartPlaylistUpdates";
        private const string STOP_PLAYLIST_INFORMATION = "Broadcast/StopPlaylistUpdates";
        private const string START_SERVER_STATUS_UPDATES = "Broadcast/StartServerUpdates";
        private const string STOP_SERVER_STATUS_UPDATES = "Broadcast/StopServerUpdates";
        private const string POST_TRACK_USER_DATA = "MetaData/UpdateTrackUserData";

        private static readonly ILogger _logger = AppLogger.CreateLogger<ServerHttpClient>();

        private readonly HttpClient _httpClient;

        #region Singleton
        private static readonly Lazy<ServerHttpClient> _instance = new Lazy<ServerHttpClient>(() => new ServerHttpClient());
        public static ServerHttpClient Instance { get => _instance.Value; }

        private ServerHttpClient()
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("http://localhost:7280/"),
            };
        }

        #endregion Singleton

        public async Task StartPlaybackInformationBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{START_PLAYBACK_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start playback information broadcast");
            }
        }

        public async Task StopPlaybackInformationBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{STOP_PLAYBACK_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to stop playback information broadcast");
            }
        }

        public async Task StartPlaylistBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{START_PLAYLIST_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start playlist broadcast");
            }
        }

        public async Task StopPlaylistBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{STOP_PLAYLIST_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to stop playlist broadcast");
            }
        }

        public async Task UpdateTrackerUserDataAsync(TrackUserData data)
        {
            try
            {
                var json = JsonConvert.SerializeObject(data, JsonSerializationHelper.NamingSerializerSettings);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"api/{POST_TRACK_USER_DATA}", content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to update track user data for {TrackPath}", data?.Path);
            }
        }

        public async Task StartServerStatusUpdatesAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{START_SERVER_STATUS_UPDATES}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start server status updates");
            }
        }

        public async Task StopServerStatusUpdatesAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{STOP_SERVER_STATUS_UPDATES}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to stop server status updates");
            }
        }
    }
}
