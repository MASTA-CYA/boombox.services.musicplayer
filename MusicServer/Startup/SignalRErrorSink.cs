using Serilog.Core;
using Serilog.Events;

namespace MusicServer.Startup
{
    // Replaces LoggingController's old dual job of "log this" + "also tell connected clients about it over
    // SignalR". Custom Serilog sinks are the standard way to fan a log event out to an extra destination, so
    // instead of one controller action doing both, the file sink (configured in Program.cs) handles logging and
    // this sink handles the live broadcast — Warning-and-above events only, forwarded to ServerHub as
    // "ReceiveServerHubError", which the Angular UI already listens for.
    //
    // Publish is set in Program.cs after app.Build(), once IHubContext<ServerHub> exists — this sink itself is
    // constructed earlier as part of the bootstrap Log.Logger, before the DI container is available, so it can't
    // take the hub context as a constructor argument. Static + settable is the same tradeoff MusicPlayer.Common.
    // AppLogger makes for the same reason.
    public sealed class SignalRErrorSink : ILogEventSink
    {
        public static Func<string, Task> Publish { get; set; } = _ => Task.CompletedTask;

        private readonly LogEventLevel _minimumLevel;

        public SignalRErrorSink(LogEventLevel minimumLevel) => _minimumLevel = minimumLevel;

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < _minimumLevel) return;

            _ = Publish(logEvent.RenderMessage());
        }
    }
}
