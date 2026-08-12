using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MusicPlayer.Common
{
    // MusicPlayer has no DI container of its own — the same constraint that led to the MappingUpdate.Changed event
    // bridge for SignalR (MusicPlayer referencing MusicServer to get an IHubContext would be circular) means it
    // can't take an ILogger<T> via constructor injection either. MusicServer.Program.cs calls Initialize once at
    // startup with its own Serilog-backed ILoggerFactory; everything in MusicPlayer logs through CreateLogger<T>()
    // instead of the previous mix of Console.WriteLine and a self-HTTP-POST back into MusicServer's own REST API
    // (LoggingController, now removed — see KNOWN_ISSUES.md).
    //
    // Defaults to NullLoggerFactory so nothing null-refs if a MusicPlayer singleton is touched before Initialize
    // runs, or in a host that never calls it at all (e.g. PlayerConsole) — logging silently no-ops instead.
    //
    // Important ordering constraint: this must be initialized before ANY MusicPlayer singleton (LibraryManager.
    // Instance, FileManager.Instance, etc.) is first touched. Each of those classes reads AppLogger.CreateLogger<T>()
    // into a static readonly field, which runs once, at the type's static-constructor time — whatever factory
    // AppLogger holds at that moment is what that class is stuck with for the process's lifetime.
    public static class AppLogger
    {
        private static ILoggerFactory _factory = NullLoggerFactory.Instance;

        public static void Initialize(ILoggerFactory factory) => _factory = factory ?? NullLoggerFactory.Instance;

        public static ILogger CreateLogger<T>() => _factory.CreateLogger<T>();

        public static ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);
    }
}
