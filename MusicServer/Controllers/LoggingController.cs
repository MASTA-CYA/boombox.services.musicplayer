using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Models;
using MusicServer.Hubs;

namespace MusicServer.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoggingController(ILogger<BroadcastController> logger, IHubContext<ServerHub> serverHubContext) : Controller
    {
        private readonly ILogger<BroadcastController> _logger = logger;
        private readonly IHubContext<ServerHub> _severHubContext = serverHubContext;
        private static CancellationTokenSource _tokenSource = new();

        [HttpPost("LogEntry")]
        public void LogEntry([FromBody] LogEntry entry)
        {
            if (_tokenSource == null) return;
            if (_tokenSource.IsCancellationRequested) return;

            var token = _tokenSource.Token;
            Task.Run(async () => await LogEntryAsync(token, entry), token);
        }

        private async Task LogEntryAsync(CancellationToken? token, LogEntry entry)
        {
            switch (entry.Severity)
            {
                case Severity.Warning:
                    _logger.LogWarning($"[{entry.TimeStamp:yyyy-MM-dd HH:mm:ss}][{entry.Source}] {entry.Exception}");
                    await _severHubContext.Clients.All.SendAsync("ReceiveServerHubError", entry.Line, token);
                    break;
                case Severity.Error:
                    _logger.LogError($"[{entry.TimeStamp:yyyy-MM-dd HH:mm:ss}][{entry.Source}] {entry.Exception}");
                    await _severHubContext.Clients.All.SendAsync("ReceiveServerHubError", entry.Line, token);
                    break;
                case Severity.Information:
                default:
                    _logger.LogInformation($"[{entry.TimeStamp:yyyy-MM-dd HH:mm:ss}][{entry.Source}] {entry.Exception}");
                    await _severHubContext.Clients.All.SendAsync("ReceiveServerHubError", entry.Exception, token);
                    break;
            }
        }
    }
}
