using MongoDB.Bson.Serialization.Attributes;
using System;

namespace MusicPlayer.LibraryManagement.Models
{
    public sealed class Track
    {
        [BsonIgnore] public int DiscNumber { get; set; }
        [BsonIgnore] public int TrackNumber { get; set; }
        [BsonIgnore] public string Name { get; set; }
        [BsonIgnore] public string Artist { get; set; }
        public int TimesPlayed { get; set; }
        public bool IsFavourite { get; set; }
        [BsonIgnore] public double Duration { get; set; }
        public string Path { get; set; }
        public Guid EqualizerGuid { get; set; }
    }
}
