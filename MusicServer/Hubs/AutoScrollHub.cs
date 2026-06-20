using Microsoft.AspNetCore.SignalR;
using MusicPlayer.AutoScrollManagement;
using MusicServer.Helpers;
using Newtonsoft.Json;

namespace MusicServer.Hubs
{
    public class AutoScrollHub : Hub
    {
        public async Task UpdateLibraryScrollPositionAsync(int horizontal, int vertical)
        {
            await ScrollManager.Instance.UpdateScrollPositionAsync(horizontal, vertical).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                var positionJson = JsonConvert.SerializeObject(await ScrollManager.Instance.GetScrollPositionAsync(), JsonSerializationHelper.NamingSerializerSettings);
                await RedisCache.SetAsync("scrollProsition", positionJson);
            });
        }

        public async Task GetLibraryScrollPositionAsync()
        {
            var position = await ScrollManager.Instance.GetScrollPositionAsync().ConfigureAwait(false);
            var positionJson = JsonConvert.SerializeObject(position, JsonSerializationHelper.NamingSerializerSettings)
                ?? await RedisCache.GetAsync("scrollProsition");

            await Clients.All.SendAsync("ReceiveLibraryScrollPosition", positionJson.ToString());
        }
    }
}
