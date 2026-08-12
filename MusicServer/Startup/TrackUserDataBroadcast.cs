using Microsoft.AspNetCore.SignalR;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicServer.Helpers;
using MusicServer.Hubs;
using System.Text.Json;

namespace MusicServer.Startup
{
    // Replaces MetaDataController/ServerHttpClient.UpdateTrackerUserDataAsync — the last remaining self-HTTP-POST
    // loopback in the app. LibraryManager.UpdateTrackUserDataAsync (favourites toggled, play counts incremented)
    // used to serialize a TrackUserData, POST it to MusicServer's own REST API, which just read the raw body back
    // off the request and fanned it out to three hubs. Same plain-event bridge as MappingUpdate.Changed and
    // Player.PlaybackBroadcastStopRequested — subscribed once here at startup instead.
    public static class TrackUserDataBroadcast
    {
        public static void Initialize(WebApplication app)
        {
            var libraryHub = app.Services.GetRequiredService<IHubContext<LibraryHub>>();
            var playerHub = app.Services.GetRequiredService<IHubContext<PlayerHub>>();
            var playlistHub = app.Services.GetRequiredService<IHubContext<PlaylistHub>>();
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MusicServer.Startup.TrackUserDataBroadcast");

            LibraryManager.Instance.TrackUserDataChanged += async (sender, data) =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(data, JsonSerializationHelper.SerializerOptions);

                    await libraryHub.Clients.All.SendAsync("ReceiveTrackUserData", json);
                    await playerHub.Clients.All.SendAsync("ReceiveTrackUserData", json);
                    await playlistHub.Clients.All.SendAsync("ReceiveTrackUserData", json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unable to broadcast track user data for {TrackPath}", data?.Path);
                }
            };
        }
    }
}
