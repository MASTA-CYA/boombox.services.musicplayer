using MongoDB.Bson;
using MongoDB.Driver;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.LyricsManagement.Models;
using MusicPlayer.Player.Models;
using MusicPlayer.PlaylistManagement.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.Common
{
    public class MongoDbClient
    {
        private const string URI = "mongodb://10.0.0.254:27017/?directConnection=true";
        private readonly IMongoClient _client;
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<Album> _albumCollection;
        private readonly IMongoCollection<Playlist> _playlistCollection;
        private readonly IMongoCollection<EqualizerPreset> _equalizerCollection;
        private readonly IMongoCollection<MappingStatistic> _mappingStatisticCollection;
        private readonly IMongoCollection<PlayerSettings> _playerSettingsCollection;
        private readonly IMongoCollection<Lyrics> _lyricsCollection;
        private readonly IMongoCollection<TrackUserData> _trackUserDataCollection;

        #region Singleton
        private static readonly Lazy<MongoDbClient> _instance = new Lazy<MongoDbClient>(() => new MongoDbClient());
        public static MongoDbClient Instance { get => _instance.Value; }

        private MongoDbClient()
        {
            _client = new MongoClient(URI);
            _database = _client.GetDatabase("music_server");
            _albumCollection = _database.GetCollection<Album>("albums");
            _playlistCollection = _database.GetCollection<Playlist>("playlists");
            _equalizerCollection = _database.GetCollection<EqualizerPreset>("equalizer");
            _mappingStatisticCollection = _database.GetCollection<MappingStatistic>("mapping_statistics");
            _playerSettingsCollection = _database.GetCollection<PlayerSettings>("player_settings");
            _lyricsCollection = _database.GetCollection<Lyrics>("lyrics");
            _trackUserDataCollection = _database.GetCollection<TrackUserData>("trackUserData");

            // GetCollection<T>() above never touches the server — it's just a client-side handle. MongoDB only
            // creates a collection on its first write, so without this, a fresh database would be missing whichever
            // collections nothing had been inserted into yet (mapping_statistics, most likely, since it's only
            // written at the end of a full rescan). Called directly rather than via Task.Run(...).GetAwaiter()
            // .GetResult() — see LibraryManager's blocking-on-async fix notes for why that wrapper is wasteful.
            EnsureCollectionsExistAsync().GetAwaiter().GetResult();
        }

        // If the constructor throws, this Lazy<T> permanently caches the exception and every future
        // MongoDbClient.Instance access rethrows it for the rest of the process's lifetime (an app restart is
        // needed to retry) — same as if Mongo were simply unreachable during any other startup call, just surfaced
        // here instead.
        private async Task EnsureCollectionsExistAsync()
        {
            var existingCollectionNames = new HashSet<string>(await (await _database.ListCollectionNamesAsync()).ToListAsync());
            var expectedCollectionNames = new[] { "albums", "playlists", "equalizer", "mapping_statistics", "player_settings", "lyrics", "trackUserData" };

            foreach (var collectionName in expectedCollectionNames)
            {
                if (existingCollectionNames.Contains(collectionName)) continue;

                try
                {
                    // trackUserData gets a case-insensitive (secondary-strength: differs by accent, not case)
                    // default collation at creation time — collation can only be set when a collection is
                    // created, not altered afterward. Every equality lookup against Path in this collection
                    // then "just works" regardless of casing drift between when a track was favourited and when
                    // it's later re-enumerated from disk, which is exactly the gap that let favourites silently
                    // stop matching on Windows (case-insensitive but case-preserving filesystems) before.
                    var options = string.Equals(collectionName, "trackUserData")
                        ? new CreateCollectionOptions { Collation = new Collation("en", strength: CollationStrength.Secondary) }
                        : null;
                    await _database.CreateCollectionAsync(collectionName, options);
                }
                catch (MongoCommandException ex) when (string.Equals(ex.CodeName, "NamespaceExists"))
                {
                    // Created by something else between the check above and this call — nothing to do.
                }
            }
        }

        #endregion Singleton

        #region Equalizer
        public async Task<List<EqualizerPreset>> GetEqualizerPresetsAsync() => await _equalizerCollection.Find(preset => preset.IsDefault).ToListAsync();

        public async Task<EqualizerPreset> GetEqualizerPresetAsync(string name) => await _equalizerCollection.Find(preset => string.Equals(preset.Name, name)).FirstOrDefaultAsync();

        public async Task InsertEqualizerPresetAsync(EqualizerPreset preset) => await _equalizerCollection.InsertOneAsync(preset);

        public async Task UpdateEqualizerPresetAsync(EqualizerPreset preset)
        {
            var filter = Builders<EqualizerPreset>.Filter.Eq(filteredPreset => filteredPreset.Id, preset.Id);
            await _equalizerCollection.ReplaceOneAsync(filter, preset);
        }

        public async Task DeleteEqualizerPresetAsync(Guid guid) => await _equalizerCollection.DeleteOneAsync(preset => preset.Guid == guid);

        #endregion Equalizer

        #region Album
        public async Task InsertAlbumAsync(Album album) => await _albumCollection.InsertOneAsync(album);

        public async Task UpdateAlbumAsync(Album album)
        {
            var filter = Builders<Album>.Filter.Eq(filteredAlbum => filteredAlbum.Id, album.Id);
            await _albumCollection.ReplaceOneAsync(filter, album);
        }

        // Replaces InsertOneAsync/ReplaceOneAsync-per-album with a single batched write. Every album passed in
        // already has its Id set correctly by the caller (either the existing document's Id, or a freshly
        // generated one for a new album — see LibraryManager.MapAlbumMetaData callers), so a plain Id-filtered
        // upsert is enough; there's no need to separately decide insert vs. update here.
        public async Task UpsertAlbumsAsync(IEnumerable<Album> albums)
        {
            var writeModels = albums
                .Where(album => album != null)
                .Select(album =>
                {
                    var filter = Builders<Album>.Filter.Eq(filteredAlbum => filteredAlbum.Id, album.Id);
                    return (WriteModel<Album>)new ReplaceOneModel<Album>(filter, album) { IsUpsert = true };
                })
                .ToList();

            if (writeModels.Count == 0) return;

            await _albumCollection.BulkWriteAsync(writeModels);
        }

        public async Task<List<Album>> GetAlbumsAsync() => await _albumCollection.Find(Builders<Album>.Filter.Empty).ToListAsync();

        public async Task<Album> GetAlbumAsync(ObjectId id) => await _albumCollection.Find(album => album.Id.Equals(id)).FirstOrDefaultAsync();

        public async Task<Album> GetAlbumAsync(string path) => await _albumCollection.Find(album => string.Equals(album.Path, path)).FirstOrDefaultAsync();

        public async Task DeleteAlbumAsync(ObjectId id) => await _albumCollection.DeleteOneAsync(album => album.Id.Equals(id));

        public async Task DeleteAlbumAsync(string path) => await _albumCollection.DeleteOneAsync(album => string.Equals(album.Path, path));


        #endregion Album

        #region Playlist
        public async Task<List<Playlist>> GetPlaylistsAsync() => await _playlistCollection.Find(Builders<Playlist>.Filter.Empty).ToListAsync();

        public async Task<Playlist> GetPlaylistAsync(string name) => await _playlistCollection.Find(playlist => string.Equals(playlist.Name, name)).FirstOrDefaultAsync();

        public async Task InsertPlaylistAsync(Playlist playlist) => await _playlistCollection.InsertOneAsync(playlist);

        public async Task UpdatePlaylistAsync(Playlist playlist)
        {
            var filter = Builders<Playlist>.Filter.Eq(filteredPlaylist => filteredPlaylist.Id, playlist.Id);
            await _playlistCollection.ReplaceOneAsync(filter, playlist);
        }

        public async Task AddPlaylistTracksAsync(string name, List<PlaylistTrack> tracks)
        {
            var playlist = await GetPlaylistAsync(name) ?? new Playlist { Name = name, Tracks = new List<PlaylistTrack>() };
            playlist.Tracks.AddRange(tracks);
            await UpdatePlaylistAsync(playlist);
        }

        public async Task RemovePlaylistTracksAsync(string name, List<PlaylistTrack> tracks)
        {
            var playlist = await GetPlaylistAsync(name);

            if (playlist == null) return;

            playlist.Tracks.RemoveAll(track => tracks.Any(trackToRemove => string.Equals(trackToRemove.Path, track.Path)));
            await UpdatePlaylistAsync(playlist);
        }

        #endregion Playlist

        #region Mapping Statistics
        public async Task InsertMappingStatisticAsync(MappingStatistic statistic) => await _mappingStatisticCollection.InsertOneAsync(statistic);

        public async Task<List<MappingStatistic>> GetMappingStatisticsAsync(int limit = 50)
            => await _mappingStatisticCollection.Find(Builders<MappingStatistic>.Filter.Empty)
                .SortByDescending(statistic => statistic.StartedAtUtc)
                .Limit(limit)
                .ToListAsync();

        public async Task<MappingStatistic> GetMappingStatisticAsync(ObjectId id) => await _mappingStatisticCollection.Find(statistic => statistic.Id.Equals(id)).FirstOrDefaultAsync();

        #endregion Mapping Statistics

        #region Player Settings
        public async Task<PlayerSettings> GetPlayerSettingsAsync() => await _playerSettingsCollection.Find(Builders<PlayerSettings>.Filter.Empty).FirstOrDefaultAsync();

        // There's only ever meant to be one document in this collection, so an empty filter with IsUpsert is
        // enough - it replaces the single existing row, or inserts one if this is the very first save.
        public async Task SavePlayerSettingsAsync(PlayerSettings settings)
            => await _playerSettingsCollection.ReplaceOneAsync(Builders<PlayerSettings>.Filter.Empty, settings, new ReplaceOptions { IsUpsert = true });

        #endregion Player Settings

        #region Lyrics
        public async Task<Lyrics> GetLyricsAsync(string trackPath) => await _lyricsCollection.Find(lyrics => string.Equals(lyrics.TrackPath, trackPath)).FirstOrDefaultAsync();

        // Upsert keyed by TrackPath (not Id, unlike Album/Playlist/EqualizerPreset's Id-filtered replace) -
        // LyricsManager never loads an existing document before deciding to write one (it either found a fresh
        // result to cache or is caching a NotFound), so it has no prior Id to filter on the way those other
        // UpdateXAsync methods do.
        public async Task SaveLyricsAsync(Lyrics lyrics)
        {
            var filter = Builders<Lyrics>.Filter.Eq(filteredLyrics => filteredLyrics.TrackPath, lyrics.TrackPath);
            await _lyricsCollection.ReplaceOneAsync(filter, lyrics, new ReplaceOptions { IsUpsert = true });
        }

        #endregion Lyrics

        #region Track User Data
        // Single source of truth for favourite/times-played state, one document per track keyed by Path -
        // replaces the old design where this lived embedded inside Album.Tracks[] (a full-document
        // read-modify-write on every toggle) AND separately inside the "Favourite" playlist (which was also
        // used as the actual read source when computing display state during a remap - two places that could,
        // and did, drift out of sync). See EnsureCollectionsExistAsync for the case-insensitive collation this
        // collection is created with.
        public async Task<TrackUserData> GetTrackUserDataAsync(string path) => await _trackUserDataCollection.Find(data => string.Equals(data.Path, path)).FirstOrDefaultAsync();

        // Bulk fetch for a full library remap - one query for the whole (small) collection instead of one
        // lookup per track. The caller indexes this into a case-insensitive in-memory dictionary too
        // (belt-and-suspenders alongside the collection's own collation, since a plain C# Dictionary doesn't
        // get Mongo's collation for free).
        public async Task<List<TrackUserData>> GetAllTrackUserDataAsync() => await _trackUserDataCollection.Find(Builders<TrackUserData>.Filter.Empty).ToListAsync();

        public async Task<bool> SetFavouriteAsync(string path, bool isFavourite)
        {
            var filter = Builders<TrackUserData>.Filter.Eq(data => data.Path, path);
            var update = Builders<TrackUserData>.Update
                .Set(data => data.IsFavourite, isFavourite)
                .Set(data => data.UpdatedAtUtc, DateTime.UtcNow)
                .SetOnInsert(data => data.Id, ObjectId.GenerateNewId())
                .SetOnInsert(data => data.Path, path);

            await _trackUserDataCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
            return isFavourite;
        }

        // No native atomic "toggle" among Mongo's update operators, so this reads the current value first - a
        // negligible race for a single-user personal app (two simultaneous toggles of the same track from two
        // clients at once isn't a realistic scenario), and still a large improvement over the old design's
        // full Album-document read-modify-write plus its two other out-of-band writes.
        public async Task<bool> ToggleFavouriteAsync(string path)
        {
            var existing = await GetTrackUserDataAsync(path);
            var newValue = !(existing?.IsFavourite ?? false);
            await SetFavouriteAsync(path, newValue);
            return newValue;
        }

        // One-time migration support (see LibraryManager.MigrateTrackUserDataIfNeededAsync) - a plain bulk
        // insert rather than an upsert, since migration only ever runs against a collection it has already
        // confirmed is empty.
        public async Task InsertTrackUserDataBatchAsync(IEnumerable<TrackUserData> userData)
        {
            var records = userData.ToList();
            if (records.Count == 0) return;

            await _trackUserDataCollection.InsertManyAsync(records);
        }

        public async Task<int> IncrementTimesPlayedAsync(string path)
        {
            var filter = Builders<TrackUserData>.Filter.Eq(data => data.Path, path);
            var update = Builders<TrackUserData>.Update
                .Inc(data => data.TimesPlayed, 1)
                .Set(data => data.UpdatedAtUtc, DateTime.UtcNow)
                .SetOnInsert(data => data.Id, ObjectId.GenerateNewId())
                .SetOnInsert(data => data.Path, path);

            var options = new FindOneAndUpdateOptions<TrackUserData> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
            var updated = await _trackUserDataCollection.FindOneAndUpdateAsync(filter, update, options);
            return updated.TimesPlayed;
        }

        #endregion Track User Data
    }
}
