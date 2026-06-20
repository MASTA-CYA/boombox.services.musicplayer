using MongoDB.Bson.Serialization.Attributes;
using MusicPlayer.Player.Models;

namespace MusicPlayer.PlaylistManagement.Models
{
    public class PlaylistTrack
    {
        public string Image { get; set; }
        public string Album { get; set; }
        public string Name { get; set; }
        public string Artist { get; set; }
        [BsonIgnore] public double PlayedDuration { get; set; }
        public double TotalDuration { get; set; }
        [BsonIgnore] public bool IsPlaying { get; set; }
        public bool IsFavourite { get; set; }
        public string Path { get; set; }
        [BsonIgnore] public EqualizerPreset EqualizerPreset { get; set; }

    }
}
