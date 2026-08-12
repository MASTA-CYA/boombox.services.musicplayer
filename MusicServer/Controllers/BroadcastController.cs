using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Player;
using MusicPlayer.PlaylistManagement;
using MusicPlayer.PlaylistManagement.Models;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Reflection;
using System.Text.Json;

namespace MusicServer.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BroadcastController(ILogger<BroadcastController> logger, IHubContext<PlayerHub> playerHub, IHubContext<PlaylistHub> playlistHub, IHubContext<ServerHub> serverHub) : ControllerBase
    {
        private readonly ILogger<BroadcastController> _logger = logger;
        private readonly IHubContext<PlayerHub> _playerHub = playerHub;
        private readonly IHubContext<PlaylistHub> _playlistHub = playlistHub;
        private readonly IHubContext<ServerHub> _serverHub = serverHub;

        private static CancellationTokenSource? _playbackToken;
        private static CancellationTokenSource? _playlistToken;
        private static CancellationTokenSource? _serverToken;

        #region Playback Information
        [HttpPost("StartPlaybackInformation")]
        public void StartPlaybackInformation()
        {
            if (_playbackToken != null) return;

            _playbackToken = new CancellationTokenSource();
            var token = _playbackToken.Token;

            Task.Run(async () => await StartPlaybackInformationBroadcastAsync(token), token);
        }

        [HttpPost("StopPlaybackInformation")]
        public void StopPlaybackInformationAsync()
        {
            if (_playbackToken == null) return;

            _playbackToken.Cancel();
            _playbackToken.Dispose();
            _playbackToken = null;
        }

        private async Task StartPlaybackInformationBroadcastAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var playbackInfo = Player.Instance.PlaybackInformation;

                    if (playbackInfo == null) return;

                    var playbackInfoJson = JsonSerializer.Serialize(playbackInfo, JsonSerializationHelper.SerializerOptions);
                    await _playerHub.Clients.All.SendAsync("ReceivePlaybackInformation", playbackInfoJson, token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playback information broadcast loop failed");
                Player.Instance.RestartPlaybackBroadcast();
            }
        }

        #endregion Playback Information

        #region Playlist
        [HttpPost("StartPlaylistUpdates")]
        public void StartPlaylistUpdates()
        {
            if (_playlistToken != null) return;

            _playlistToken = new CancellationTokenSource();
            var token = _playlistToken.Token;

            Task.Run(async () => await StartPlaylistBroadcastAsync(token), token);
        }

        [HttpPost("StopPlaylistUpdates")]
        public void StopPlaylistUpdates()
        {
            if (_playlistToken == null) return;

            _playlistToken.Cancel();
            _playlistToken.Dispose();
            _playlistToken = null;
        }

        private async Task StartPlaylistBroadcastAsync(CancellationToken token)
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
                        //playlists.AddRange(await PlaylistManager.Instance.GetPlaylistsAsync());
                        //await _playlistHub.Clients.All.SendAsync("ReceivePlaylistHubError", ex.Message);
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
            }
        }

        #endregion Playlist

        // Library Mapping updates no longer go through this controller — MappingUpdate now broadcasts itself via
        // its Changed event, wired up once at startup by MusicServer.Startup.MappingUpdateBroadcast. See
        // KNOWN_ISSUES.md for why: the old 1ms polling timer here re-sent the same state constantly regardless of
        // whether anything had changed.

        #region Server Updates
        [HttpPost("StartServerUpdates")]
        public void StartServerUpdates()
        {
            if (_serverToken != null) return;

            _serverToken = new CancellationTokenSource();
            var token = _serverToken.Token;

            Task.Run(async () => await StartSeverStatusBroadcastAsync(token), token);
        }

        [HttpPost("StopServerUpdates")]
        public void StopServerUpdates()
        {
            if (_serverToken == null) return;

            _serverToken.Cancel();
            _serverToken.Dispose();
            _serverToken = null;
        }

        private async Task StartSeverStatusBroadcastAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion[..13];
                while (await timer.WaitForNextTickAsync(token))
                    await _serverHub.Clients.All.SendAsync("ReceiveServerUpdates", version, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Server status broadcast loop failed");
            }
        }

        #endregion Server Updates
    }
}
