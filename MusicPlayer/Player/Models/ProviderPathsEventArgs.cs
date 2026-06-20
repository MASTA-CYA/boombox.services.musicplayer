using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace MusicPlayer.Player.Models
{
    public class ProviderPathsEventArgs : EventArgs
    {
        public IEnumerable<ISampleProvider> Providers { get; }

        public ProviderPathsEventArgs(IEnumerable<ISampleProvider> providers) => Providers = providers;
    }
}
