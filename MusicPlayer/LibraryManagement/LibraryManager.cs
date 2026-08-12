using MediaInfo;
using MongoDB.Bson;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Models;
using MusicPlayer.PlaylistManagement.Models;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TagLibSharp2.Core;

namespace MusicPlayer.LibraryManagement
{
    public sealed class LibraryManager
    {
        private readonly List<string> _audioFileExtensions = new List<string> { ".mp3", ".wav", ".flac", ".m4a" };
        private readonly List<string> _imageFileExtensions = new List<string> { ".png", ".jpg", ".webp", ".jpeg" };
        private readonly List<string> _directoryExclusions = new List<string> { "beats", "Edits", "iTunes", "Playlists", "Dump", "Resampled Providers", "Staging" };

        public MappingUpdate MappingUpdate { get; set; }
        public string SelectedAlbum { get; set; }

        public event EventHandler<FavouriteTrackEventArgs> UpdatedFavouriteTrack;


        #region Singleton
        private static readonly Lazy<LibraryManager> _instance = new Lazy<LibraryManager>(() => new LibraryManager());
        public static LibraryManager Instance { get => _instance.Value; }

        private LibraryManager()
        {
            MappingUpdate = new MappingUpdate();
        }

        #endregion Singleton

        #region Public Functions
        public async Task<List<Album>> GetAlbumsAsync()
        {
            var databaseAlbums = await GetCachedDatabaseAlbumsAsync();
            var cachedFavouriteTracks = (await MongoDbClient.Instance.GetPlaylistAsync("Favourite"))?.Tracks;
            var mappedAlbums = await Task.Run(() => GetAlbums(databaseAlbums, cachedFavouriteTracks));
            _ = Task.Run(async () => await UpsertMappedAlbumsAsync(mappedAlbums));
            return mappedAlbums.ToList();
        }

        public PlaylistTrack GetTrackInformation(string filePath)
        {
            var directory = Directory.GetParent(filePath);
            var files = Directory.EnumerateFiles(directory.FullName, "*.*", SearchOption.AllDirectories);

            var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
            var imageFile = GetHighestResolutionImage(imageFiles);
            string base64Image = null;

            if (!string.IsNullOrWhiteSpace(imageFile))
            {
                var imageBytes = File.ReadAllBytes(imageFile);
                base64Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
            }

            var mediaInfo = new MediaInfoWrapper(filePath);
            var savedAlbum = Task.Run(async () => await MongoDbClient.Instance.GetAlbumAsync(directory.FullName)).GetAwaiter().GetResult();
            var isFavourite = savedAlbum.Tracks.Find(track => string.Equals(track.Path, filePath)).IsFavourite;
            var preset = Task.Run(async () => await MongoDbClient.Instance.GetEqualizerPresetAsync(filePath)).GetAwaiter().GetResult();

            return new PlaylistTrack
            {
                Image = base64Image,
                Album = mediaInfo.Tags.Album,
                Name = mediaInfo.Tags.Track,
                Artist = GetAlbumArtist(mediaInfo.Tags.AlbumArtist, mediaInfo.Tags.Artist, "Unknown"),
                IsFavourite = isFavourite,
                TotalDuration = GetTrackDuration(mediaInfo.Duration, filePath),
                Path = filePath,
                EqualizerPreset = preset,
            };
        }

        public async Task UpdateTrackPlayedAsync(string path)
        {
            var albumPath = Path.GetDirectoryName(path);
            await MongoDbClient.Instance.UpdateTimesPlayedAsync(albumPath: albumPath, trackPath: path);

            var filePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
            var cachedAlbum = await MongoDbClient.Instance.GetAlbumAsync(albumPath);
            var appDataPath = filePaths.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), cachedAlbum.Guid.ToString()));
            var albumFileContent = FileManager.Instance.Read(appDataPath);
            var album = JsonConvert.DeserializeObject<Album>(albumFileContent, settings: JsonSerializationHelper.FileSerializerSettings);
            var playedTrack = album.Tracks.Find(track => string.Equals(track.Path, path));
            playedTrack.TimesPlayed = cachedAlbum.Tracks.Find(track => string.Equals(track.Path, path)).TimesPlayed;
            FileManager.Instance.Write(album);

            await UpdateTrackUserDataAsync(path);
        }

        public async Task UpdateIsFavoriteTrackAsync(string path)
        {
            try
            {
                var (isFasvourite, guid) = await MongoDbClient.Instance.UpdateIsFavouriteAsync(albumPath: Path.GetDirectoryName(path), trackPath: path);
                UpdatedFavouriteTrack?.Invoke(this, new FavouriteTrackEventArgs(path, isFasvourite));

                var track = ((await MongoDbClient.Instance.GetPlaylistAsync("Favourite"))?.Tracks?.Find(playlistTrack => string.Equals(playlistTrack.Path, path)))
                    ?? GetTrackInformation(path);

                await UpdateTrackUserDataAsync(path);

                if (isFasvourite)
                    await MongoDbClient.Instance.AddPlaylistTracksAsync("Favourite", new List<PlaylistTrack> { track });
                else
                    await MongoDbClient.Instance.RemovePlaylistTracksAsync("Favourite", new List<PlaylistTrack> { track });

                var appDataPath = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN).FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), guid));
                var albumFileContent = FileManager.Instance.Read(appDataPath);
                var album = JsonConvert.DeserializeObject<Album>(albumFileContent, settings: JsonSerializationHelper.FileSerializerSettings);
                var favouriteTrack = album.Tracks.Find(albumTrack => string.Equals(albumTrack.Path, path));
                favouriteTrack.IsFavourite = isFasvourite;
                FileManager.Instance.Write(album);
            }
            catch (Exception ex)
            {
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "UpdateIsFavoriteTrackAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }

        public void HandleNewAlbumAdded()
        {
            // Fires off a bounded set of real awaited tasks instead of Parallel.ForEach with an async lambda —
            // Parallel.ForEach treats an async delegate as "fire and forget" (it matches Action<T> as async void
            // under the hood), so it was never actually waiting for MapCreatedAlbumAsync to finish, and any
            // exception thrown inside it would have become an unobserved async-void exception.
            Task.Run(async () =>
            {
                using (var throttle = new SemaphoreSlim(Environment.ProcessorCount))
                {
                    var mappingTasks = GetUnmappedDirectories().Select(async directory =>
                    {
                        await throttle.WaitAsync();
                        try { await MapCreatedAlbumAsync(directory); }
                        finally { throttle.Release(); }
                    });
                    await Task.WhenAll(mappingTasks);
                }
            });
        }

        public async Task MapCreatedAlbumAsync(string path)
        {
            try
            {
                var album = await GetCreatedAlbumAsync(path);
                if (album == null) return;
                await MongoDbClient.Instance.InsertAlbumAsync(album);
                FileManager.Instance.Write(album);
            }
            catch (Exception ex)
            {
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "MapCreatedAlbumAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }

        public void HandleDeletedAlbums()
        {
            // Same fix as HandleNewAlbumAdded — Parallel.ForEach with an async lambda never actually waits for the
            // work inside it, so this was previously firing off untracked, unbounded async operations.
            Task.Run(async () =>
            {
                using (var throttle = new SemaphoreSlim(Environment.ProcessorCount))
                {
                    var deletionTasks = GetUnmappedDirectories().Select(async directory =>
                    {
                        await throttle.WaitAsync();
                        try
                        {
                            var cachedAlbum = await MongoDbClient.Instance.GetAlbumAsync(directory);

                            if (cachedAlbum != null)
                                await MongoDbClient.Instance.DeleteAlbumAsync(directory);

                            var appDataPath = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN).FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), cachedAlbum?.Guid.ToString()));
                            if (appDataPath != null)
                                FileManager.Instance.RemoveFile(appDataPath);
                        }
                        catch (Exception ex)
                        {
                            await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                            {
                                Severity = Severity.Error,
                                Source = "HandleDeletedAlbums",
                                Line = ex.Message,
                                TimeStamp = DateTime.Now,
                                Exception = ex
                            });
                            Console.WriteLine(ex.Message);
                        }
                        finally
                        {
                            throttle.Release();
                        }
                    });
                    await Task.WhenAll(deletionTasks);
                }
            });
        }

        public async Task ClearLocalCacheAsync()
        {
            await Task.CompletedTask;
            FileManager.Instance.ClearDirectory(null, Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
        }

        public async Task RefreshAlbumAsync(string path)
        {
            var databaseAlbum = await GetCachedDatabaseAlbumAsync(path);
            var cachedFavouriteTracks = (await MongoDbClient.Instance.GetPlaylistAsync("Favourite"))?.Tracks;

            var mappedAlbum = RefreshMappedAlbum(path, databaseAlbum, cachedFavouriteTracks);
            SelectedAlbum = JsonConvert.SerializeObject(mappedAlbum, JsonSerializationHelper.NamingSerializerSettings);
            _ = Task.Run(async () => await UpsertMappedAlbumsAsync(new ConcurrentBag<Album> { mappedAlbum }));
            _ = Task.Run(() => FileManager.Instance.Write(mappedAlbum));
        }

        #endregion Public Functions

        #region Private Functions

        private ConcurrentBag<Album> GetAlbums(List<Album> databaseAlbums, List<PlaylistTrack> cachedFavouriteTracks)
        {
            // Reset from any previous run so a stale IsComplete/Error doesn't linger into this one — MusicServer's
            // MappingUpdateBroadcast treats IsComplete transitioning to true as "this run just finished, persist
            // it," so a leftover true here would make every update in a fresh run look like a completed one.
            MappingUpdate.IsComplete = false;
            MappingUpdate.Error = null;
            MappingUpdate.StartedAtUtc = DateTime.UtcNow;

            var process = Process.GetCurrentProcess();
            var lastCpuTime = process.TotalProcessorTime;
            var lastSampleAt = DateTime.UtcNow;

            // Process-level (not system-wide) CPU/memory, sampled once a second for the duration of this rescan
            // only — bounded lifetime, stopped via Dispose() below once mapping finishes or throws. Unlike the old
            // BroadcastController polling loop this isn't a fixed unconditional broadcast; it's a short-lived
            // sampler feeding MappingUpdate the same way any other mutation does.
            using (new Timer(_ =>
            {
                try
                {
                    process.Refresh();
                    var now = DateTime.UtcNow;
                    var cpuTime = process.TotalProcessorTime;

                    var cpuTimeDeltaMs = (cpuTime - lastCpuTime).TotalMilliseconds;
                    var wallDeltaMs = (now - lastSampleAt).TotalMilliseconds;
                    // Normalized 0-100 (divided by logical processor count) rather than allowed to run past 100 on
                    // a multi-core machine.
                    var cpuPercent = wallDeltaMs > 0 ? (cpuTimeDeltaMs / wallDeltaMs / Environment.ProcessorCount) * 100 : 0;

                    lastCpuTime = cpuTime;
                    lastSampleAt = now;

                    MappingUpdate.CpuPercent = Math.Round(cpuPercent, 1);
                    MappingUpdate.MemoryMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unable to sample resource usage: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            try
            {
                MappingUpdate.Message = $"Mapping target directory: {Constants.LIBRARY_DIRECTORY}";

                var directories = Directory.EnumerateDirectories(Constants.LIBRARY_DIRECTORY, "*.*", SearchOption.TopDirectoryOnly);
                ConcurrentBag<Album> albums = new ConcurrentBag<Album>();
                MappingUpdate.DirectoryCount = directories.Count();

                // Directories are processed sequentially; parallelism happens once, below, at the file level,
                // bounded to the machine's actual logical processor count. The old code parallelized both this
                // loop AND the file loop inside it, which oversubscribes the thread pool instead of speeding
                // things up (two unbounded parallel levels stacked on top of each other).
                foreach (var directory in directories)
                {
                    MappingUpdate.Message = $"Mapping directory: {directory}";

                    var directoryInfo = new DirectoryInfo(directory);
                    var directoryName = directoryInfo.Name;
                    if (_directoryExclusions.Contains(directoryName)) continue;

                    if (string.Equals(directoryName, "Singles"))
                    {
                        var savedSinglesAlbum = databaseAlbums?.Find(dbAlbum => string.Equals(dbAlbum.Path, directory));
                        var singlesAlbum = MapSinglesAlbum(directory, savedSinglesAlbum, cachedFavouriteTracks, directoryInfo);
                        albums.Add(singlesAlbum);
                        continue;
                    }

                    // Materialized once and reused for image lookup, the first-audio-file lookup below, and the
                    // parallel pass — the old code enumerated the directory twice (once via .Where().ToList() for
                    // images, once again for the Parallel.ForEach) without ever storing the result.
                    var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).ToList();
                    if (!files.Any()) continue;

                    var savedAlbum = databaseAlbums?.Find(dbAlbum => string.Equals(dbAlbum.Path, directory));

                    var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
                    var imageFile = GetHighestResolutionImage(imageFiles);

                    var firstAudioFile = files.FirstOrDefault(file => _audioFileExtensions.Contains(Path.GetExtension(file).ToLower()));
                    if (firstAudioFile == null) continue;

                    // Album-level metadata is built once, deterministically, from a single designated file before
                    // the parallel pass starts. The old code built it lazily inside the parallel loop via
                    // "if (album == null) album = ...", which is a data race — multiple file threads could pass
                    // that null check simultaneously and each construct a competing Album.
                    var album = MapAlbumMetaData(new MediaInfoWrapper(firstAudioFile), firstAudioFile, savedAlbum, directoryInfo.CreationTime);
                    ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();

                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
                    {
                        if (!_audioFileExtensions.Contains(Path.GetExtension(file).ToLower())) return;

                        try
                        {
                            var mediaInfo = new MediaInfoWrapper(file);
                            var savedtrack = savedAlbum?.Tracks.Find(track => string.Equals(track.Path, file));
                            // Awaited directly instead of via Task.Run(...).GetAwaiter().GetResult() — the old code
                            // queued a second thread-pool thread just to block-wait on it, doubling the thread cost
                            // of every Mongo lookup on top of the Parallel.ForEach worker already blocked here.
                            var presetGuid = GetTrackEqualizerPresetGuidAsync(file, savedtrack?.EqualizerGuid ?? Guid.Empty).GetAwaiter().GetResult();
                            var isFavourite = cachedFavouriteTracks?.Any(playlistTrack => string.Equals(playlistTrack.Path, file)) ?? false;
                            tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, savedtrack, presetGuid, isFavourite));
                        }
                        catch (Exception ex)
                        {
                            Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                            {
                                Severity = Severity.Error,
                                Source = "GetAlbums",
                                Line = ex.Message,
                                TimeStamp = DateTime.Now,
                                Exception = ex
                            }));
                            MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
                            Console.WriteLine($"Unable to map metadata: {ex.Message}");
                        }
                    });

                    if (album.Name == null || album.Artist == "Unknown") continue;

                    if (!string.IsNullOrWhiteSpace(imageFile))
                    {
                        var imageBytes = File.ReadAllBytes(imageFile);
                        album.Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
                    }

                    album.Id = savedAlbum?.Id ?? ObjectId.GenerateNewId();
                    album.NumberOfTracks = tracks.Count;
                    album.Duration = tracks.Sum(track => track.Duration);
                    album.Tracks = tracks.OrderBy(track => track.TrackNumber).ToList();
                    album.Path = directory;
                    albums.Add(album);

                    var numberOfDiscs = tracks.Max(track => track.DiscNumber);
                    if (album.NumberOfDiscs == 0 && numberOfDiscs > 0)
                        album.NumberOfDiscs = numberOfDiscs;

                    MappingUpdate.MappedDirectories = albums.Count;
                }

                MappingUpdate.IsComplete = true;
                return albums;
            }
            catch (UnauthorizedAccessException ex)
            {
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetAlbums",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                MappingUpdate.Error = $"Access denied: {ex.Message}";
                Console.WriteLine($"Access denied: {ex.Message}");
                throw;
            }
            catch (DirectoryNotFoundException ex)
            {
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetAlbums",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                MappingUpdate.Error = $"Directory not found: {ex.Message}";
                Console.WriteLine($"Directory not found: {ex.Message}");
                throw;
            }
        }

        private Album MapSinglesAlbum(string directory, Album savedAlbum, List<PlaylistTrack> cachedFavouriteTracks, DirectoryInfo directoryInfo)
        {
            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).ToList();

            if (!files.Any()) return null;

            Album album = new Album
            {
                Name = "Singles",
                Artist = "Various Artists",
                Genre = "Various",
                Year = DateTime.Now.Year,
                Encoding = "MPEG",
                Played = false,
                DateMapped = savedAlbum?.DateMapped ?? directoryInfo.CreationTime,
                NumberOfDiscs = 1,
                Path = directory
            };
            ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();

            var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
            var imageFile = GetHighestResolutionImage(imageFiles);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file).ToLower())) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);
                    var savedtrack = savedAlbum?.Tracks.Find(track => string.Equals(track.Path, file));
                    var presetGuid = GetTrackEqualizerPresetGuidAsync(file, savedtrack?.EqualizerGuid ?? Guid.Empty).GetAwaiter().GetResult();
                    var isFavourite = cachedFavouriteTracks?.Any(playlistTrack => string.Equals(playlistTrack.Path, file)) ?? false;
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, savedtrack, presetGuid, isFavourite));
                }
                catch (Exception ex)
                {
                    Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "MapSinglesAlbum",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    }));
                    MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
                    Console.WriteLine($"Unable to map metadata: {ex.Message}");
                }
            });

            if (!string.IsNullOrWhiteSpace(imageFile))
            {
                var imageBytes = File.ReadAllBytes(imageFile);
                album.Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
            }

            album.Id = savedAlbum?.Id ?? ObjectId.GenerateNewId();
            album.NumberOfTracks = tracks.Count;
            album.Duration = tracks.Sum(track => track.Duration);
            album.Tracks = tracks.OrderBy(track => track.TrackNumber).ToList();

            return album;
        }

        private async Task<Album> GetCreatedAlbumAsync(string path)
        {
            var directoryInfo = new DirectoryInfo(path);
            var directoryName = directoryInfo.Name;
            if (_directoryExclusions.Contains(directoryName)) return null;

            var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();

            if (!files.Any()) return null;

            var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
            var imageFile = GetHighestResolutionImage(imageFiles);

            var firstAudioFile = files.FirstOrDefault(file => _audioFileExtensions.Contains(Path.GetExtension(file).ToLower()));
            if (firstAudioFile == null) return null;

            // Built once, deterministically, before the parallel pass — see GetAlbums() for why (data race).
            var album = MapAlbumMetaData(new MediaInfoWrapper(firstAudioFile), firstAudioFile, null, directoryInfo.CreationTime);
            ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();

            // Fetched once here instead of once per file — the old code queried the Favourite playlist from Mongo
            // on every single track.
            var cachedFavouriteTracks = (await MongoDbClient.Instance.GetPlaylistAsync("Favourite"))?.Tracks;

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file).ToLower())) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);
                    var presetGuid = GetTrackEqualizerPresetGuidAsync(file, Guid.Empty).GetAwaiter().GetResult();
                    var isFavourite = cachedFavouriteTracks?.Any(playlistTrack => string.Equals(playlistTrack.Path, file)) ?? false;
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, null, presetGuid, isFavourite));
                }
                catch (Exception ex)
                {
                    Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "GetCreatedAlbum",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    }));
                    Console.WriteLine($"Unable to map metadata: {ex.Message}");
                }
            });

            if (album.Name == null || album.Artist == "Unknown") return null;

            if (!string.IsNullOrWhiteSpace(imageFile))
            {
                var imageBytes = File.ReadAllBytes(imageFile);
                album.Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
            }

            album.Id = ObjectId.GenerateNewId();
            album.NumberOfTracks = tracks.Count;
            album.Duration = tracks.Sum(track => track.Duration);
            album.Tracks = tracks.OrderBy(track => track.TrackNumber).ToList();
            album.Path = path;

            var numberOfDiscs = tracks.Max(track => track.DiscNumber);
            if (album.NumberOfDiscs == 0 && numberOfDiscs > 0)
                album.NumberOfDiscs = numberOfDiscs;

            return album;
        }

        private Album RefreshMappedAlbum(string path, Album databaseAlbum = null, List<PlaylistTrack> cachedFavouriteTracks = null)
        {
            // Checked before doing any file enumeration — the old code always walked the whole directory tree and
            // scanned for images first, even for the "Singles" and excluded-directory cases where that work is
            // immediately thrown away.
            if (string.Equals(path, "Singles"))
                return MapSinglesAlbum(path, databaseAlbum, cachedFavouriteTracks, new DirectoryInfo(path));

            var directoryInfo = new DirectoryInfo(path);
            var directoryName = directoryInfo.Name;
            if (_directoryExclusions.Contains(directoryName)) return null;

            var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();
            if (!files.Any()) return null;

            var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
            var imageFile = GetHighestResolutionImage(imageFiles);

            var firstAudioFile = files.FirstOrDefault(file => _audioFileExtensions.Contains(Path.GetExtension(file).ToLower()));
            if (firstAudioFile == null) return null;

            // Built once, deterministically, before the parallel pass — see GetAlbums() for why (data race).
            var album = MapAlbumMetaData(new MediaInfoWrapper(firstAudioFile), firstAudioFile, databaseAlbum, directoryInfo.CreationTime);
            ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file).ToLower())) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);
                    var savedtrack = databaseAlbum?.Tracks.Find(track => string.Equals(track.Path, file));
                    var presetGuid = GetTrackEqualizerPresetGuidAsync(file, savedtrack?.EqualizerGuid ?? Guid.Empty).GetAwaiter().GetResult();
                    var isFavourite = cachedFavouriteTracks?.Any(playlistTrack => string.Equals(playlistTrack.Path, file)) ?? false;
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, savedtrack, presetGuid, isFavourite));
                }
                catch (Exception ex)
                {
                    Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "GetAlbums",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    }));
                    MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
                    Console.WriteLine($"Unable to map metadata: {ex.Message}");
                }
            });

            if (album.Name == null || album.Artist == "Unknown") return null;

            if (!string.IsNullOrWhiteSpace(imageFile))
            {
                var imageBytes = File.ReadAllBytes(imageFile);
                album.Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
            }

            album.Guid = databaseAlbum?.Guid ?? Guid.NewGuid();
            album.Id = databaseAlbum?.Id ?? ObjectId.GenerateNewId();
            album.NumberOfTracks = tracks.Count;
            album.Duration = tracks.Sum(track => track.Duration);
            album.Tracks = tracks.OrderBy(track => track.TrackNumber).ToList();
            album.Path = path;

            var numberOfDiscs = tracks.Max(track => track.DiscNumber);
            if (album.NumberOfDiscs == 0 && numberOfDiscs > 0)
                album.NumberOfDiscs = numberOfDiscs;

            return album;
        }

        private Album MapAlbumMetaData(MediaInfoWrapper metadata, string path, Album album, DateTime dateCreated)
        {
            return new Album
            {
                Name = metadata.Tags.Album,
                Artist = GetAlbumArtist(metadata.Tags.AlbumArtist, metadata.Tags.Artist),
                Genre = metadata.Tags.Genre,
                Year = GetAlbumReleaseYear(metadata.Tags.RecordedDate, metadata.Tags.ReleasedDate),
                Encoding = (metadata.AudioCodec ?? metadata.Codec)?.Split(' ')?.FirstOrDefault()?.ToUpper(),
                Played = album?.Played ?? false,
                DateMapped = album?.DateMapped ?? dateCreated,
                NumberOfDiscs = metadata.Tags.TotalDiscs ?? GetTotalDiscs(path),
            };
        }

        private int GetTotalDiscs(string path)
        {
            var extData = MediaFile.Read(path);
            if (!extData.IsSuccess) return 0;
            return (int)(extData.Tag.TotalDiscs ?? 0);
        }

        private string GetAlbumArtist(string albumArtist, string artist, string albumArtistFallback = null)
        {
            if (!string.IsNullOrWhiteSpace(albumArtist)) return albumArtist;

            if (!string.IsNullOrWhiteSpace(artist)) return artist;

            return albumArtistFallback ?? "Unknown";
        }

        private string GetTrackArtist(string albumArtist, string artist, string albumArtistFallback = null)
        {
            if (!string.IsNullOrWhiteSpace(artist)) return artist;

            if (!string.IsNullOrWhiteSpace(albumArtist)) return albumArtist;

            return albumArtistFallback ?? "Unknown";
        }

        private int GetAlbumReleaseYear(DateTime? recordedDate, DateTime? releasedDate)
        {
            if (recordedDate != default) return recordedDate.Value.Year;

            if (releasedDate != default) return releasedDate.Value.Year;

            return 0;
        }

        private Track MapAlbumTrackMetaData(MediaInfoWrapper metadata, string AlbumArtistFallback, string path, Track track, Guid presetGuid, bool isFavourite)
        {
            return new Track
            {
                DiscNumber = metadata.Tags.DiscNumber ?? GetDiscNumber(path),
                TrackNumber = metadata.Tags.TrackPosition ?? GetTrackNumber(path),
                Name = metadata.Tags.Track,
                Artist = GetTrackArtist(metadata.Tags.AlbumArtist, metadata.Tags.Artist, AlbumArtistFallback),
                Duration = GetTrackDuration(metadata.Duration, path),
                TimesPlayed = track?.TimesPlayed ?? 0,
                IsFavourite = isFavourite || (track?.IsFavourite ?? false),
                EqualizerGuid = presetGuid,
                Path = path,
            };
        }

        private int GetDiscNumber(string path)
        {
            var extData = MediaFile.Read(path);
            if (!extData.IsSuccess) return 1;
            return (int)(extData.Tag.DiscNumber ?? 1);
        }

        private int GetTrackNumber(string path)
        {
            var extData = MediaFile.Read(path);
            if (!extData.IsSuccess) return 0;
            return (int)(extData.Tag.Track ?? 0);
        }

        private double GetTrackDuration(int metadataDuration, string path)
        {
            if ((double)metadataDuration == 0)
                using (var reader = new AudioFileReader(path))
                    return reader.TotalTime.TotalSeconds;
            else
                return TimeSpan.FromMilliseconds(metadataDuration).TotalSeconds;
        }

        private string GetHighestResolutionImage(List<string> imageFiles)
        {
            if (!imageFiles.Any()) return null;

            if (imageFiles.Count() == 1) return imageFiles.First();

            var coverFile = imageFiles.Find(HasCommonAlbumArtFileName);
            if (!string.IsNullOrWhiteSpace(coverFile)) return coverFile;

            return imageFiles.OrderByDescending(file => new FileInfo(file).Length).First();
        }

        private bool HasCommonAlbumArtFileName(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            return fileName.StartsWith("cover", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("folder", StringComparison.OrdinalIgnoreCase);
        }

        private async Task UpsertMappedAlbumsAsync(ConcurrentBag<Album> mappedAlbums)
        {
            try
            {
                // A single batched BulkWriteAsync instead of one InsertOneAsync/ReplaceOneAsync call per album.
                // The old insert-vs-update decision (checking mappedAlbums against a separately-passed list of
                // already-saved albums) is now handled entirely inside UpsertAlbumsAsync via an Id-based upsert,
                // since every album here already has its Id set correctly by the caller.
                await MongoDbClient.Instance.UpsertAlbumsAsync(mappedAlbums);
            }
            catch (Exception)
            {
                Console.WriteLine("Unable to upsert albums to database");
                throw;
            }
        }

        private async Task UpdateTrackUserDataAsync(string path)
        {
            var track = await MongoDbClient.Instance.GetTrackAsync(albumPath: Path.GetDirectoryName(path), trackPath: path);
            await ServerHttpClient.Instance.UpdateTrackerUserDataAsync(new TrackUserData
            {
                Path = path,
                IsFavourite = track.IsFavourite,
                TimesPlayed = track.TimesPlayed,
            });
        }

        private async Task<List<Album>> GetCachedDatabaseAlbumsAsync()
        {
            try
            {
                return await MongoDbClient.Instance.GetAlbumsAsync();
            }
            catch (Exception ex)
            {
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetCachedDatabaseAlbumsAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                MappingUpdate.Error = ex.Message;
                return null;
            }
        }

        private async Task<Album> GetCachedDatabaseAlbumAsync(string path)
        {
            try
            {
                return await MongoDbClient.Instance.GetAlbumAsync(path);
            }
            catch (Exception ex)
            {
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetCachedDatabaseAlbumsAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                MappingUpdate.Error = ex.Message;
                return null;
            }
        }

        private async Task<Guid> GetTrackEqualizerPresetGuidAsync(string path, Guid equalizerGuid)
        {
            if (equalizerGuid != Guid.Empty) return equalizerGuid;

            try
            {
                return (await MongoDbClient.Instance.GetEqualizerPresetAsync(path))?.Guid ?? Guid.Empty;
            }
            catch (Exception ex)
            {
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "GetTrackEqualizerPresetAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                MappingUpdate.Error = ex.Message;
                return Guid.Empty;
            }
        }

        private List<string> GetUnmappedDirectories()
        {
            var directories = Directory.EnumerateDirectories(Constants.LIBRARY_DIRECTORY, "*.*", SearchOption.TopDirectoryOnly);
            var filePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
            var mappedAlbumPaths = new ConcurrentBag<string>();
            Parallel.ForEach(filePaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                var path = ReadCachedAlbumPath(file);
                if (path != null)
                    mappedAlbumPaths.Add(path);
            });
            return directories.Except(mappedAlbumPaths).ToList();
        }

        // Streams the cached album file and stops as soon as it finds "Path", instead of deserializing the whole
        // Album (which includes the full Tracks list and a base64-encoded cover image). Only checking whether a
        // directory is already mapped shouldn't cost parsing every album's artwork.
        private string ReadCachedAlbumPath(string filePath)
        {
            using (var streamReader = new StreamReader(filePath))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType != JsonToken.PropertyName || !string.Equals((string)jsonReader.Value, nameof(Album.Path)))
                        continue;

                    jsonReader.Read();
                    return (string)jsonReader.Value;
                }

                return null;
            }
        }

        #endregion Private Functions
    }
}
