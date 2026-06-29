using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace MusicPlayer.PlaylistManagement.Models
{
    public class Playlist
    {
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        public string Name { get; set; }
        public bool CanEdit { get; set; }
        public List<PlaylistTrack> Tracks { get; set; }
    }
}
