using NAudio.Wave;
using System;

namespace MusicPlayer.Extensions
{
    public static class SampleProviderExtensions
    {
        public static void Reset(this AudioFileReader reader) => reader.CurrentTime = TimeSpan.FromSeconds(0);
        public static void SeekToEnd(this AudioFileReader reader) => reader.CurrentTime = reader.TotalTime;
    }
}
