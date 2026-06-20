using MusicPlayer.Common;
using MusicPlayer.PlaylistManagement.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.PlaylistManagement
{
    public sealed class PlaylistManager
    {
        public readonly List<string> CORE_PLAYLISTS = new List<string> { "Favourite", " Recently Played" };

        #region Singleton
        private static readonly Lazy<PlaylistManager> _instance = new Lazy<PlaylistManager>(() => new PlaylistManager());
        public static PlaylistManager Instance { get => _instance.Value; }

        private PlaylistManager()
        {
            Task.Run(async () => await InitializePlaylistsAsync());
        }

        #endregion Singleton

        public async Task<List<Playlist>> GetPlaylistsAsync()
        {
            List<Playlist> playlists = new List<Playlist>
            {
                new Playlist
                {
                    Name = "Now Playing",
                    Tracks = Player.Player.Instance.GetNowPlayingPlaylist()
                },
            };

            var savedPlaylists = await MongoDbClient.Instance.GetPlaylistsAsync();
            playlists.AddRange(savedPlaylists.Where(playlist => playlist.CanEdit));

            return playlists;
        }

        private async Task InitializePlaylistsAsync()
        {
            var playlists = await MongoDbClient.Instance.GetPlaylistsAsync();
            Parallel.ForEach(CORE_PLAYLISTS, async (name) =>
            {
                if (playlists.Any(playlist => string.Equals(playlist.Name, name))) return;

                await MongoDbClient.Instance.InsertPlaylistAsync(new Playlist { Name = name, Tracks = new List<PlaylistTrack>() });
            });
        }
    }
}
