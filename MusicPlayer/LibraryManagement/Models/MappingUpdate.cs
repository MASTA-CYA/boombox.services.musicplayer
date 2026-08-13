using System;

namespace MusicPlayer.LibraryManagement.Models
{
    // Raises Changed on every mutation so a subscriber can push updates the instant something actually happens,
    // instead of a poller having to guess how often to check. MusicServer.Startup.MappingUpdateBroadcast
    // subscribes once, at startup, and forwards each change straight to the LibraryHub clients — see the
    // "no longer using BroadcastController for mapping updates" change.
    public sealed class MappingUpdate
    {
        private double _directoryCount;
        private double _mappedDirectories;
        private string _message;
        private string _error;
        private bool _isComplete;
        private DateTime? _startedAtUtc;
        private double _cpuPercent;
        private double _memoryMb;
        private MappingRunType _runType;

        public event EventHandler Changed;

        // Set once, at the start of a full-library rescan (LibraryManager.GetAlbums) — the client derives elapsed
        // time from this instead of the server pushing a separately-ticking ElapsedMs field. Also doubles as the
        // "did a new run start" signal for MusicServer.Startup.MappingUpdateBroadcast's run-history bookkeeping.
        public DateTime? StartedAtUtc
        {
            get => _startedAtUtc;
            set { _startedAtUtc = value; OnChanged(); }
        }

        // Set alongside StartedAtUtc at the start of every run - FullScan by LibraryManager.GetAlbums (walks disk,
        // re-reads file metadata), Cache by LibraryHub.GetLibraryResponseFromFileAsync (just loads the already-
        // mapped albums back out of the local JSON cache, the common case on a normal startup). Lets both the live
        // progress screen and the persisted MappingStatistic history distinguish the two instead of a cache load
        // looking identical to - or, before this, simply not showing - a full rescan.
        public MappingRunType RunType
        {
            get => _runType;
            set { _runType = value; OnChanged(); }
        }

        // Process-level (not system-wide) CPU/memory, sampled roughly once a second while a rescan is running —
        // see LibraryManager.GetAlbums's sampling timer. CpuPercent is normalized 0-100 (divided by
        // Environment.ProcessorCount) rather than allowed to exceed 100 on a multi-core machine.
        public double CpuPercent
        {
            get => _cpuPercent;
            set { _cpuPercent = value; OnChanged(); }
        }

        public double MemoryMb
        {
            get => _memoryMb;
            set { _memoryMb = value; OnChanged(); }
        }

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
