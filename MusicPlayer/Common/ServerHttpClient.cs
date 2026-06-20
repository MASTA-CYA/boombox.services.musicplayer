using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Models;
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
        private const string START_MAPPING_INFORMATION = "Broadcast/StartMappingUpdates";
        private const string STOP_MAPPING_INFORMATION = "Broadcast/StopMappingUpdates";
        private const string POST_TRACK_USER_DATA = "MetaData/UpdateTrackUserData";
        private const string POST_LOG_ENTRY = "Logging/LogEntry";

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

        public async Task LogEntryAsync(LogEntry entry)
        {
            try
            {
                var json = JsonConvert.SerializeObject(entry, JsonSerializationHelper.NamingSerializerSettings);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"api/{POST_LOG_ENTRY}", content);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public async Task StartPlaybackInformationBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{START_PLAYBACK_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StartPlaybackInformationBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
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
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StopPlaybackInformationBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
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
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StartPlaylistBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
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
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StopPlaylistBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }

        public async Task StartMappingBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{START_MAPPING_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StartMappingBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }

        public async Task StopMappingBroadcastAsync()
        {
            try
            {
                await _httpClient.PostAsync($"api/{STOP_MAPPING_INFORMATION}", null);
            }
            catch (Exception ex)
            {
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "StopMappingBroadcastAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
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
                await LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "UpdateTrackerUserDataAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }
    }
}
