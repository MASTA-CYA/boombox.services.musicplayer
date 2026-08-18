namespace MusicPlayer.LyricsManagement.Models
{
    public class LyricsLine
    {
        // Null = unsynced/plain lyrics - no per-line timing available for this line (either the whole track's
        // lyrics are plain text, e.g. a manual paste with no LRC timestamps, or a source that only offers
        // unsynced text). The frontend falls back to a static, non-highlighted display when every line in a
        // Lyrics document has a null TimestampMs.
        public int? TimestampMs { get; set; }
        public string Text { get; set; }
    }
}
