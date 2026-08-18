using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MusicPlayer.Helpers;
using Newtonsoft.Json;
using System;

namespace MusicPlayer.LibraryManagement.Models
{
    // Doubles as both the Mongo-backed document (one per track, keyed by Path - see MongoDbClient's Track User
    // Data region) and the plain event/broadcast payload LibraryManager.TrackUserDataChanged already raised
    // before this collection existed. Path/TimesPlayed/IsFavourite are unchanged from that original shape
    // deliberately, since the Angular client's IUserTrackData interface already matches them field-for-field -
    // Id/UpdatedAtUtc are new but additive, so existing broadcast consumers just ignore them.
    public class TrackUserData
    {
        // Also needs System.Text.Json's own JsonIgnore (fully qualified - a `using System.Text.Json.Serialization;`
        // would collide with Newtonsoft.Json's JsonConverter/JsonIgnore attribute names already used unqualified
        // in this file). This model pulls double duty as both the Mongo-backed document AND the plain-event
        // broadcast payload (TrackUserDataChanged -> TrackUserDataBroadcast.cs), and that broadcast path
        // serializes with System.Text.Json (MusicServer.Helpers.JsonSerializationHelper.SerializerOptions),
        // which has no converter registered for MongoDB's ObjectId and throws trying to serialize one. The
        // frontend's IUserTrackData interface never reads an id field from that broadcast anyway, so omitting
        // it there costs nothing - Newtonsoft's ObjectIdConverter still applies everywhere Id actually needs to
        // round-trip (Mongo, the AppData file cache).
        [BsonId][JsonConverter(typeof(ObjectIdConverter))][System.Text.Json.Serialization.JsonIgnore] public ObjectId Id { get; set; }
        public string Path { get; set; }
        public int TimesPlayed { get; set; }
        public bool IsFavourite { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
