using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;

namespace MusicServer.Hubs
{
    public class ServerHub : Hub
    {
        public async Task<bool> IsServerRunningAsync()
        {
            await Task.CompletedTask;
            return true;
        }

        public async Task StartServerStatusUpdatedAsync() => await ServerHttpClient.Instance.StartServerStatusUpdatesAsync();

        public async Task StopServerStatusUpdatedAsync() => await ServerHttpClient.Instance.StopServerStatusUpdatesAsync();
    }
}
