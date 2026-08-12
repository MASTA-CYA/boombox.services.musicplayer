using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicServer.Helpers;
using MusicServer.Hubs;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text;

namespace MusicServer.Startup
{
    // Replaces BroadcastController's old mapping-update polling loop (and the self-HTTP-POST that used to
    // start/stop it from LibraryHub.GetLibraryAsync). MusicPlayer can't hold an IHubContext itself — that
    // would mean MusicPlayer referencing MusicServer, a circular project reference — so instead MusicServer,
    // which already owns the DI container, subscribes once here to the plain C# event MappingUpdate raises
    // on every mutation, and forwards each one straight to LibraryHub's clients. No more timer, no more
    // polling a shared object hoping something changed.
    //
    // Also doubles as the run-history recorder: it tracks how many bytes it has actually sent over the wire this
    // run (an exact, zero-dependency stand-in for "network usage" — Windows has no simple per-process network
    // counter the way it does for CPU/memory) and a once-a-second sample of the CPU/memory MappingUpdate is
    // already carrying, then persists both as a MappingStatistic the moment IsComplete flips to true.
    public static class MappingUpdateBroadcast
    {
        public static void Initialize(WebApplication app)
        {
            var libraryHubContext = app.Services.GetRequiredService<IHubContext<LibraryHub>>();
            var mappingUpdate = LibraryManager.Instance.MappingUpdate;

            // MappingUpdate.Changed can fire from several different threads in close succession (the mapping
            // foreach loop, the CPU/memory sampling Timer, LibraryHub's cached-load path) — this lock protects the
            // run-accumulator state below from being read/written by two invocations at once. It's only ever held
            // across synchronous bookkeeping, never across an await (can't lock across an await anyway).
            var historyLock = new object();
            long bytesBroadcastThisRun = 0;
            var samples = new List<MappingStatisticSample>();
            DateTime? currentRunStartedAt = null;
            DateTime? lastSampleAt = null;

            mappingUpdate.Changed += async (sender, _) =>
            {
                try
                {
                    var update = (MappingUpdate)sender;
                    string mappingUpdateJson;
                    MappingStatistic statisticToPersist = null;

                    lock (historyLock)
                    {
                        // A different StartedAtUtc means a new run began since the last event — reset this run's
                        // accumulators. Never write back onto `update` itself here: MappingUpdate.Changed handlers
                        // mutating the very instance that raised them would re-trigger Changed recursively.
                        if (update.StartedAtUtc != currentRunStartedAt)
                        {
                            currentRunStartedAt = update.StartedAtUtc;
                            bytesBroadcastThisRun = 0;
                            samples.Clear();
                            lastSampleAt = null;
                        }

                        mappingUpdateJson = JsonConvert.SerializeObject(update, JsonSerializationHelper.NamingSerializerSettings);
                        bytesBroadcastThisRun += Encoding.UTF8.GetByteCount(mappingUpdateJson);

                        var now = DateTime.UtcNow;

                        // Record at most one sample per second, piggybacking on whichever mutation happens to land
                        // near that mark, instead of running a second independent timer just for history bookkeeping.
                        if (lastSampleAt == null || (now - lastSampleAt.Value) >= TimeSpan.FromSeconds(1))
                        {
                            samples.Add(new MappingStatisticSample { TimestampUtc = now, CpuPercent = update.CpuPercent, MemoryMb = update.MemoryMb });
                            lastSampleAt = now;
                        }

                        // Only successful runs are persisted for now — a run that throws (see LibraryManager.GetAlbums's
                        // catch blocks) never sets IsComplete, so it's silently skipped here rather than saved half-done.
                        if (update.IsComplete && update.StartedAtUtc.HasValue)
                        {
                            statisticToPersist = new MappingStatistic
                            {
                                StartedAtUtc = update.StartedAtUtc.Value,
                                CompletedAtUtc = now,
                                DurationMs = (now - update.StartedAtUtc.Value).TotalMilliseconds,
                                DirectoryCount = update.DirectoryCount,
                                MappedDirectories = update.MappedDirectories,
                                BytesBroadcast = bytesBroadcastThisRun,
                                Error = update.Error,
                                Samples = new List<MappingStatisticSample>(samples)
                            };
                        }
                    }

                    await libraryHubContext.Clients.All.SendAsync("ReceiveMappingUpdate", mappingUpdateJson);

                    if (statisticToPersist != null)
                        await MongoDbClient.Instance.InsertMappingStatisticAsync(statisticToPersist);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            };
        }
    }
}
