using MusicPlayer.LyricsManagement.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MusicPlayer.LyricsManagement
{
    // Parses raw LRC-format text into a flat, time-ordered list of LyricsLine - shared by every lyrics source
    // (an LRCLIB response's syncedLyrics, a .lrc sidecar file's contents, or a manual paste in the lyrics
    // dialog), since all three are the same format. Falls back to treating the input as plain unsynced text -
    // one LyricsLine per non-empty source line, TimestampMs left null - when no bracketed timestamps are found
    // anywhere in it, so the same entry point covers a manual plain-text paste too without the caller needing
    // to guess which kind of input it has first.
    public static class LrcParser
    {
        // [mm:ss.xx], [mm:ss.xxx], or [hh:mm:ss.xx] - covers every variant seen in the wild. A line can carry
        // more than one timestamp tag (LRC allows repeating a line at several playback points, e.g. a chorus)
        // - handled by matching all of them, not just the first, and emitting one LyricsLine per tag.
        private static readonly Regex TimestampTagPattern = new Regex(@"\[(?:(\d{1,2}):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

        // LRC metadata directives ([ar:Artist], [ti:Title], [al:Album], [length:03:45], [offset:500], [by:...],
        // etc.) - not lyric content, so dropped entirely rather than leaking into the displayed lyrics as a
        // literal line. Matched separately from TimestampTagPattern since these use a non-numeric key.
        private static readonly Regex MetadataTagPattern = new Regex(@"^\[[a-zA-Z]+:[^\]]*\]$", RegexOptions.Compiled);

        public static List<LyricsLine> Parse(string rawText)
        {
            var lines = new List<LyricsLine>();
            if (string.IsNullOrWhiteSpace(rawText)) return lines;

            var sourceLines = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var hasAnyTimestamp = false;

            foreach (var sourceLine in sourceLines)
            {
                var trimmedLine = sourceLine.Trim();

                if (string.IsNullOrWhiteSpace(trimmedLine) || MetadataTagPattern.IsMatch(trimmedLine)) continue;

                var matches = TimestampTagPattern.Matches(trimmedLine);

                if (matches.Count == 0)
                {
                    lines.Add(new LyricsLine { TimestampMs = null, Text = trimmedLine });
                    continue;
                }

                hasAnyTimestamp = true;
                var text = TimestampTagPattern.Replace(trimmedLine, string.Empty).Trim();

                foreach (Match match in matches)
                    lines.Add(new LyricsLine { TimestampMs = ToMilliseconds(match), Text = text });
            }

            if (!hasAnyTimestamp) return lines;

            // Once any timestamp exists anywhere in the input, treat the whole thing as synced - drop any
            // empty-text synced lines (a bare timing tag with nothing following it, seen in some LRC files as
            // instrumental-break markers) and sort by timestamp, since tags don't always appear in the source
            // file in playback order (repeated chorus lines especially).
            return lines.Where(line => line.TimestampMs == null || !string.IsNullOrWhiteSpace(line.Text))
                .OrderBy(line => line.TimestampMs ?? 0)
                .ToList();
        }

        private static int ToMilliseconds(Match match)
        {
            var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
            var minutes = int.Parse(match.Groups[2].Value);
            var seconds = int.Parse(match.Groups[3].Value);
            var fraction = match.Groups[4].Success ? match.Groups[4].Value : "0";

            // Fraction can be 1-3 digits (".1" = 100ms, ".12" = 120ms, ".123" = 123ms) - pad to 3 digits before
            // parsing so a 1- or 2-digit fraction isn't misread as already being in milliseconds.
            var fractionMs = int.Parse(fraction.PadRight(3, '0'));

            return ((hours * 3600 + minutes * 60 + seconds) * 1000) + fractionMs;
        }
    }
}
