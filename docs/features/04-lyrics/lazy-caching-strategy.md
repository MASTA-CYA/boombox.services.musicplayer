# Lazy per-track lyrics caching strategy

_Category: [Lyrics](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-25_

## What it does

Lyrics are resolved and cached **on demand, per track, the first time they're actually requested** — never as
part of a library mapping pass. See [Lyrics source chain](lyrics-source-chain.md) for the resolution order
itself; this doc covers why the caching is shaped the way it is.

## Why not during mapping

`LyricsManager.GetLyricsAsync` is only ever called from `PlayerHub.GetTrackLyricsAsync`, itself only ever
triggered when the user actually opens a track's lyrics dialog. It is deliberately **not** wired into
[library mapping](../02-library-mapping/full-mapping-pipeline.md) (full scan, cache load, or single-album
refresh) at all. Two independent reasons:

- **Cost per track.** A full library mapping pass already does real per-file work (`MediaInfoWrapper` tag
  reads, image extraction) across potentially thousands of tracks. Lyrics resolution adds a full tag re-parse
  (a second, different read of the file, via `TagLibSharp2` rather than `MediaInfo`) plus, when nothing embedded
  is found, a network round-trip to LRCLIB — running that for every track in the library, most of which may
  never actually be played, would make an already meaningfully-long full scan dramatically slower.
- **Respecting LRCLIB's free service.** LRCLIB is a free, community-run API with no authentication in front of
  it in this integration. Hammering it with one request per track during every full library scan — again, for
  tracks that might never be played — would be an inconsiderate way to use a free service the app doesn't pay
  for or have any special allowance with.

## Cache-forever, including failure

`GetLyricsAsync` caches the *outcome* of the source chain permanently, success or not: a resolved `Lyrics`
document (`Embedded`/`Lrclib`/`Sidecar`/`Manual` source) is saved to the `lyrics` Mongo collection (see [Storage
map](../07-persistence-storage/storage-map.md)), and so is a `NotFound` result when every source comes up
empty. On every subsequent call for that same track, the very first thing `GetLyricsAsync` does is check for an
existing cached document — if one exists (including `NotFound`), the entire source chain is skipped entirely,
no tag re-read and no LRCLIB call. This is what keeps repeat opens of the lyrics dialog for the same track
instant, and keeps a track with genuinely no lyrics available from re-triggering the same LRCLIB
lookup/tag-parse/sidecar-check every single time the dialog is reopened.

The only way to invalidate a cached result (including a `NotFound`) is the user pasting their own lyrics via
`SaveManualLyricsAsync`, which explicitly overwrites whatever was cached — there's no automatic re-check or
expiry (e.g. re-trying LRCLIB after some time has passed, in case the track was added to their catalog since).

## Known constraints

- A `NotFound` cached before a track was added to LRCLIB's catalog stays `NotFound` forever unless the user
  manually pastes lyrics — there's no periodic re-attempt.
- Because resolution is entirely on-demand, the very first open of a track's lyrics dialog can be noticeably
  slower than every subsequent one (tag parse + possible network round-trip vs. a single Mongo lookup) — this
  is expected, not a bug, and only ever happens once per track.
