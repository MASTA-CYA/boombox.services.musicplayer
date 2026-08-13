namespace MusicPlayer.LibraryManagement.Models
{
    // Distinguishes MappingUpdate/MappingStatistic runs that walked the disk and re-read file metadata
    // (LibraryManager.GetAlbums) from runs that just loaded the already-mapped albums back out of the local
    // JSON cache (LibraryHub.GetLibraryResponseFromFileAsync) - the common case on every normal startup. Plain
    // int enum, same convention as Player.Models.PlaybackMode: Newtonsoft serializes it as a number and the
    // Angular side maps it to a display label rather than the server sending a string.
    public enum MappingRunType
    {
        FullScan,
        Cache
    }
}
