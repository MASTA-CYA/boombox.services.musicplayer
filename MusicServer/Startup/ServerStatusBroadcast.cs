using Microsoft.AspNetCore.SignalR;
using MusicServer.Hubs;
using System.Reflection;

namespace MusicServer.Startup
{
    // Replaces BroadcastController's Server Updates region. Same story as PlaylistBroadcast — ServerHub already
    // lives in MusicServer, so the old self-HTTP-POST through ServerHttpClient/BroadcastController was pure
    // indirection. ServerHub now calls Start()/Stop() directly.
    public static class ServerStatusBroadcast
    {
        private static readonly object _lock = new object();

        private static IHubContext<ServerHub> _serverHub;
        private static ILogger _logger;
        private static CancellationTokenSource _cts;

        public static void Initialize(WebApplication app)
        {
            _serverHub = app.Services.GetRequiredService<IHubContext<ServerHub>>();
            _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MusicServer.Startup.ServerStatusBroadcast");
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
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                // Was `InformationalVersion[..13]` — an arbitrary fixed-length substring that happened to roughly
                // fit the old non-semantic "1.0.<date>.<time>" wildcard version. Now that Nerdbank.GitVersioning
                // controls this attribute (see MusicServer/version.json), the string is a real semver like
                // "1.2.3+a1b2c3d" and can just be sent as-is — no length assumption to keep in sync.
                var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
                while (await timer.WaitForNextTickAsync(token))
                    await _serverHub.Clients.All.SendAsync("ReceiveServerUpdates", version, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Server status broadcast loop failed");
                lock (_lock) { _cts = null; }
            }
        }
    }
}
