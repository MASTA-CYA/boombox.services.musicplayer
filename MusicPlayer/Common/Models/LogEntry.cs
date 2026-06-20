using System;

namespace MusicPlayer.Models
{
    public class LogEntry
    {
        public DateTime TimeStamp { get; set; }
        public Severity Severity { get; set; }
        public Exception Exception { get; set; }
        public string Source { get; set; }
        public string Line { get; set; }
    }
}
