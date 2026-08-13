using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicServer.Helpers;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace MusicServer.Hubs
{
    public class LibraryHub(ILogger<LibraryHub> logger) : Hub
    {
        private readonly ILogger<LibraryHub> _logger = logger;

        #region Public Methods
        public async Task GetLibraryAsync()
        {
            var libraryResponse = string.Empty;

            try
            {
                await SendMappingUpdate(message: "Attempting to get library mapping from cache");
                libraryResponse = await GetLibraryResponseFromFileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to get library mapping from cache");
                await SendMappingUpdate(error: ex.Message);
                await SendMappingUpdate(message: "Unable to get library mapping from cache");

                libraryResponse = await GetLibraryResponseAsync();
                _ = Task.Run(async () => await SaveLibraryResponseToFileAsync(libraryResponse));
            }

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
                _logger.LogError(ex, "Unable to get selected album");
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

        public async Task RefreshAlbumAsync(string path)
        {
            await LibraryManager.Instance.RefreshAlbumAsync(path);
            await Clients.All.SendAsync("ReceiveRefreshedAlbum");
        }

        // Feeds the Settings > Mapping Statistics tab. One-shot request/response like GetSelectedAlbumAsync, not a
        // broadcast — nothing else needs to know when history is fetched, so it's a plain return value rather than
        // a Clients.All.SendAsync.
        public async Task<string> GetMappingHistoryAsync(int limit = 50)
        {
            try
            {
                var statistics = await MongoDbClient.Instance.GetMappingStatisticsAsync(limit);
                return JsonConvert.SerializeObject(statistics, JsonSerializationHelper.NamingSerializerSettings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to get mapping history");
                return JsonConvert.SerializeObject(new List<MappingStatistic>(), JsonSerializationHelper.NamingSerializerSettings);
            }
        }

        #endregion Public Methods

        #region Private Methods
        private static async Task<string> GetLibraryResponseAsync()
        {
            var library = await LibraryManager.Instance.GetAlbumsAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(library, JsonSerializationHelper.NamingSerializerSettings);
        }

        // Just mutates MappingUpdate now — the Changed event (wired up once in MusicServer.Program.cs) pushes the
        // broadcast itself, so this no longer needs to serialize/send anything directly. Kept as async Task so
        // existing call sites don't need to change.
        private async Task SendMappingUpdate(string message = "", string error = "")
        {
            await Task.CompletedTask;

            var mappingUpdate = LibraryManager.Instance.MappingUpdate;

            if (!string.IsNullOrWhiteSpace(message))
                mappingUpdate.Message = message;

            if (!string.IsNullOrWhiteSpace(error))
                mappingUpdate.Error = error;
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
                _logger.LogError(ex, "Unable to save album to file");
            }
        }

        // No longer static — it needs the injected _logger, which is instance state.
        //
        // This is the common path on every normal startup (LibraryManager.GetAlbums, the full disk/metadata scan,
        // only runs when this throws - see GetLibraryAsync's catch block). It didn't used to set StartedAtUtc at
        // all, which meant the live progress screen - gated on mappingUpdate.startedAtUtc - never rendered for a
        // cache load, and MappingUpdateBroadcast never had a completed run to persist to history either. Now it
        // resets/starts/completes MappingUpdate the same way GetAlbums does, tagged RunType.Cache so the live view
        // and the run-history table can tell the two apart. In practice this takes roughly 10 seconds (deserializing
        // every cached album JSON file), not the sub-second turnaround originally assumed - long enough to warrant
        // the same once-a-second CPU/memory Timer GetAlbums uses, rather than a single one-shot reading.
        private async Task<string> GetLibraryResponseFromFileAsync()
        {
            var mappingUpdate = LibraryManager.Instance.MappingUpdate;

            try
            {
                mappingUpdate.IsComplete = false;
                mappingUpdate.Error = null;
                mappingUpdate.RunType = MappingRunType.Cache;
                mappingUpdate.MappedDirectories = 0;
                mappingUpdate.StartedAtUtc = DateTime.UtcNow;

                var process = Process.GetCurrentProcess();
                var lastCpuTime = process.TotalProcessorTime;
                var lastSampleAt = DateTime.UtcNow;

                using (new Timer(_ =>
                {
                    try
                    {
                        process.Refresh();
                        var now = DateTime.UtcNow;
                        var cpuTime = process.TotalProcessorTime;

                        var cpuTimeDeltaMs = (cpuTime - lastCpuTime).TotalMilliseconds;
                        var wallDeltaMs = (now - lastSampleAt).TotalMilliseconds;
                        var cpuPercent = wallDeltaMs > 0 ? (cpuTimeDeltaMs / wallDeltaMs / Environment.ProcessorCount) * 100 : 0;

                        lastCpuTime = cpuTime;
                        lastSampleAt = now;

                        mappingUpdate.CpuPercent = Math.Round(cpuPercent, 1);
                        mappingUpdate.MemoryMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to sample resource usage");
                    }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                {
                    ConcurrentBag<Album> albums = [];
                    var filepaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
                    mappingUpdate.DirectoryCount = filepaths.Count;
                    await Parallel.ForEachAsync(filepaths, async (file, token) =>
                    {
                        await Task.CompletedTask;
                        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _)) return;
                        var json = FileManager.Instance.Read(file);
                        var album = JsonConvert.DeserializeObject<Album>(json, settings: JsonSerializationHelper.FileSerializerSettings);
                        if (album == null)
                            return;
                        albums.Add(album);

                        mappingUpdate.Message = $"Mapped {album.Path}";
                        mappingUpdate.MappedDirectories = albums.Count;

                    });
                    if (albums.Count == 0) throw new Exception("No matching files found in application directory");

                    mappingUpdate.IsComplete = true;
                    return JsonConvert.SerializeObject(albums, JsonSerializationHelper.NamingSerializerSettings);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to get saved albums");
                throw;
            }
        }

        private RedisValue GetRedisSetValue(Album album) => new(JsonConvert.SerializeObject(album, JsonSerializationHelper.NamingSerializerSettings));


        #endregion Private Methods
    }
}
