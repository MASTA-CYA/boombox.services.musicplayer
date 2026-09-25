using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MusicPlayer.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MusicPlayer.LyricsManagement.Models
{
    // One document per track, keyed by TrackPath rather than a Guid/name like EqualizerPreset - lyrics are
    // always 1:1 with a specific file, so the path itself is the natural key. See MongoDbClient's Lyrics
    // region for the upsert-by-TrackPath pattern this relies on, and LyricsManager for how/when a document
    // gets created (lazily, the first time a track's lyrics are actually requested - never during library
    // mapping, which would mean a network round-trip per track across the whole library).
    public class Lyrics
    {
        [BsonId][JsonConverter(typeof(ObjectIdConverter))] public ObjectId Id { get; set; }
        public string TrackPath { get; set; }
        public List<LyricsLine> Lines { get; set; } = new List<LyricsLine>();
        public LyricsSource Source { get; set; }
        public DateTime FetchedAtUtc { get; set; }
    }
}
