using Microsoft.AspNetCore.SignalR;
using MusicServer.Startup;

namespace MusicServer.Hubs
{
    public class ServerHub : Hub
    {
        public async Task<bool> IsServerRunningAsync()
        {
            await Task.CompletedTask;
            return true;
        }

        public async Task StartServerStatusUpdatedAsync()
        {
            await Task.CompletedTask;
            ServerStatusBroadcast.Start();
        }

        public async Task StopServerStatusUpdatedAsync()
        {
            await Task.CompletedTask;
            ServerStatusBroadcast.Stop();
        }
    }
}
