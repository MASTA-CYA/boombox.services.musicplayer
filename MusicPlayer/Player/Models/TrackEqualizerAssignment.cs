using System;

namespace MusicPlayer.Player.Models
{
    // Read model for the Settings "Equalizer" tab's track-assignment list - a track paired with the custom
    // per-track EqualizerPreset it's using (Track.EqualizerGuid pointing at an EqualizerPreset.Guid). Track/Album
    // Name/Path fields are duplicated here rather than referencing the Track/Album types directly because
    // Track.Name/Album.Name are [BsonIgnore] (not stored in Mongo - see LibraryManager.GetTrackEqualizerAssignmentsAsync
    // for where these are actually sourced from, the JSON file cache rather than Mongo).
    public class TrackEqualizerAssignment
    {
        public string AlbumName { get; set; }
        public string AlbumPath { get; set; }
        public string TrackName { get; set; }
        public string TrackPath { get; set; }
        public Guid PresetGuid { get; set; }
    }
}
