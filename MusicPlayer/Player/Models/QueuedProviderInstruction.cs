using NAudio.Wave;
using System.Collections.Generic;

namespace MusicPlayer.Player.Models
{
    public class QueuedProviderInstruction
    {
        public IEnumerable<ISampleProvider> Providers { get; set; } = new List<ISampleProvider>();
        public string IndexPath { get; set; }
        public bool CanAppend { get; set; }
    }
}
