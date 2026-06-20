using MongoDB.Bson;
using MongoDB.Driver;
using MusicPlayer.LibraryManagement.Models;
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

        #endregion Equalizer

        #region Album
        public async Task InsertAlbumAsync(Album album) => await _albumCollection.InsertOneAsync(album);

        public async Task UpdateAlbumAsync(Album album)
        {
            var filter = Builders<Album>.Filter.Eq(filteredAlbum => filteredAlbum.Id, album.Id);
            await _albumCollection.ReplaceOneAsync(filter, album);
        }

        public async Task UpdateTimesPlayedAsync(string albumPath, string trackPath)
        {
            var album = await GetAlbumAsync(albumPath);
            album.Tracks.Find(filteredTrack => string.Equals(filteredTrack.Path, trackPath)).TimesPlayed++;

            await UpdateAlbumAsync(album);
        }

        public async Task<(bool isFavourite, string guid)> UpdateIsFavouriteAsync(string albumPath, string trackPath)
        {
            var album = await GetAlbumAsync(albumPath);
            var track = album.Tracks.Find(filteredTrack => string.Equals(filteredTrack.Path, trackPath));
            track.IsFavourite = !track.IsFavourite;

            await UpdateAlbumAsync(album);
            return (isFavourite: track.IsFavourite, guid: album.Guid.ToString());
        }

        public async Task<List<Album>> GetAlbumsAsync() => await _albumCollection.Find(Builders<Album>.Filter.Empty).ToListAsync();

        public async Task<Album> GetAlbumAsync(ObjectId id) => await _albumCollection.Find(album => album.Id.Equals(id)).FirstOrDefaultAsync();

        public async Task<Album> GetAlbumAsync(string path) => await _albumCollection.Find(album => string.Equals(album.Path, path)).FirstOrDefaultAsync();

        public async Task<Track> GetTrackAsync(string albumPath, string trackPath)
            => (await _albumCollection.Find(album => string.Equals(album.Path, albumPath)).FirstOrDefaultAsync()).Tracks.Find(track => string.Equals(track.Path, trackPath));

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
    }
}
