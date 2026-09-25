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
                    // Projected rather than serializing `data` directly - TrackUserData.Id is a MongoDB ObjectId,
                    // and System.Text.Json has no converter for it (would throw). MusicPlayer can't carry a
                    // System.Text.Json [JsonIgnore] on the model itself (old-style .NET Framework 4.8 project,
                    // no reference to that assembly - see TrackUserData.cs's comment), so it's dropped here at
                    // the one place that actually serializes with System.Text.Json instead. The frontend's
                    // IUserTrackData interface never reads an id off this broadcast anyway.
                    var json = JsonSerializer.Serialize(new
                    {
                        data.Path,
                        data.TimesPlayed,
                        data.IsFavourite,
                        data.UpdatedAtUtc
                    }, JsonSerializationHelper.SerializerOptions);

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
