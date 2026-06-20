namespace MusicPlayer.LibraryManagement.Models
{
    public sealed class MappingUpdate
    {
        public double DirectoryCount { get; set; }
        public double MappedDirectories { get; set; }
        public int Percent
        {
            get
            {
                if (DirectoryCount == 0) return 0;
                return (int)((MappedDirectories / DirectoryCount) * 100);
            }
        }
        public string Message { get; set; }
        public string Error { get; set; }
        public bool IsComplete { get; set; }
    }
}
