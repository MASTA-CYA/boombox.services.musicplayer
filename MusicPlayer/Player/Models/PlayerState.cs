namespace MusicPlayer.Player.Models
{
    public class PlayerState
    {
        public bool IsPlaying { get; set; }
        public bool HasNext { get; set; }
        public bool HasPrevious { get; set; }
        public PlaybackMode Mode { get; set; } = PlaybackMode.Sequential;
        public AudioOutput AudioOutput { get; set; } = AudioOutput.Speakers;

        // Kept up to date by AudioOutputAvailabilityBroadcast (MusicServer/Startup), polled independently of
        // playback state - a USB device can disappear whether or not anything is currently playing. Default
        // true/true so a brand new PlaybackInformation (before the first availability refresh has run) doesn't
        // render either sidebar icon as greyed out.
        public bool IsSpeakersAvailable { get; set; } = true;
        public bool IsHeadsetAvailable { get; set; } = true;
    }
}
