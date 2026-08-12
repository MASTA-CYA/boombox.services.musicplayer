using Microsoft.AspNetCore.SignalR;
using MusicPlayer.PlaylistManagement;
using MusicPlayer.PlaylistManagement.Models;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Text.Json;

namespace MusicServer.Startup
{
    // Replaces BroadcastController's Playlist region. Unlike playback broadcasting, nothing here ever needed a
    // MusicPlayer-side trigger — PlaylistHub, which starts/stops this loop, already lives in MusicServer, so the
    // old self-HTTP-POST through ServerHttpClient/BroadcastController was pure indirection. PlaylistHub now calls
    // Start()/Stop() directly.
    public static class PlaylistBroadcast
    {
        private static readonly object _lock = new object();

        private static IHubContext<PlaylistHub> _playlistHub;
        private static ILogger _logger;
        private static CancellationTokenSource _cts;

        public static void Initialize(WebApplication app)
        {
            _playlistHub = app.Services.GetRequiredService<IHubContext<PlaylistHub>>();
            _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MusicServer.Startup.PlaylistBroadcast");
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
            }

            _ = RunAsync(_cts.Token);
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (_cts == null) return;
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        private static async Task RunAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2000));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    List<Playlist> playlists = [];

                    try
                    {
                        playlists = await PlaylistManager.Instance.GetPlaylistsAsync();
                        var playlistsResponse = await RedisCache.GetAsync("Playlists");
                        playlists.AddRange(JsonSerializer.Deserialize<List<Playlist>>(playlistsResponse) ?? []);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to merge Redis playlist cache, falling back to Mongo-only playlists");
                    }
                    finally
                    {
                        var playlistJson = JsonSerializer.Serialize(playlists, JsonSerializationHelper.SerializerOptions);
                        await _playlistHub.Clients.All.SendAsync("ReceivePlaylists", playlistJson, token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playlist broadcast loop failed");
                lock (_lock) { _cts = null; }
            }
        }
    }
}
