using MongoDB.Bson;
using System.Collections.Generic;

namespace MusicPlayer.PlaylistManagement.Models
{
    public class Playlist
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public bool CanEdit { get; set; }
        public List<PlaylistTrack> Tracks { get; set; }
    }
}
