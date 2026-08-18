namespace MusicPlayer.LyricsManagement
{
    // Mirrors lrclib.net's GET /api/get response shape exactly (field names match their JSON verbatim - see
    // https://lrclib.net/docs - Newtonsoft's default contract resolver matches property names case-insensitively,
    // so "trackName" in the response binds to TrackName here with no [JsonProperty] attributes needed).
    internal class LrclibResponse
    {
        public long Id { get; set; }
        public string TrackName { get; set; }
        public string ArtistName { get; set; }
        public string AlbumName { get; set; }
        public double Duration { get; set; }
        public bool Instrumental { get; set; }
        public string PlainLyrics { get; set; }
        public string SyncedLyrics { get; set; }
    }
}
