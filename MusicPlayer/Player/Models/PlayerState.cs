namespace MusicPlayer.Player.Models
{
    public class PlayerState
    {
        public bool IsPlaying { get; set; }
        public bool HasNext { get; set; }
        public bool HasPrevious { get; set; }
        public PlaybackMode Mode { get; set; } = PlaybackMode.Sequential;
        public AudioOutput AudioOutput { get; set; } = AudioOutput.Speakers;
    }
}
