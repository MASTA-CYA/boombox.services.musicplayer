using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.FileManagment;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Models;
using MusicServer.Helpers;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace MusicServer.Hubs
{
    public class LibraryHub : Hub
    {
        #region Public Methods
        public async Task GetLibraryAsync()
        {
            var libraryResponse = string.Empty;

            try
            {
                await ServerHttpClient.Instance.StartMappingBroadcastAsync();
                await SendMappingUpdate(message: "Attempting to get library mapping from cache");
                libraryResponse = await GetLibraryResponseFromFileAsync();
            }
            catch (Exception ex)
            {
                _ = Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetLibraryAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                await SendMappingUpdate(error: ex.Message);
                await SendMappingUpdate(message: "Unable to get library mapping from cache");

                libraryResponse = await GetLibraryResponseAsync();
                _ = Task.Run(async () => await SaveLibraryResponseToFileAsync(libraryResponse));
            }

            await ServerHttpClient.Instance.StopMappingBroadcastAsync();
            await Clients.All.SendAsync("ReceiveLibrary", libraryResponse.ToString());
        }

        public async Task UpdateSelectedAlbumAsync(string album, string path)
        {
            try
            {
                await RedisCache.SetAsync("SelectedAlbum", path);
            }
            finally
            {
                LibraryManager.Instance.SelectedAlbum = album;
            }
        }

        public async Task<string> GetSelectedAlbumAsync()
        {
            try
            {
                var path = await RedisCache.GetAsync("SelectedAlbum");
                var album = await MongoDbClient.Instance.GetAlbumAsync(path);
                var filePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
                var appDataFilePath = filePaths.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), album.Guid.ToString()));
                var fileJson = FileManager.Instance.Read(appDataFilePath);
                var cachedAlbum = JsonConvert.DeserializeObject<Album>(fileJson, settings: JsonSerializationHelper.FileSerializerSettings);
                return JsonConvert.SerializeObject(cachedAlbum, JsonSerializationHelper.NamingSerializerSettings);
            }
            catch (Exception ex)
            {
                _ = Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetSelectedAlbumAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex.Message);
                return LibraryManager.Instance.SelectedAlbum;
            }
        }

        public async Task MarkAsFavoriteAsync(string path)
        {
            _ = Task.Run(async () => await LibraryManager.Instance.UpdateIsFavoriteTrackAsync(path));
            await Task.CompletedTask;
        }

        public async Task ClearLocalCacheAsync()
        {
            _ = LibraryManager.Instance.ClearLocalCacheAsync();
            await Clients.All.SendAsync("ReceiveLocalCacheCleared");
        }

        #endregion Public Methods

        #region Private Methods
        private static async Task<string> GetLibraryResponseAsync()
        {
            var library = await LibraryManager.Instance.GetAlbumsAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(library, JsonSerializationHelper.NamingSerializerSettings);
        }

        private async Task SendMappingUpdate(string message = "", string error = "")
        {
            var libraryManager = LibraryManager.Instance;
            var mappingUpdate = libraryManager.MappingUpdate;

            if (!string.IsNullOrWhiteSpace(message))
                mappingUpdate.Message = message;

            if (!string.IsNullOrWhiteSpace(error))
                mappingUpdate.Error = error;

            var mappingUpdateJson = JsonConvert.SerializeObject(mappingUpdate, JsonSerializationHelper.NamingSerializerSettings);

            await Clients.All.SendAsync("ReceiveMappingUpdate", mappingUpdateJson);
        }

        private async Task SaveLibraryResponseToFileAsync(string libraryResponse)
        {
            try
            {
                await Task.CompletedTask;
                var albums = JsonConvert.DeserializeObject<List<Album>>(libraryResponse, settings: JsonSerializationHelper.FileSerializerSettings) ?? throw new Exception("No albums to save");
                Parallel.ForEach(albums, (album, token) => { FileManager.Instance.Write(album); });
            }
            catch (Exception ex)
            {
                _ = Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "SaveLibraryResponseToFileAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine($"Unable to save album to file: {ex.Message}");
            }
        }

        private static async Task<string> GetLibraryResponseFromFileAsync()
        {
            try
            {
                ConcurrentBag<Album> albums = [];
                var filepaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
                LibraryManager.Instance.MappingUpdate.DirectoryCount = filepaths.Count;
                await Parallel.ForEachAsync(filepaths, async (file, token) =>
                {
                    await Task.CompletedTask;
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _)) return;
                    var json = FileManager.Instance.Read(file);
                    var album = JsonConvert.DeserializeObject<Album>(json, settings: JsonSerializationHelper.FileSerializerSettings);
                    if (album == null)
                        return;
                    albums.Add(album);

                    LibraryManager.Instance.MappingUpdate.Message = $"Mapped {album.Path}";
                    LibraryManager.Instance.MappingUpdate.MappedDirectories = albums.Count;

                });
                if (albums.Count == 0) throw new Exception("No matching files found in application directory");

                return JsonConvert.SerializeObject(albums, JsonSerializationHelper.NamingSerializerSettings);
            }
            catch (Exception ex)
            {
                _ = Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetLibraryResponseFromFileAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine($"Unable to get saved albums: {ex.Message}");
                throw;
            }
        }

        private RedisValue GetRedisSetValue(Album album) => new(JsonConvert.SerializeObject(album, JsonSerializationHelper.NamingSerializerSettings));


        #endregion Private Methods
    }
}
