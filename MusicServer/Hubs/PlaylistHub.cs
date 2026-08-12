using Microsoft.AspNetCore.SignalR;
using MusicPlayer.PlaylistManagement;
using MusicPlayer.PlaylistManagement.Models;
using MusicServer.Helpers;
using MusicServer.Startup;
using System.Text.Json;

namespace MusicServer.Hubs
{
    public class PlaylistHub : Hub
    {
        public async Task GetPlaylistsAsync()
        {
            List<Playlist> playlists = [];

            try
            {
                throw new Exception();
                playlists = await PlaylistManager.Instance.GetPlaylistsAsync();
                var playlistsResponse = await RedisCache.GetAsync("Playlists");
                playlists.AddRange(JsonSerializer.Deserialize<List<Playlist>>(playlistsResponse));
            }
            catch (Exception ex)
            {
                playlists = await PlaylistManager.Instance.GetPlaylistsAsync();
                //playlists.AddRange(await PlaylistManager.Instance.GetPlaylistsAsync());
                //await Clients.All.SendAsync("ReceivePlaylistHubError", ex.Message);
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
