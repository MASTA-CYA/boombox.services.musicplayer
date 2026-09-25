using Microsoft.AspNetCore.SignalR;
using MusicPlayer.PlaylistManagement;
using MusicPlayer.PlaylistManagement.Models;
using MusicServer.Helpers;
using MusicServer.Startup;
using System.Text.Json;

namespace MusicServer.Hubs
{
    public class PlaylistHub(ILogger<PlaylistHub> logger) : Hub
    {
        private readonly ILogger<PlaylistHub> _logger = logger;

        public async Task GetPlaylistsAsync()
        {
            List<Playlist> playlists = [];

            // Used to also attempt a "Playlists" Redis cache merge here (behind a hardcoded `throw new
            // Exception();` that unconditionally skipped it - dead/incomplete code, nothing ever wrote that key).
            // Removed along with the same dead attempt in PlaylistBroadcast.RunAsync - see the comment there.
            try
            {
                playlists = await PlaylistManager.Instance.GetPlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load playlists");
            }

            var playlistJson = JsonSerializer.Serialize(playlists, JsonSerializationHelper.SerializerOptions);
            await Clients.All.SendAsync("ReceivePlaylists", playlistJson);
        }

        public async Task StartPlayingUpdatesAsync()
        {
            await Task.CompletedTask;
            PlaylistBroadcast.Start();
        }

        public async Task StopPlayingUpdatesAsync()
        {
            await Task.CompletedTask;
            PlaylistBroadcast.Stop();
        }
    }
}
