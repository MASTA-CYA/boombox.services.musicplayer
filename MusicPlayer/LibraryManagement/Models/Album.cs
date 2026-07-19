using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MusicPlayer.FileManagement.Interfaces;
using MusicPlayer.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MusicPlayer.LibraryManagement.Models
{
    public sealed class Album : IFileWritable
    {
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        [BsonRepresentation(BsonType.String)] public Guid Guid { get; set; } = Guid.NewGuid();
        [BsonIgnore] public string Name { get; set; }
        [BsonIgnore] public string Artist { get; set; }
        [BsonIgnore] public string Genre { get; set; }
        [BsonIgnore] public int Year { get; set; }
        [BsonIgnore] public int NumberOfDiscs { get; set; }
        [BsonIgnore] public int NumberOfTracks { get; set; }
        [BsonIgnore] public double Duration { get; set; }
        [BsonIgnore] public string Image { get; set; }
        [BsonIgnore] public string Encoding { get; set; }
        public bool Played { get; set; }
        public DateTime DateMapped { get; set; }
        public string Path { get; set; }
        public List<Track> Tracks { get; set; }

        public (string id, string content) GetFileContent()
            => (id: Guid.ToString(), content: JsonConvert.SerializeObject(this, JsonSerializationHelper.FileSerializerSettings));
    }
}
