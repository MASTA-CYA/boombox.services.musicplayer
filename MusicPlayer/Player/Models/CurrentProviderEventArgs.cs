using NAudio.Wave;
using System;

namespace MusicPlayer.Player.Models
{
    public class CurrentProviderEventArgs : EventArgs
    {
        private ISampleProvider _provider;
        public ISampleProvider Provider { get => _provider; }

        public CurrentProviderEventArgs(ISampleProvider provider) => _provider = provider;
    }
}
