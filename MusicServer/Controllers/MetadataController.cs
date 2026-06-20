using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicServer.Hubs;

namespace MusicServer.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MetaDataController(ILogger<BroadcastController> logger, IHubContext<PlayerHub> playerHub, IHubContext<PlaylistHub> playlistHub, IHubContext<LibraryHub> libraryHub) : ControllerBase
    {
        private readonly ILogger<BroadcastController> _logger = logger;
        private readonly IHubContext<PlayerHub> _playerHub = playerHub;
        private readonly IHubContext<PlaylistHub> _playlistHub = playlistHub;
        private readonly IHubContext<LibraryHub> _libraryHub = libraryHub;

        [HttpPost("UpdateTrackUserData")]
        public async Task UpdateTrackUserDataAsync()
        {
            string? body = null;
            using (var reader = new StreamReader(Request.Body))
                body = await reader.ReadToEndAsync();

            await _libraryHub.Clients.All.SendAsync("ReceiveTrackUserData", body);
            await _playerHub.Clients.All.SendAsync("ReceiveTrackUserData", body);
            await _playlistHub.Clients.All.SendAsync("ReceiveTrackUserData", body);
        }
    }
}
