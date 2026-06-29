using MediaInfo;
using MongoDB.Bson;
using MusicPlayer.Common;
using MusicPlayer.FileManagment;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Models;
using MusicPlayer.PlaylistManagement.Models;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _ = Task.Run(async () => await UpsertMappedAlbumsAsync(databaseAlbums, mappedAlbums));
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

        public void HandleCreatedAlbum(string path)
        {
            Task.Run(async () =>
            {
                try
                {
                    var album = GetCreatedAlbum(path);
                    await MongoDbClient.Instance.InsertAlbumAsync(album);
                    FileManager.Instance.Write(album);
                }
                catch (Exception ex)
                {
                    await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "HandleCreatedAlbum",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    });
                    Console.WriteLine(ex.Message);
                }
            });
        }

        public void HandleDeletedAlbum(string path)
        {
            Task.Run(async () =>
            {
                try
                {
                    var filePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
                    var cachedAlbum = await MongoDbClient.Instance.GetAlbumAsync(path);

                    if (cachedAlbum != null)
                        await MongoDbClient.Instance.DeleteAlbumAsync(path);

                    var appDataPath = filePaths.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), cachedAlbum?.Guid.ToString()));
                    if (appDataPath != null)
                        FileManager.Instance.RemoveFile(appDataPath);
                }
                catch (Exception ex)
                {
                    await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                    {
                        Severity = Severity.Error,
                        Source = "HandleDeletedAlbum",
                        Line = ex.Message,
                        TimeStamp = DateTime.Now,
                        Exception = ex
                    });
                    Console.WriteLine(ex.Message);
                }
            });
        }

        public async Task ClearLocalCacheAsync()
        {
            await Task.CompletedTask;
            FileManager.Instance.ClearDirectory(null, Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
        }

        #endregion Public Functions

        #region Private Functions

        private ConcurrentBag<Album> GetAlbums(List<Album> databaseAlbums, List<PlaylistTrack> cachedFavouriteTracks)
        {
            try
            {
                MappingUpdate.Message = $"Mapping target directory: {Constants.LIBRARY_DIRECTORY}";

                var directories = Directory.EnumerateDirectories(Constants.LIBRARY_DIRECTORY, "*.*", SearchOption.TopDirectoryOnly);
                ConcurrentBag<Album> albums = new ConcurrentBag<Album>();
                MappingUpdate.DirectoryCount = directories.Count();

                Parallel.ForEach(directories, directory =>
                {
                    MappingUpdate.Message = $"Mapping directory: {directory}";

                    var directoryInfo = new DirectoryInfo(directory);
                    var directoryName = directoryInfo.Name;
                    if (_directoryExclusions.Contains(directoryName)) return;

                    if (string.Equals(directoryName, "Singles"))
                    {
                        var savedSinglesAlbum = databaseAlbums?.Find(dbAlbum => string.Equals(dbAlbum.Path, directory));
                        var singlesAlbum = MapSinglesAlbum(directory, savedSinglesAlbum, cachedFavouriteTracks, directoryInfo);
                        albums.Add(singlesAlbum);
                        return;
                    }

                    var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);
                    if (!files.Any()) return;

                    Album album = null;
                    ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();
                    var savedAlbum = databaseAlbums?.Find(dbAlbum => string.Equals(dbAlbum.Path, directory));

                    var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
                    var imageFile = GetHighestResolutionImage(imageFiles);


                    Parallel.ForEach(files, file =>
                    {
                        if (!_audioFileExtensions.Contains(Path.GetExtension(file))) return;

                        try
                        {
                            var mediaInfo = new MediaInfoWrapper(file);

                            if (album == null)
                                album = MapAlbumMetaData(mediaInfo, file, savedAlbum, directoryInfo.CreationTime);

                            var savedtrack = savedAlbum?.Tracks.Find(track => string.Equals(track.Path, file));
                            var presetGuid = Task.Run(async () => await GetTrackEqualizerPresetGuidAsync(file, savedtrack?.EqualizerGuid ?? Guid.Empty)).GetAwaiter().GetResult();
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


                    if (album == null) return;
                    if (album.Name == null || album.Artist == "Unknown") return;

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
                });

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
            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);

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

            Parallel.ForEach(files, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file))) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);
                    var savedtrack = savedAlbum?.Tracks.Find(track => string.Equals(track.Path, file));
                    var presetGuid = Task.Run(async () => await GetTrackEqualizerPresetGuidAsync(file, savedtrack?.EqualizerGuid ?? Guid.Empty)).GetAwaiter().GetResult();
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

        private Album GetCreatedAlbum(string path)
        {
            var directoryInfo = new DirectoryInfo(path);
            var directoryName = directoryInfo.Name;
            if (_directoryExclusions.Contains(directoryName)) return null;

            var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);

            if (!files.Any()) return null;

            Album album = null;
            ConcurrentBag<Track> tracks = new ConcurrentBag<Track>();

            var imageFiles = files.Where(file => _imageFileExtensions.Contains(Path.GetExtension(file))).ToList();
            var imageFile = GetHighestResolutionImage(imageFiles);

            Parallel.ForEach(files, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file))) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);

                    if (album == null)
                        album = MapAlbumMetaData(mediaInfo, file, null, directoryInfo.CreationTime);

                    var presetGuid = Task.Run(async () => await GetTrackEqualizerPresetGuidAsync(file, Guid.Empty)).GetAwaiter().GetResult();
                    var isFavourite = Task.Run(async () => ((await MongoDbClient.Instance.GetPlaylistAsync("Favourite"))?.Tracks?.Any(playlistTrack => string.Equals(playlistTrack.Path, file))) ?? false).GetAwaiter().GetResult();
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

            if (album == null) return null;
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

        private async Task UpsertMappedAlbumsAsync(List<Album> savedAlbums, ConcurrentBag<Album> mappedAlbums)
        {
            await Task.CompletedTask;

            try
            {
                Parallel.ForEach(mappedAlbums, async (album) =>
                {
                    if (savedAlbums.Any(dbAlbum => string.Equals(dbAlbum.Path, album.Path)))
                        await MongoDbClient.Instance.UpdateAlbumAsync(album);
                    else
                        await MongoDbClient.Instance.InsertAlbumAsync(album);
                });
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
        #endregion Private Functions
    }
}
