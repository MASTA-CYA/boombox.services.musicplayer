using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Player;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Text.Json;

namespace MusicServer.Startup
{
    // Replaces BroadcastController's Playback Information region. That controller only existed so MusicPlayer
    // (Player.RestartPlaybackBroadcast/StopPlaybackInformationUpdatesAsync) had something to self-HTTP-POST back
    // into — despite PlayerHub, which actually starts this loop, already living in MusicServer and being able to
    // call a static class directly. The one genuine cross-project trigger is stopping the loop once a playlist
    // finishes, which happens inside MusicPlayer's Player class; that's bridged via Player.PlaybackBroadcastStopRequested,
    // the same plain-event pattern used for MappingUpdate.Changed and AppLogger.
    public static class PlaybackBroadcast
    {
        private static readonly object _lock = new object();

        private static IHubContext<PlayerHub> _playerHub;
        private static ILogger _logger;
        private static CancellationTokenSource _cts;

        public static void Initialize(WebApplication app)
        {
            _playerHub = app.Services.GetRequiredService<IHubContext<PlayerHub>>();
            _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MusicServer.Startup.PlaybackBroadcast");

            Player.Instance.PlaybackBroadcastStopRequested += (sender, args) => Stop();
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

                lock (_lock) { _cts = null; }

                // Same guard BroadcastController's old RestartPlaybackBroadcast() used: only self-heal if playback
                // is genuinely still active. Cancelling the token to stop the loop at playlist-end also lands here
                // (WaitForNextTickAsync throws on cancellation), and in that case IsPlaying is already false, so
                // this correctly does nothing instead of restarting a loop that was deliberately stopped.
                if (Player.Instance.PlaybackInformation?.PlayerState?.IsPlaying == true)
                    Start();
            }
        }
    }
}
