namespace MusicPlayer.Player.Models
{
    public class EqualizerFrequencyBand
    {
        public string Name { get; set; }
        public float Frequency { get; set; }
        public float Gain { get; set; } = 0f;
        public float Min { get; set; } = -15f;
        public float Max { get; set; } = 15f;
    }
}
