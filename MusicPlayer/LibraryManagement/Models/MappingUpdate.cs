using System;

namespace MusicPlayer.LibraryManagement.Models
{
    // Raises Changed on every mutation so a subscriber can push updates the instant something actually happens,
    // instead of a poller having to guess how often to check. MusicServer's Program.cs subscribes once, at
    // startup, and forwards each change straight to the LibraryHub clients — see the "no longer using
    // BroadcastController for mapping updates" change.
    public sealed class MappingUpdate
    {
        private double _directoryCount;
        private double _mappedDirectories;
        private string _message;
        private string _error;
        private bool _isComplete;

        public event EventHandler Changed;

        public double DirectoryCount
        {
            get => _directoryCount;
            set { _directoryCount = value; OnChanged(); }
        }

        public double MappedDirectories
        {
            get => _mappedDirectories;
            set { _mappedDirectories = value; OnChanged(); }
        }

        public int Percent
        {
            get
            {
                if (DirectoryCount == 0) return 0;
                return (int)((MappedDirectories / DirectoryCount) * 100);
            }
        }

        public string Message
        {
            get => _message;
            set { _message = value; OnChanged(); }
        }

        public string Error
        {
            get => _error;
            set { _error = value; OnChanged(); }
        }

        public bool IsComplete
        {
            get => _isComplete;
            set { _isComplete = value; OnChanged(); }
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
