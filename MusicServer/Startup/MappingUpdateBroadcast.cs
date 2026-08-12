using Microsoft.AspNetCore.SignalR;
using MusicPlayer.LibraryManagement;
using MusicServer.Helpers;
using MusicServer.Hubs;
using Newtonsoft.Json;

namespace MusicServer.Startup
{
    // Replaces BroadcastController's old mapping-update polling loop (and the self-HTTP-POST that used to
    // start/stop it from LibraryHub.GetLibraryAsync). MusicPlayer can't hold an IHubContext itself — that
    // would mean MusicPlayer referencing MusicServer, a circular project reference — so instead MusicServer,
    // which already owns the DI container, subscribes once here to the plain C# event MappingUpdate raises
    // on every mutation, and forwards each one straight to LibraryHub's clients. No more timer, no more
    // polling a shared object hoping something changed.
    public static class MappingUpdateBroadcast
    {
        public static void Initialize(WebApplication app)
        {
            var libraryHubContext = app.Services.GetRequiredService<IHubContext<LibraryHub>>();

            LibraryManager.Instance.MappingUpdate.Changed += async (sender, _) =>
            {
                try
                {
                    var mappingUpdateJson = JsonConvert.SerializeObject(sender, JsonSerializationHelper.NamingSerializerSettings);
                    await libraryHubContext.Clients.All.SendAsync("ReceiveMappingUpdate", mappingUpdateJson);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            };
        }
    }
}
