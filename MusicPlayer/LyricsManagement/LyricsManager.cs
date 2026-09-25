using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MusicPlayer.Common;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LyricsManagement.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TagLibSharp2.Id3.Id3v2.Frames;
using TagLibSharp2.Mp4;
using TagLibSharp2.Mpeg;
using TagLibSharp2.Xiph;

namespace MusicPlayer.LyricsManagement
{
    // Fetch order: cached Mongo document (including a cached NotFound) -> embedded file tag -> LRCLIB ->
    // .lrc sidecar file -> NotFound. Deliberately lazy/on-demand - GetLyricsAsync is only ever called from
    // PlayerHub when the user actually opens a track's lyrics dialog, never from library mapping. Mapping a
    // large library while also doing a network round-trip (LRCLIB) plus a full tag re-parse per track would be
    // painfully slow and would hammer LRCLIB's free service for tracks that might never even be played -
    // instead, each track's lyrics get resolved (and cached forever after, success or not) the first time
    // they're actually needed.
    public sealed class LyricsManager
    {
        private static readonly ILogger _logger = AppLogger.CreateLogger<LyricsManager>();

        #region Singleton
        private static readonly Lazy<LyricsManager> _instance = new Lazy<LyricsManager>(() => new LyricsManager());
        public static LyricsManager Instance { get => _instance.Value; }

        private LyricsManager() { }

        #endregion Singleton

        public async Task<Lyrics> GetLyricsAsync(string trackPath)
        {
            var cached = await MongoDbClient.Instance.GetLyricsAsync(trackPath);
            if (cached != null) return cached;

            var lyrics = await FetchLyricsAsync(trackPath);
            await MongoDbClient.Instance.SaveLyricsAsync(lyrics);
            return lyrics;
        }

        // The manual-paste fallback from the lyrics dialog, for whenever none of the automated sources turn
        // anything up (or turn up something wrong/incomplete) - LrcParser handles both a real LRC paste and
        // plain unformatted text through the same entry point, so this doesn't need to know which one it got.
        // Overwrites any existing document for the track, including a prior NotFound - deliberately, since the
        // user pasting something in is a stronger signal than whatever was cached before.
        public async Task<Lyrics> SaveManualLyricsAsync(string trackPath, string rawText)
        {
            // Reuse the existing document's Id when one's already cached (e.g. overwriting a prior NotFound) -
            // SaveLyricsAsync's ReplaceOneAsync matches by TrackPath, but Mongo still rejects a replacement that
            // changes an existing document's immutable _id, so a fresh GenerateNewId() here would only be safe
            // for a brand new track.
            var existing = await MongoDbClient.Instance.GetLyricsAsync(trackPath);

            var lyrics = new Lyrics
            {
                Id = existing?.Id ?? ObjectId.GenerateNewId(),
                TrackPath = trackPath,
                Lines = LrcParser.Parse(rawText),
                Source = LyricsSource.Manual,
                FetchedAtUtc = DateTime.UtcNow,
            };

            await MongoDbClient.Instance.SaveLyricsAsync(lyrics);
            return lyrics;
        }

        private async Task<Lyrics> FetchLyricsAsync(string trackPath)
        {
            try
            {
                var embeddedLines = await ReadEmbeddedLyricsAsync(trackPath);
                if (embeddedLines != null && embeddedLines.Count > 0)
                    return BuildLyrics(trackPath, embeddedLines, LyricsSource.Embedded);

                var lrclibLines = await FetchLrclibLinesAsync(trackPath);
                if (lrclibLines != null && lrclibLines.Count > 0)
                    return BuildLyrics(trackPath, lrclibLines, LyricsSource.Lrclib);

                var sidecarLines = ReadSidecarLyrics(trackPath);
                if (sidecarLines != null && sidecarLines.Count > 0)
                    return BuildLyrics(trackPath, sidecarLines, LyricsSource.Sidecar);

                return BuildLyrics(trackPath, new List<LyricsLine>(), LyricsSource.NotFound);
            }
            catch (Exception ex)
            {
                // A failure anywhere in this chain (a corrupt tag, an unreachable LRCLIB, a permissions error
                // on the sidecar file) shouldn't leave GetLyricsAsync throwing out to the hub - caching a
                // NotFound here means the user sees "no lyrics found" (with the option to paste their own)
                // instead of an error, and isn't stuck re-triggering the same failure every time they open the
                // dialog for this track.
                _logger.LogError(ex, "Unable to fetch lyrics for {TrackPath}", trackPath);
                return BuildLyrics(trackPath, new List<LyricsLine>(), LyricsSource.NotFound);
            }
        }

        private async Task<List<LyricsLine>> FetchLrclibLinesAsync(string trackPath)
        {
            var trackInfo = LibraryManager.Instance.GetTrackInformation(trackPath);
            var result = await LrclibClient.GetAsync(trackInfo.Name, trackInfo.Artist, trackInfo.Album, trackInfo.TotalDuration);

            if (result == null) return null;

            // Prefer synced when LRCLIB has it; their plainLyrics is still worth taking over nothing at all
            // when only that's available.
            var lrcText = !string.IsNullOrWhiteSpace(result.SyncedLyrics) ? result.SyncedLyrics : result.PlainLyrics;
            return string.IsNullOrWhiteSpace(lrcText) ? null : LrcParser.Parse(lrcText);
        }

        // Only ever called on a cache miss (see GetLyricsAsync), so there's never an existing document for this
        // TrackPath to collide with - a fresh Id is always correct here, unlike SaveManualLyricsAsync above.
        private static Lyrics BuildLyrics(string trackPath, List<LyricsLine> lines, LyricsSource source)
            => new Lyrics { Id = ObjectId.GenerateNewId(), TrackPath = trackPath, Lines = lines, Source = source, FetchedAtUtc = DateTime.UtcNow };

        // Best-effort - in practice only MP3 (via the ID3v2 SYLT frame) carries real per-line timing through
        // TagLibSharp2; FLAC/M4A can only ever surface plain unsynced text this way (no synced-lyrics
        // convention exists for Vorbis comments or MP4 atoms in this library). Checked first anyway since it's
        // free - no network call - whenever it's actually present.
        private static async Task<List<LyricsLine>> ReadEmbeddedLyricsAsync(string trackPath)
        {
            var extension = Path.GetExtension(trackPath)?.ToLowerInvariant();

            switch (extension)
            {
                case ".mp3":
                {
                    var result = await Mp3File.ReadFromFileAsync(trackPath, null, CancellationToken.None);
                    if (!result.IsSuccess || result.File == null) return null;

                    // Only Milliseconds-format SYLT frames are usable directly - MpegFrames-format timestamps
                    // would need the exact frame rate the encoder used to convert to real time, which isn't
                    // recoverable reliably after the fact, and Milliseconds is what virtually every modern
                    // tagger writes anyway.
                    var syncFrame = result.File.Id3v2Tag?.SyncLyricsFrames?
                        .FirstOrDefault(frame => frame.ContentType == SyncLyricsType.Lyrics && frame.TimestampFormat == TimestampFormat.Milliseconds);

                    if (syncFrame != null && syncFrame.SyncItems.Any())
                        return syncFrame.SyncItems
                            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                            .Select(item => new LyricsLine { TimestampMs = (int)item.Timestamp, Text = item.Text })
                            .OrderBy(line => line.TimestampMs)
                            .ToList();

                    return ToPlainLines(result.File.Tag?.Lyrics);
                }
                case ".flac":
                {
                    var result = await FlacFile.ReadFromFileAsync(trackPath, null, CancellationToken.None);
                    return result.IsSuccess ? ToPlainLines(result.File.Tag?.Lyrics) : null;
                }
                case ".m4a":
                case ".mp4":
                {
                    var result = await Mp4File.ReadFromFileAsync(trackPath, null, CancellationToken.None);
                    return result.IsSuccess ? ToPlainLines(result.File.Tag?.Lyrics) : null;
                }
                default:
                    // Every other supported audio format (WAV, AIFF, Ogg Vorbis/Opus, WavPack, etc.) either has
                    // no meaningful embedded-lyrics convention or isn't worth a dedicated branch here - LRCLIB
                    // and the .lrc sidecar are still tried for these regardless.
                    return null;
            }
        }

        private static List<LyricsLine> ToPlainLines(string rawLyrics)
            => string.IsNullOrWhiteSpace(rawLyrics) ? null : LrcParser.Parse(rawLyrics);

        // Same-name .lrc file next to the track (e.g. "Song.flac" -> "Song.lrc") - the standard sidecar
        // convention every LRC-aware player/tagger already follows, so lyrics sourced manually (or left over
        // from another player) get picked up with no extra work.
        private static List<LyricsLine> ReadSidecarLyrics(string trackPath)
        {
            var sidecarPath = Path.ChangeExtension(trackPath, ".lrc");
            if (!File.Exists(sidecarPath)) return null;

            try
            {
                return LrcParser.Parse(File.ReadAllText(sidecarPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read lyrics sidecar file {SidecarPath}", sidecarPath);
                return null;
            }
        }
    }
}
