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

            // Kept across ticks rather than re-declared inside the loop (the old `List<Playlist> playlists = []`
            // was local to each iteration) - see the "deliberately doesn't broadcast" comment below for why.
            List<Playlist> lastKnownGoodPlaylists = [];
            bool isCurrentlyFailing = false;

            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    try
                    {
                        // Used to also merge in a "Playlists" Redis key here, but nothing in the app ever wrote
                        // that key - RedisCache.GetAsync("Playlists") was throwing "key does not exist" on every
                        // single tick forever, not reporting a real outage, so it just warned (and toasted a
                        // snackbar via SignalRErrorSink) every 2 seconds for no reason. Removed rather than merely
                        // silenced: caching the full playlist JSON in Redis would be a meaningful amount of data to
                        // push through it on every read anyway, so Mongo-only is the right shape here, not just a
                        // fallback.
                        lastKnownGoodPlaylists = await PlaylistManager.Instance.GetPlaylistsAsync();

                        // Only log the "back to normal" transition, not every single healthy tick - LogInformation
                        // is below SignalRErrorSink's Warning threshold (see Program.cs), so this doesn't toast;
                        // it's here for the server log only.
                        if (isCurrentlyFailing)
                        {
                            _logger.LogInformation("Playlist loading recovered");
                            isCurrentlyFailing = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Edge-triggered: only log (and therefore toast, via SignalRErrorSink) once, when an
                        // outage STARTS - not on every failed 2-second tick for its entire duration. A transient
                        // Mongo blip lasting 30+ seconds (a real one prompted this fix - MongoConnectionException/
                        // socket timeout, not an app bug) previously produced a fresh "Unable to load playlists"
                        // toast every single tick, stacking up dozens of near-identical notifications for what
                        // was really one ongoing problem.
                        if (!isCurrentlyFailing)
                        {
                            _logger.LogWarning(ex, "Unable to load playlists");
                            isCurrentlyFailing = true;
                        }

                        // Deliberately skips broadcasting here rather than falling through to send an empty list
                        // (the old `finally` block always sent, even on failure) - a transient outage shouldn't
                        // wipe out every client's already-displayed playlists just because this one fetch failed;
                        // better to keep showing the last known good data until a fresh fetch actually succeeds.
                        continue;
                    }

                    var playlistJson = JsonSerializer.Serialize(lastKnownGoodPlaylists, JsonSerializationHelper.SerializerOptions);
                    await _playlistHub.Clients.All.SendAsync("ReceivePlaylists", playlistJson, token);
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
