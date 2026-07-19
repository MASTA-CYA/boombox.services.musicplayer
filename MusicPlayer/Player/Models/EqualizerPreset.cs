using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MusicPlayer.FileManagement.Interfaces;
using MusicPlayer.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MusicPlayer.Player.Models
{
    public class EqualizerPreset : IFileWritable
    {
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        [BsonRepresentation(BsonType.String)] public Guid Guid { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public List<EqualizerFrequencyBand> FrequencyBands { get; set; }
        public bool IsDefault { get; set; }

        public (string id, string content) GetFileContent()
            => (id: Guid.ToString(), content: JsonConvert.SerializeObject(this, JsonSerializationHelper.FileSerializerSettings));
    }
}
