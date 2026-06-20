using MusicPlayer.Player.Models;
using NAudio.Wave;

namespace MusicPlayer.Player
{
    public class EnhancedAudioFileReader : AudioFileReader
    {
        public string OriginalFilePath { get; set; }
        public string ServerFilepath { get; set; }
        public EqualizerPreset EqualizerPreset { get; set; }

        public EnhancedAudioFileReader(string serverFileName, string originalFileName, EqualizerPreset equalizerPreset) : base(serverFileName ?? originalFileName)
        {
            OriginalFilePath = originalFileName;
            ServerFilepath = serverFileName;
            EqualizerPreset = equalizerPreset;
        }
    }
}
