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
        // This model pulls double duty as both the Mongo-backed document AND the plain-event broadcast payload
        // (TrackUserDataChanged -> TrackUserDataBroadcast.cs), and that broadcast path serializes with
        // System.Text.Json (MusicServer.Helpers.JsonSerializationHelper.SerializerOptions), which has no
        // converter registered for MongoDB's ObjectId and throws trying to serialize one. A System.Text.Json
        // [JsonIgnore] on Id would fix that, but this project (MusicPlayer, old-style .NET Framework 4.8 csproj)
        // has no reference to System.Text.Json at all - fully qualifying the attribute doesn't avoid needing the
        // assembly, it still failed to compile (CS0234) the first time this was tried. Fixed instead at the
        // broadcast call site: TrackUserDataBroadcast.cs serializes a trimmed anonymous projection that never
        // includes Id, so System.Text.Json never sees the ObjectId. Newtonsoft's ObjectIdConverter below still
        // applies everywhere Id actually needs to round-trip (Mongo, the AppData file cache).
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        public string Path { get; set; }
        public int TimesPlayed { get; set; }
        public bool IsFavourite { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
