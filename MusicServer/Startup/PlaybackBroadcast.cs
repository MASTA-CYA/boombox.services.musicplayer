using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Player;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Linq;
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

        // Path of whichever track this loop last reported as playing - lets RunAsync notice a track change
        // itself, entirely within its own single-threaded loop, with no second sender involved. An earlier
        // version of this fix raised a separate Player.CurrentTrackChanged event and had its own independent
        // SendAsync call race against this loop's own tick - both could be in flight around the same moment
        // (most likely exactly when playback starts, since a new tick loop and the first "track changed" both
        // fire together), and whichever one's SendAsync happened to land last on the client overwrote the other -
        // including a trimmed, image-less tick landing after the real image, permanently hiding the art until
        // the next track change. Doing the comparison here instead, with one send per tick, makes that
        // impossible: there's only ever one message in flight for a given tick, in a fixed order. See
        // KNOWN_ISSUES.md #19.
        private static string? _lastSentPlayingTrackPath;

        // Trimmed projection of PlaylistTrack sent on every 500ms tick - EqualizerPreset is dropped entirely
        // (the frontend never reads it off this payload; the equalizer UI fetches presets through its own
        // dedicated hub calls). Image is dropped for every track EXCEPT the currently-playing one, and even then
        // only included on the tick where it just started playing (see RunAsync) - every other tick sends it as
        // null, since the frontend already has it and just needs to hold onto what it was last given. Was
        // previously re-serializing every track's full cover art and EQ object up to ~4x/second regardless of
        // whether anything changed - see KNOWN_ISSUES.md #19.
        private sealed class PlaybackTickTrack
        {
            public string Album { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public double PlayedDuration { get; set; }
            public double TotalDuration { get; set; }
            public bool IsPlaying { get; set; }
            public bool IsFavourite { get; set; }
            public string Path { get; set; } = string.Empty;
            // Deliberately nullable, no default - null (not empty string) means "no change this tick, client
            // should keep whatever it already has" (see the frontend's own carry-forward logic in
            // player.component.ts). Only ever populated for the playing track on the tick it changed.
            public string? Image { get; set; }
        }

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

                    var playingPath = playbackInfo.Tracks?.FirstOrDefault(track => track.IsPlaying)?.Path;
                    var trackJustChanged = !string.Equals(playingPath, _lastSentPlayingTrackPath);
                    _lastSentPlayingTrackPath = playingPath;

                    // Deliberately re-shaped into PlaybackTickTrack rather than serializing playbackInfo.Tracks
                    // directly - see that class's doc comment for why Image/EqualizerPreset are dropped here.
                    var tickPayload = new
                    {
                        playbackInfo.PlayerState,
                        playbackInfo.HasReachedEndOfPlaylist,
                        Tracks = playbackInfo.Tracks?.Select(track => new PlaybackTickTrack
                        {
                            Album = track.Album,
                            Name = track.Name,
                            Artist = track.Artist,
                            PlayedDuration = track.PlayedDuration,
                            TotalDuration = track.TotalDuration,
                            IsPlaying = track.IsPlaying,
                            IsFavourite = track.IsFavourite,
                            Path = track.Path,
                            Image = trackJustChanged && track.IsPlaying ? track.Image : null
                        }).ToList()
                    };

                    var playbackInfoJson = JsonSerializer.Serialize(tickPayload, JsonSerializationHelper.SerializerOptions);
                    await _playerHub.Clients.All.SendAsync("ReceivePlaybackInformation", playbackInfoJson, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected whenever Stop() cancels the token while a SendAsync write happens to be in flight
                // (e.g. reaching the end of the playlist) - Stop() has already nulled _cts and this wasn't a
                // real failure, so it shouldn't be logged as an error or trigger the self-heal restart below.
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
