namespace MusicPlayer.LyricsManagement.Models
{
    // Where a track's cached lyrics record came from - shown in the UI as provenance, and used by
    // LyricsManager to distinguish "checked, nothing available anywhere" (NotFound) from a document that
    // simply hasn't been looked up yet (no document at all). Without a NotFound value, a silent track (no
    // embedded lyrics, nothing on LRCLIB, no sidecar file) would be re-queried against LRCLIB every single
    // time its lyrics dialog is opened.
    public enum LyricsSource
    {
        Embedded,
        Lrclib,
        Sidecar,
        Manual,
        NotFound
    }
}
