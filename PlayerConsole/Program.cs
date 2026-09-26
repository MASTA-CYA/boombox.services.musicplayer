namespace PlayerConsole
{
    // PlayerConsole is a throwaway console harness for exercising MusicPlayer/MusicServer code directly -
    // testing and debugging playback-engine behavior, one-off data migrations, and other hard-to-reach paths
    // without going through the full SignalR/MusicServer host. It references both MusicPlayer and MusicServer
    // (see PlayerConsole.csproj) so either layer's code can be called from here ad hoc.
    //
    // Intentionally empty in this snapshot - whatever was being exercised here was scratch work tied to a
    // specific local debugging session, not a reusable tool, so it's been cleared out rather than committed.
    internal class Program
    {
        static void Main(string[] args)
        {
        }
    }
}
