using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;

namespace MusicPlayer.LibraryManagement.Models
{
    // Persisted snapshot of one completed full-library mapping run (LibraryManager.GetAlbums), written once by
    // MusicServer.Startup.MappingUpdateBroadcast when it observes MappingUpdate.IsComplete become true. Every run
    // is timestamped (StartedAtUtc/CompletedAtUtc) so the Angular history view can list/sort runs chronologically.
    //
    // This collection deliberately uses explicit snake_case BsonElement names, unlike Album/Playlist/EqualizerPreset
    // which rely on the Mongo driver's default (PascalCase, matching the C# property name) — a convention chosen
    // for this new collection specifically, not a retrofit of the older models.
    public sealed class MappingStatistic
    {
        [BsonId] public ObjectId Id { get; set; }
        [BsonElement("started_at")] public DateTime StartedAtUtc { get; set; }
        [BsonElement("completed_at")] public DateTime CompletedAtUtc { get; set; }
        [BsonElement("duration_ms")] public double DurationMs { get; set; }
        [BsonElement("directory_count")] public double DirectoryCount { get; set; }
        [BsonElement("mapped_directories")] public double MappedDirectories { get; set; }
        [BsonElement("bytes_broadcast")] public long BytesBroadcast { get; set; }
        [BsonElement("error")] public string Error { get; set; }
        [BsonElement("samples")] public List<MappingStatisticSample> Samples { get; set; } = new List<MappingStatisticSample>();
    }

    // One process-level CPU/memory reading, taken roughly once a second during the run.
    public sealed class MappingStatisticSample
    {
        [BsonElement("timestamp")] public DateTime TimestampUtc { get; set; }
        [BsonElement("cpu_percent")] public double CpuPercent { get; set; }
        [BsonElement("memory_mb")] public double MemoryMb { get; set; }
    }
}
