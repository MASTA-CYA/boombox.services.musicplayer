using System.Runtime.Versioning;

namespace MusicServer.Helpers
{
    // AsioOut and WasapiOut (MusicPlayer.Player.Player's two audio outputs) are COM-based and expect calls to
    // originate from an STA thread - every action that touches the live audio pipeline needs to run through
    // here rather than on whatever thread happened to invoke it (a SignalR hub method's thread-pool thread, or
    // a background broadcast loop's PeriodicTimer continuation - neither is STA).
    //
    // Spins up a brand new STA thread per call and blocks the caller until it finishes; nothing is pooled or
    // reused. This was previously PlayerHub's own private ExecuteOnPlayerThread, used only for calls made
    // directly from a client's hub invocation - centralized here (PlayerHub now delegates to it) so
    // MusicServer/Startup/AudioOutputAvailabilityBroadcast.cs can reach the same STA guarantee for a
    // server-initiated switch (a USB device disappearing while nothing is playing) without a SignalR hub
    // instance of its own to run it from.
    [SupportedOSPlatform("windows")]
    public static class PlayerThreadExecutor
    {
        public static void Execute(Action action, Action<Exception> onError = null)
        {
            var thread = new Thread(() =>
            {
                try { action.Invoke(); }
                catch (Exception ex) { onError?.Invoke(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
    }
}
