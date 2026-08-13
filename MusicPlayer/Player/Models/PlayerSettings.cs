using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace MusicPlayer.Player.Models
{
    // Single-document collection - there's only ever one PlayerSettings row, holding whichever playback
    // preferences should survive a MusicServer restart. Currently just the last-selected AudioOutput; extendable
    // later without a migration since Mongo documents don't need one.
    public sealed class PlayerSettings
    {
        // Without this converter, Newtonsoft serializes ObjectId as a nested object instead of a plain string -
        // same reasoning as Album.Id/MappingStatistic.Id, see ObjectIdConverter.cs. Not actually consumed by the
        // Angular client for this model, but kept consistent with every other Mongo-backed model in the app.
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        public AudioOutput AudioOutput { get; set; } = AudioOutput.Speakers;
    }
}
