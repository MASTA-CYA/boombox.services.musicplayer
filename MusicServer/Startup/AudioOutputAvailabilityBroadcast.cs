using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Player;
using MusicPlayer.Player.Models;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Text.Json;

namespace MusicServer.Startup
{
    // Always-on for the lifetime of the connection (started once from PlayerHub.InitializePlayerAsync, same
    // way ServerHub.StartServerStatusUpdatedAsync starts ServerStatusBroadcast), independent of whether
    // anything is currently playing - PlaybackBroadcast's 500ms loop only runs while audio is actively
    // playing, but a USB device (the Focusrite, or the Corsair headset) can disappear at any time, including
    // while playback is paused/stopped, so availability needs its own loop that's never gated on playback
    // state.
    public static class AudioOutputAvailabilityBroadcast
    {
        private static readonly object _lock = new object();

        private static IHubContext<PlayerHub> _playerHub;
        private static ILogger _logger;
        private static CancellationTokenSource _cts;

        public static void Initialize(WebApplication app)
        {
            _playerHub = app.Services.GetRequiredService<IHubContext<PlayerHub>>();
            _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MusicServer.Startup.AudioOutputAvailabilityBroadcast");
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
            // 2s matches PlaylistBroadcast's polling interval - frequent enough that a USB unplug/replug
            // feels near-immediate in the UI, without polling ASIO/WASAPI device enumeration any more often
            // than a human plugging in a cable would ever notice.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2000));

            // Tracked across ticks so a broadcast only goes out when something actually changed (device
            // plugged/unplugged, or a forced switch) rather than every single tick regardless - the same
            // "don't hammer clients with identical updates" lesson KNOWN_ISSUES.md item #2 already fixed for
            // the other broadcast loops. `false` here just means "broadcast at least once on the first tick"
            // so a client that connected before the first refresh still learns the real state promptly.
            var hasBroadcastOnce = false;
            var lastSpeakersAvailable = true;
            var lastHeadsetAvailable = true;

            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    try
                    {
                        var previousOutput = Player.Instance.PlaybackInformation?.PlayerState?.AudioOutput;
                        var (isSpeakersAvailable, isHeadsetAvailable, requiredFallback) = Player.Instance.RefreshOutputAvailability();

                        if (requiredFallback.HasValue)
                        {
                            // This is a genuinely user-visible event (playback is about to audibly move to a
                            // different output with no direct action from them) - LogWarning here, rather than
                            // just LogInformation, deliberately reaches SignalRErrorSink (wired up at Warning
                            // and above in Program.cs), which turns it into a frontend snackbar automatically.
                            // No separate hub call needed to "announce" the switch - this is the mechanism
                            // already established for surfacing server-side state changes to the UI.
                            _logger.LogWarning("{PreviousOutput} is no longer available; switching to {FallbackOutput}", previousOutput, requiredFallback.Value);

                            // SetAudioOutput touches AsioOut/WasapiOut (COM) and must run on a dedicated STA
                            // thread - same requirement as every other player action PlayerHub marshals via
                            // ExecuteOnPlayerThread, reached here through the same underlying helper since this
                            // loop has no SignalR hub instance of its own to run it from.
                            PlayerThreadExecutor.Execute(
                                () => Player.Instance.SetAudioOutput(requiredFallback.Value),
                                ex => _logger.LogError(ex, "Unable to switch audio output after the active device became unavailable"));
                        }

                        var availabilityChanged = !hasBroadcastOnce || isSpeakersAvailable != lastSpeakersAvailable || isHeadsetAvailable != lastHeadsetAvailable;

                        if (availabilityChanged || requiredFallback.HasValue)
                        {
                            var playbackInfo = Player.Instance.PlaybackInformation;

                            if (playbackInfo != null)
                            {
                                var playbackInfoJson = JsonSerializer.Serialize(playbackInfo, JsonSerializationHelper.SerializerOptions);
                                await _playerHub.Clients.All.SendAsync("ReceivePlaybackInformation", playbackInfoJson, token);
                            }

                            hasBroadcastOnce = true;
                            lastSpeakersAvailable = isSpeakersAvailable;
                            lastHeadsetAvailable = isHeadsetAvailable;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to refresh audio output availability");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected whenever Stop() cancels the token mid-tick - not a real failure, same as
                // PlaybackBroadcast's identical guard.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audio output availability broadcast loop failed");
                lock (_lock) { _cts = null; }
            }
        }
    }
}
