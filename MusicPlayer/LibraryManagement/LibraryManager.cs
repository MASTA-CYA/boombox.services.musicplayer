using MediaInfo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Player.Models;
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
        private static readonly Microsoft.Extensions.Logging.ILogger _logger = AppLogger.CreateLogger<LibraryManager>();

        private readonly List<string> _audioFileExtensions = new List<string> { ".mp3", ".wav", ".flac", ".m4a" };
        private readonly List<string> _imageFileExtensions = new List<string> { ".png", ".jpg", ".webp", ".jpeg" };
        private readonly List<string> _directoryExclusions = new List<string> { "beats", "Edits", "iTunes", "Playlists", "Dump", "Resampled Providers", "Staging" };

        public MappingUpdate MappingUpdate { get; set; }
        public string SelectedAlbum { get; set; }

        public event EventHandler<FavouriteTrackEventArgs> UpdatedFavouriteTrack;

        // MusicPlayer can't reference MusicServer's SignalR hubs directly (circular project reference), so this is
        // raised instead of the old self-HTTP-POST through ServerHttpClient/MetaDataController — same bridge
        // pattern as MappingUpdate.Changed and Player.PlaybackBroadcastStopRequested. Subscribed once at startup by
        // MusicServer/Startup/TrackUserDataBroadcast.cs.
        public event EventHandler<TrackUserData> TrackUserDataChanged;


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
            // One bulk fetch for the whole (small) trackUserData collection instead of a per-track Mongo
            // lookup, indexed case-insensitively so a track's path casing drifting between when it was
            // favourited and now (Windows filesystems are case-insensitive but case-preserving) can't cause a
            // lookup miss - see BuildUserDataLookup and the track-user-data design notes.
            var userDataLookup = BuildUserDataLookup(await MongoDbClient.Instance.GetAllTrackUserDataAsync());
            // Same bulk-fetch-once treatment for equalizer presets - see BuildEqualizerPresetLookup.
            var presetLookup = BuildEqualizerPresetLookup(await MongoDbClient.Instance.GetAllEqualizerPresetsAsync());
            var mappedAlbums = await Task.Run(() => GetAlbums(databaseAlbums, userDataLookup, presetLookup));
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

            // Was Task.Run(async () => await Foo()).GetAwaiter().GetResult() - same redundant-thread-hop
            // antipattern already fixed on the mapping path (KNOWN_ISSUES.md #5): scheduling the async lambda
            // onto the thread pool via Task.Run, then immediately blocking that thread-pool thread on its own
            // result, burns two thread-pool threads per call instead of one. Calling the async method directly
            // and blocking on it here has the same synchronous behavior (this method's own signature isn't
            // async, and its Select(...)-based callers need it to stay that way) without the extra hop.
            var userData = MongoDbClient.Instance.GetTrackUserDataAsync(filePath).GetAwaiter().GetResult();
            var isFavourite = userData?.IsFavourite ?? false;
            var preset = MongoDbClient.Instance.GetEqualizerPresetAsync(filePath).GetAwaiter().GetResult();

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

        // TimesPlayed now lives solely in the trackUserData collection (one atomic $inc, no read-modify-write) -
        // it used to also be patched into the Album's Mongo document and its per-album AppData JSON file mirror
        // by hand, on every single play. Neither is needed anymore: GetAlbums/MapAlbumTrackMetaData read
        // TimesPlayed straight from trackUserData at load time (see the remap rewiring), so a stale value left
        // behind in the JSON mirror is simply never consulted.
        public async Task UpdateTrackPlayedAsync(string path)
        {
            await MongoDbClient.Instance.IncrementTimesPlayedAsync(path);
            await UpdateTrackUserDataAsync(path);
        }

        // Was a three-way write (the Album's embedded Track.IsFavourite field via a full-document
        // read-modify-write, the "Favourite" playlist, and the per-album AppData JSON file mirror, patched by
        // hand in three separate steps with no transaction tying them together - see the track-user-data
        // design discussion for how that caused favourites to silently appear "reset"). Now a single atomic
        // upsert against trackUserData is the only thing that has to succeed for the toggle itself to be
        // durable; the playlist sync below is real but explicitly best-effort and secondary.
        public async Task UpdateIsFavoriteTrackAsync(string path)
        {
            try
            {
                var isFavourite = await MongoDbClient.Instance.ToggleFavouriteAsync(path);
                UpdatedFavouriteTrack?.Invoke(this, new FavouriteTrackEventArgs(path, isFavourite));
                await UpdateTrackUserDataAsync(path);

                // The "Favourite" playlist is a real playable playlist ("play my favourites"), so it's still
                // kept in sync here - but it's no longer what display state is computed from (see
                // GetAlbums/MapAlbumTrackMetaData), so a failure in this block can no longer corrupt what the
                // UI shows the way it used to; it just means the playlist itself lags until the next toggle.
                try
                {
                    var track = GetTrackInformation(path);

                    if (isFavourite)
                        await MongoDbClient.Instance.AddPlaylistTracksAsync("Favourite", new List<PlaylistTrack> { track });
                    else
                        await MongoDbClient.Instance.RemovePlaylistTracksAsync("Favourite", new List<PlaylistTrack> { track });
                }
                catch (Exception playlistEx)
                {
                    _logger.LogWarning(playlistEx, "Unable to sync Favourite playlist for {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to update favourite state for {Path}", path);
            }
        }

        // One-time backfill for the trackUserData consolidation - before this, favourite/times-played state
        // lived embedded in each Album document's Tracks[] AND separately in the "Favourite" playlist, with no
        // guarantee the two agreed. Runs once (guarded by the collection actually being empty, so it's a cheap
        // no-op on every startup after the first) and unions both old sources rather than picking one, so
        // nothing a user had already favourited or played gets silently dropped by the migration itself.
        // Called from MusicServer's startup sequence, awaited, before anything else can read trackUserData.
        public async Task MigrateTrackUserDataIfNeededAsync()
        {
            try
            {
                var alreadyMigrated = (await MongoDbClient.Instance.GetAllTrackUserDataAsync()).Any();
                if (alreadyMigrated) return;

                // Prefer the disaster-recovery backup file (MongoDbClient.BackupTrackUserDataAsync) over
                // reconstructing from Mongo's albums/playlists collections below - a full Mongo clear wipes those
                // too, so they can't help recover from exactly the scenario this backup exists for. The backup is
                // also strictly fresher/more complete: it's written on every real favourite/times-played change,
                // while albums/playlists only ever reflect whatever was true at the last mapping run for that
                // album. Only falls through to the original reconstruction below if no backup file exists yet -
                // e.g. a genuinely first-ever run, before this collection or its backup ever existed.
                var backupJson = FileManager.Instance.ReadTrackUserDataBackup();
                if (!string.IsNullOrWhiteSpace(backupJson))
                {
                    var backedUp = JsonConvert.DeserializeObject<List<TrackUserData>>(backupJson, JsonSerializationHelper.FileSerializerSettings);
                    if (backedUp != null && backedUp.Any())
                    {
                        await MongoDbClient.Instance.InsertTrackUserDataBatchAsync(backedUp);
                        _logger.LogWarning("Restored {Count} track user data records from local backup file", backedUp.Count);
                        return;
                    }
                }

                var albums = await MongoDbClient.Instance.GetAlbumsAsync();
                var favouritePlaylist = await MongoDbClient.Instance.GetPlaylistAsync("Favourite");
                var favouritePaths = new HashSet<string>(
                    favouritePlaylist?.Tracks.Select(track => track.Path) ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var merged = new Dictionary<string, TrackUserData>(StringComparer.OrdinalIgnoreCase);

                foreach (var album in albums)
                {
                    foreach (var track in album.Tracks ?? new List<Track>())
                    {
                        if (string.IsNullOrWhiteSpace(track.Path)) continue;

                        if (!merged.TryGetValue(track.Path, out var data))
                        {
                            data = new TrackUserData { Id = ObjectId.GenerateNewId(), Path = track.Path, UpdatedAtUtc = DateTime.UtcNow };
                            merged[track.Path] = data;
                        }

                        // Max, not overwrite - a track's path can theoretically appear in more than one saved
                        // Album snapshot (e.g. a stale duplicate left over from a moved directory); keeping the
                        // higher play count is the safer direction to err in for a migration.
                        data.TimesPlayed = Math.Max(data.TimesPlayed, track.TimesPlayed);
                        data.IsFavourite = data.IsFavourite || track.IsFavourite || favouritePaths.Contains(track.Path);
                    }
                }

                // Covers any Favourite-playlist track that isn't attached to a currently-mapped album at all -
                // rare (e.g. the album was since moved or deleted), but a real case this loop alone would miss.
                foreach (var path in favouritePaths)
                {
                    if (merged.ContainsKey(path)) continue;
                    merged[path] = new TrackUserData { Id = ObjectId.GenerateNewId(), Path = path, IsFavourite = true, UpdatedAtUtc = DateTime.UtcNow };
                }

                if (!merged.Any()) return;

                await MongoDbClient.Instance.InsertTrackUserDataBatchAsync(merged.Values);

                // Warning, not Information, deliberately - see SignalRErrorSink: this is the one and only time
                // this migration ever runs, and surfacing it as a toast is a useful, reassuring confirmation it
                // actually happened, the same reasoning AudioOutputAvailabilityBroadcast's forced-switch
                // notification already uses this mechanism for.
                _logger.LogWarning("Migrated {Count} track user data records into the new trackUserData collection", merged.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to migrate track user data");
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
                _logger.LogError(ex, "Unable to map newly created album at {Path}", path);
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
                            _logger.LogError(ex, "Unable to handle deleted album at {Path}", directory);
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
            var userDataLookup = BuildUserDataLookup(await MongoDbClient.Instance.GetAllTrackUserDataAsync());
            var presetLookup = BuildEqualizerPresetLookup(await MongoDbClient.Instance.GetAllEqualizerPresetsAsync());

            var mappedAlbum = RefreshMappedAlbum(path, databaseAlbum, userDataLookup, presetLookup);

            // RefreshMappedAlbum returns null for a handful of legitimate reasons (excluded directory, no audio
            // files left, unreadable tags leaving Name/Artist unresolved) - previously this fell straight
            // through and serialized/wrote a null album: SelectedAlbum became the literal string "null", and
            // both background tasks below threw on mappedAlbum.Guid/.Id before doing anything (harmless on
            // their own, since BulkWriteAsync failing doesn't touch the existing document) but the swallowed
            // exceptions made this fail silently instead of leaving the previously cached album in place, which
            // is the correct behavior when a refresh can't produce anything better.
            if (mappedAlbum == null)
            {
                _logger.LogError("Refresh produced no album for {Path} — leaving the previously cached album in place", path);
                return;
            }

            SelectedAlbum = JsonConvert.SerializeObject(mappedAlbum, JsonSerializationHelper.NamingSerializerSettings);
            _ = Task.Run(async () => await UpsertMappedAlbumsAsync(new ConcurrentBag<Album> { mappedAlbum }));
            _ = Task.Run(() => FileManager.Instance.Write(mappedAlbum));
            // Self-heal for the "duplicate card" bug (see KNOWN_ISSUES.md): a refresh that ran before the Guid
            // carry-forward fix above would have left an orphaned cache file behind under a stale Guid. Cheap
            // enough to run on every refresh regardless — normally finds nothing, since mappedAlbum.Guid now
            // matches the existing file.
            _ = Task.Run(() => RemoveOrphanedAlbumCacheFiles(path, mappedAlbum.Guid));
        }

        // Deletes any other cached album JSON file whose stored Path matches this album but whose filename
        // (the album's Guid) doesn't match the one we just wrote — a leftover from before a bug fix, or any
        // future path where a refresh generates a new Guid instead of reusing the existing one.
        private void RemoveOrphanedAlbumCacheFiles(string albumPath, Guid keepGuid)
        {
            try
            {
                var cachedFilePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
                foreach (var file in cachedFilePaths)
                {
                    var fileGuid = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(fileGuid, keepGuid.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                    var cachedPath = ReadCachedAlbumPath(file);
                    if (string.Equals(cachedPath, albumPath))
                        FileManager.Instance.RemoveFile(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to clean up orphaned album cache files for {Path}", albumPath);
            }
        }

        #region Equalizer Assignments

        // Feeds Settings' "Equalizer" tab track-assignment list. Deliberately doesn't go through the full
        // GetAlbumsAsync()/GetAlbums() remap pipeline (expensive - MediaInfo/file probing per track, see the
        // known library-mapping performance issues) - a plain Mongo album query is enough to find which tracks
        // have a custom preset (Track.EqualizerGuid), and Name/Path is enough to identify each album (Mongo
        // doesn't store Album.Name/Track.Name - see their [BsonIgnore] attributes - so those are filled in from
        // the same per-album JSON file cache SaveEqualizerPresetAsync/UpdateTrackPlayedAsync already read, but
        // only for the handful of albums that actually have an assignment, not the whole library).
        public async Task<List<TrackEqualizerAssignment>> GetTrackEqualizerAssignmentsAsync()
        {
            var albums = await MongoDbClient.Instance.GetAlbumsAsync();
            var assignedAlbums = albums.Where(album => album.Tracks.Any(track => track.EqualizerGuid != Guid.Empty)).ToList();

            if (!assignedAlbums.Any()) return new List<TrackEqualizerAssignment>();

            var cachedFilePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
            var assignments = new List<TrackEqualizerAssignment>();

            foreach (var album in assignedAlbums)
            {
                var cachedAlbum = ReadCachedAlbum(cachedFilePaths, album.Guid);

                foreach (var track in album.Tracks.Where(track => track.EqualizerGuid != Guid.Empty))
                {
                    var cachedTrack = cachedAlbum?.Tracks.Find(cachedAlbumTrack => string.Equals(cachedAlbumTrack.Path, track.Path));

                    assignments.Add(new TrackEqualizerAssignment
                    {
                        AlbumName = cachedAlbum?.Name ?? Path.GetFileName(album.Path),
                        AlbumPath = album.Path,
                        TrackName = cachedTrack?.Name ?? Path.GetFileNameWithoutExtension(track.Path),
                        TrackPath = track.Path,
                        PresetGuid = track.EqualizerGuid,
                    });
                }
            }

            return assignments;
        }

        // Mirrors Player.SaveEqualizerPresetAsync's per-track-preset logic, but parameterized by an explicit
        // trackPath instead of pulling it from _playlistProvider.CurrentProvider - that method only ever makes
        // sense for "whatever's currently playing," which isn't the track being edited when this is called from
        // the Settings track-assignments list (that track may not be playing, or anything may be playing).
        public async Task UpdateTrackEqualizerPresetAsync(string trackPath, EqualizerPreset preset)
        {
            var existingPreset = await MongoDbClient.Instance.GetEqualizerPresetAsync(trackPath);

            if (existingPreset != null)
            {
                existingPreset.FrequencyBands = preset.FrequencyBands;
                await MongoDbClient.Instance.UpdateEqualizerPresetAsync(existingPreset);
                await SyncTrackEqualizerGuidAsync(Path.GetDirectoryName(trackPath), trackPath, existingPreset.Guid);
                return;
            }

            var newPreset = new EqualizerPreset
            {
                Id = ObjectId.Empty,
                Guid = Guid.NewGuid(),
                Name = trackPath,
                IsDefault = false,
                FrequencyBands = preset.FrequencyBands,
            };
            await MongoDbClient.Instance.InsertEqualizerPresetAsync(newPreset);
            await SyncTrackEqualizerGuidAsync(Path.GetDirectoryName(trackPath), trackPath, newPreset.Guid);
        }

        // Permanently deletes a track's custom preset document and resets the track back to whatever the
        // player's default/merged preset resolves to. Renamed from ClearTrackEqualizerPresetAsync - the
        // behavior was always a real delete (nothing kept the preset around for reuse), the old name just
        // undersold that, which read as "just unassigns" rather than "gone for good."
        public async Task DeleteTrackEqualizerPresetAsync(string trackPath)
        {
            var preset = await MongoDbClient.Instance.GetEqualizerPresetAsync(trackPath);

            if (preset != null)
                await MongoDbClient.Instance.DeleteEqualizerPresetAsync(preset.Guid);

            await SyncTrackEqualizerGuidAsync(Path.GetDirectoryName(trackPath), trackPath, Guid.Empty);
        }

        // Called when a NAMED preset is deleted from Settings (Player.DeleteEqualizerPresetAsync) - any track
        // that had it assigned would otherwise keep pointing at a Guid that no longer resolves to anything.
        public async Task ClearEqualizerPresetReferencesAsync(Guid guid)
        {
            var assignments = await GetTrackEqualizerAssignmentsAsync();

            foreach (var assignment in assignments.Where(assignment => assignment.PresetGuid == guid))
                await SyncTrackEqualizerGuidAsync(assignment.AlbumPath, assignment.TrackPath, Guid.Empty);
        }

        #endregion Equalizer Assignments

        #endregion Public Functions

        #region Private Functions

        private ConcurrentBag<Album> GetAlbums(List<Album> databaseAlbums, Dictionary<string, TrackUserData> userDataLookup, Dictionary<string, Guid> presetLookup)
        {
            // Reset from any previous run so a stale IsComplete/Error doesn't linger into this one — MusicServer's
            // MappingUpdateBroadcast treats IsComplete transitioning to true as "this run just finished, persist
            // it," so a leftover true here would make every update in a fresh run look like a completed one.
            MappingUpdate.IsComplete = false;
            MappingUpdate.Error = null;
            MappingUpdate.RunType = MappingRunType.FullScan;
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
                    _logger.LogWarning(ex, "Unable to sample resource usage");
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
                            var singlesAlbum = MapSinglesAlbum(directory, savedSinglesAlbum, userDataLookup, presetLookup, directoryInfo);
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
                                // Was GetTrackEqualizerPresetGuidAsync(...).GetAwaiter().GetResult() - one Mongo
                                // round trip per file across the whole library (KNOWN_ISSUES.md #5's N+1 note).
                                // Now a synchronous lookup against presetLookup, fetched once for the whole scan.
                                var presetGuid = ResolveEqualizerPresetGuid(file, savedtrack?.EqualizerGuid ?? Guid.Empty, presetLookup);
                                userDataLookup.TryGetValue(file, out var userData);
                                tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, presetGuid, userData));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Unable to map metadata for {FilePath}", file);
                                MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
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
                    _logger.LogError(ex, "Access denied while mapping library");
                    MappingUpdate.Error = $"Access denied: {ex.Message}";
                    throw;
                }
                catch (DirectoryNotFoundException ex)
                {
                    _logger.LogError(ex, "Directory not found while mapping library");
                    MappingUpdate.Error = $"Directory not found: {ex.Message}";
                    throw;
                }
        }

        private Album MapSinglesAlbum(string directory, Album savedAlbum, Dictionary<string, TrackUserData> userDataLookup, Dictionary<string, Guid> presetLookup, DirectoryInfo directoryInfo)
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
                    var presetGuid = ResolveEqualizerPresetGuid(file, savedtrack?.EqualizerGuid ?? Guid.Empty, presetLookup);
                    userDataLookup.TryGetValue(file, out var userData);
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, presetGuid, userData));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to map metadata for {FilePath}", file);
                    MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
                }
            });

            if (!string.IsNullOrWhiteSpace(imageFile))
            {
                var imageBytes = File.ReadAllBytes(imageFile);
                album.Image = $"data:image/{Path.GetExtension(imageFile)};base64,{Convert.ToBase64String(imageBytes)}";
            }

            album.Id = savedAlbum?.Id ?? ObjectId.GenerateNewId();
            // Carries the existing album's Guid forward, same as RefreshMappedAlbum's generic branch does for
            // every other album — Album.Guid defaults to a fresh random value on every `new Album { ... }`, and
            // the per-album AppData cache file is named `{Guid}.json`, so without this a Singles refresh would
            // silently write a brand-new cache file instead of overwriting the existing one, leaving the old one
            // behind as an orphan (this was the cause of the "duplicate Singles card" bug — see KNOWN_ISSUES.md).
            album.Guid = savedAlbum?.Guid ?? Guid.NewGuid();
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
            var userDataLookup = BuildUserDataLookup(await MongoDbClient.Instance.GetAllTrackUserDataAsync());
            // Same batching for equalizer presets - see BuildEqualizerPresetLookup.
            var presetLookup = BuildEqualizerPresetLookup(await MongoDbClient.Instance.GetAllEqualizerPresetsAsync());

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                if (!_audioFileExtensions.Contains(Path.GetExtension(file).ToLower())) return;

                try
                {
                    var mediaInfo = new MediaInfoWrapper(file);
                    var presetGuid = ResolveEqualizerPresetGuid(file, Guid.Empty, presetLookup);
                    userDataLookup.TryGetValue(file, out var userData);
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, presetGuid, userData));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to map metadata for {FilePath}", file);
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

        private Album RefreshMappedAlbum(string path, Album databaseAlbum, Dictionary<string, TrackUserData> userDataLookup, Dictionary<string, Guid> presetLookup)
        {
            // Checked before doing any file enumeration — the old code always walked the whole directory tree and
            // scanned for images first, even for the "Singles" and excluded-directory cases where that work is
            // immediately thrown away.
            //
            // Compares the directory's NAME against "Singles", not the full path — this used to check
            // `string.Equals(path, "Singles")`, but `path` here is the real absolute folder path (e.g.
            // "D:\Music\Singles") passed in from the frontend's Refresh Album button, never literally the
            // four-character string "Singles". That made the branch dead code: every Singles refresh fell
            // through to the generic single-album path below, which derives the album's Name/Artist from
            // whichever track happens to enumerate first in the folder — silently overwriting "Singles" /
            // "Various Artists" with some random track's own tags on every refresh (e.g. a loose single by
            // Sombr became the whole folder's displayed "album"). Matches the check GetAlbums() already uses
            // correctly for the same folder during a full library mapping pass.
            var directoryInfo = new DirectoryInfo(path);

            if (string.Equals(directoryInfo.Name, "Singles"))
                return MapSinglesAlbum(path, databaseAlbum, userDataLookup, presetLookup, directoryInfo);

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
                    var presetGuid = ResolveEqualizerPresetGuid(file, savedtrack?.EqualizerGuid ?? Guid.Empty, presetLookup);
                    userDataLookup.TryGetValue(file, out var userData);
                    tracks.Add(MapAlbumTrackMetaData(mediaInfo, album.Artist, file, presetGuid, userData));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to map metadata for {FilePath}", file);
                    MappingUpdate.Error = $"Unable to map metadata: {ex.Message}";
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

        // TimesPlayed/IsFavourite now come solely from trackUserData (userData, looked up by the caller via
        // BuildUserDataLookup) - this used to also fall back to the old Track snapshot embedded in the
        // previous Album document (an `isFavourite || track?.IsFavourite` OR across two sources that could,
        // and did, disagree). trackUserData is the only place this state is written now, so there's nothing
        // left to OR against.
        private Track MapAlbumTrackMetaData(MediaInfoWrapper metadata, string AlbumArtistFallback, string path, Guid presetGuid, TrackUserData userData)
        {
            return new Track
            {
                DiscNumber = metadata.Tags.DiscNumber ?? GetDiscNumber(path),
                TrackNumber = metadata.Tags.TrackPosition ?? GetTrackNumber(path),
                Name = metadata.Tags.Track,
                Artist = GetTrackArtist(metadata.Tags.AlbumArtist, metadata.Tags.Artist, AlbumArtistFallback),
                Duration = GetTrackDuration(metadata.Duration, path),
                TimesPlayed = userData?.TimesPlayed ?? 0,
                IsFavourite = userData?.IsFavourite ?? false,
                EqualizerGuid = presetGuid,
                Path = path,
            };
        }

        // Bulk trackUserData -> case-insensitive lookup dictionary, shared by every remap entry point above.
        // GroupBy+First (rather than a plain ToDictionary, which throws on a duplicate key) means this can
        // never crash a library load even if two documents somehow end up differing only by path casing -
        // it silently keeps whichever one it saw first instead. Windows filesystems are case-insensitive but
        // case-preserving, which is exactly the mismatch that let favourites appear to silently reset before
        // (see the track-user-data design notes) - this dictionary, plus the collection's own case-insensitive
        // collation (EnsureCollectionsExistAsync), closes that gap at both layers.
        private static Dictionary<string, TrackUserData> BuildUserDataLookup(IEnumerable<TrackUserData> userData)
            => userData
                .GroupBy(data => data.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Same batching fix as BuildUserDataLookup, for the same reason - KNOWN_ISSUES.md #5's "N+1 Mongo query
        // in GetTrackEqualizerPresetGuidAsync (one round trip per file) - not batched". Named presets and
        // per-track custom overrides (Name == track path) share one collection, keyed by Name the same way
        // GetEqualizerPresetAsync(name) already matches (plain ordinal, not case-insensitive - unlike
        // trackUserData, this hasn't had a reported path-casing issue, so kept as-is rather than changing
        // behavior beyond the batching itself). GroupBy+First for the same reason as above: never crash a
        // library load over a stray duplicate Name.
        private static Dictionary<string, Guid> BuildEqualizerPresetLookup(IEnumerable<EqualizerPreset> presets)
            => presets
                .GroupBy(preset => preset.Name)
                .ToDictionary(group => group.Key, group => group.First().Guid);

        // Replaces the old GetTrackEqualizerPresetGuidAsync's one-Mongo-call-per-file lookup with a synchronous
        // read against a lookup built once per mapping pass (see BuildEqualizerPresetLookup). Same short-circuit
        // as before: a track that already has an assigned EqualizerGuid keeps it, no lookup needed.
        private static Guid ResolveEqualizerPresetGuid(string path, Guid existingGuid, Dictionary<string, Guid> presetLookup)
        {
            if (existingGuid != Guid.Empty) return existingGuid;
            return presetLookup.TryGetValue(path, out var guid) ? guid : Guid.Empty;
        }

        // Overlays LIVE trackUserData onto already-mapped albums' tracks - needed by any read path that serves a
        // previously-cached Album without running a full mapping pass. GetAlbumsAsync/RefreshAlbumAsync already
        // pull trackUserData fresh via BuildUserDataLookup because they run an actual mapping pass, but two real
        // read paths don't: LibraryHub's fast "Cache" library load (deserializes every per-album JSON file
        // straight off disk) and GetSelectedAlbumAsync (deserializes a single album's JSON file the same way).
        // Both of those JSON files only ever get their IsFavourite/TimesPlayed fields refreshed when that
        // specific album is next mapped/refreshed - toggling a favourite anywhere else (the player, a different
        // album's grid) only updates trackUserData plus whatever PlaylistTrack happens to be live in memory (see
        // UpdateIsFavoriteTrackAsync), never these on-disk snapshots. That's exactly what let the player show a
        // track as favourited while the album grid - served straight from its stale JSON cache - still showed it
        // as not. Calling this right before either of those cache reads returns to the client makes trackUserData
        // the actual single source of truth end-to-end, not just for the mapping passes.
        public async Task ApplyLiveTrackUserDataAsync(IEnumerable<Album> albums)
        {
            var userDataLookup = BuildUserDataLookup(await MongoDbClient.Instance.GetAllTrackUserDataAsync());

            foreach (var album in albums)
            {
                if (album?.Tracks == null) continue;

                foreach (var track in album.Tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.Path)) continue;
                    if (!userDataLookup.TryGetValue(track.Path, out var userData)) continue;

                    track.IsFavourite = userData.IsFavourite;
                    track.TimesPlayed = userData.TimesPlayed;
                }
            }
        }

        public async Task ApplyLiveTrackUserDataAsync(Album album) => await ApplyLiveTrackUserDataAsync(new[] { album });

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to upsert albums to database");
                throw;
            }
        }

        // trackUserData is now the only place this state lives, so the freshly-written document can be
        // broadcast as-is instead of reconstructing one from an Album's embedded Track (which no longer holds
        // the current value at all - see the write-path rewiring above).
        private async Task UpdateTrackUserDataAsync(string path)
        {
            var trackUserData = await MongoDbClient.Instance.GetTrackUserDataAsync(path);
            if (trackUserData == null) return;

            TrackUserDataChanged?.Invoke(this, trackUserData);
        }

        private async Task<List<Album>> GetCachedDatabaseAlbumsAsync()
        {
            try
            {
                return await MongoDbClient.Instance.GetAlbumsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to get cached database albums");
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
                _logger.LogError(ex, "Unable to get cached database album at {Path}", path);
                MappingUpdate.Error = ex.Message;
                return null;
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

        // Full deserialize (unlike ReadCachedAlbumPath above) - only called for the handful of albums that
        // actually have a track equalizer assignment, so the cost of reading Tracks/Image in full is fine here.
        private Album ReadCachedAlbum(IEnumerable<string> cachedFilePaths, Guid albumGuid)
        {
            var filePath = cachedFilePaths.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), albumGuid.ToString()));
            if (filePath == null) return null;

            return JsonConvert.DeserializeObject<Album>(FileManager.Instance.Read(filePath), settings: JsonSerializationHelper.FileSerializerSettings);
        }

        // Keeps a track's EqualizerGuid in sync across both persistence layers - the Mongo Album document (source
        // of truth for playback/mapping) and its JSON file cache mirror (what GetTrackInformation/ReadCachedAlbum
        // read back for display) - same two-write pattern already used by UpdateTrackPlayedAsync/
        // UpdateIsFavoriteTrackAsync above, just for EqualizerGuid instead of TimesPlayed/IsFavourite.
        private async Task SyncTrackEqualizerGuidAsync(string albumPath, string trackPath, Guid guid)
        {
            var album = await MongoDbClient.Instance.GetAlbumAsync(albumPath);
            var track = album?.Tracks.Find(albumTrack => string.Equals(albumTrack.Path, trackPath));
            if (track == null) return;

            track.EqualizerGuid = guid;
            await MongoDbClient.Instance.UpdateAlbumAsync(album);

            var cachedFilePaths = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN);
            var cachedAlbum = ReadCachedAlbum(cachedFilePaths, album.Guid);
            var cachedTrack = cachedAlbum?.Tracks.Find(albumTrack => string.Equals(albumTrack.Path, trackPath));
            if (cachedTrack == null) return;

            cachedTrack.EqualizerGuid = guid;
            FileManager.Instance.Write(cachedAlbum);
        }

        #endregion Private Functions
    }
}
