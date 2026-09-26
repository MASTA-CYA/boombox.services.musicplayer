# Path-matching conventions

_Category: [Persistence & storage architecture](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-25_

## What this covers

Every album/track lookup across the codebase is, at bottom, a string comparison of a filesystem path — against
a Mongo document, an in-memory list, or a dictionary key. Windows filesystems are case-insensitive but
case-preserving (`D:\Music\Song.flac` and `D:\Music\song.flac` are the same file, but whichever casing was
typed first is what gets stored/returned). This mismatch — code doing an exact case-sensitive string
comparison against a path that the filesystem itself doesn't treat as case-sensitive — is a known, partially
fixed risk across the app. This doc flags where the fix has and hasn't been applied; it is not a fix in itself.

## Where it's fixed: `trackUserData`

The one place this was identified, diagnosed, and fully fixed at both the storage and application layers is
[Track user data](../02-library-mapping/track-user-data.md): the `trackUserData` Mongo collection is created
with an explicit case-insensitive collation, and every in-memory lookup dictionary built from it
(`LibraryManager.BuildUserDataLookup`) is separately built with `StringComparer.OrdinalIgnoreCase` —
belt-and-suspenders, since a plain C# `Dictionary` doesn't inherit Mongo's collation for free. This was a
direct response to a real reported symptom: favourites appearing to silently "reset" when a path's casing
drifted between when a track was favourited and when it was later re-enumerated from disk.

A handful of other specific spots also use `StringComparison.OrdinalIgnoreCase` explicitly, where a related
symptom was independently diagnosed and fixed: `DynamicPlaylistSampleProvider`'s resampled-cache filename
matching (`IsCacheFileFor`, and the swap-matching inside `EnsureWindowResampled`) — see [Lazy resample
window](../01-playback-engine/lazy-resample-window.md).

## Where it's not: nearly everywhere else

Grepping every `string.Equals(...)` call across `MusicPlayer` that compares a path shows the overwhelming
majority use the plain two-argument overload — ordinal, case-sensitive, no comparer specified — including:

- **`MongoDbClient`**: `GetAlbumAsync(path)`, `DeleteAlbumAsync(path)`, playlist track removal matching,
  `GetLyricsAsync(trackPath)`, `GetTrackUserDataAsync(path)` (this one specifically is safe in practice only
  because the *collection* itself carries the case-insensitive collation — the C# comparison expression is
  still plain `string.Equals`, riding on Mongo's server-side collation rather than doing the insensitive match
  client-side).
- **`LibraryManager`**: matching a freshly-enumerated file against a previously-saved album's track list
  (`savedAlbum?.Tracks.Find(track => string.Equals(track.Path, file))`, repeated across the [full mapping
  pipeline](../02-library-mapping/full-mapping-pipeline.md), [single-album
  refresh](../02-library-mapping/single-album-refresh.md), and Singles grouping), matching a saved album's
  directory `Path` against the current scan directory, and the equalizer track-assignment matching in
  `GetTrackEqualizerAssignmentsAsync`.
- **`Player`**: matching the currently-playing provider's `OriginalFilePath` against a `PlaylistTrack.Path` in
  several places (`UpdatePlaybackInformation`'s per-track loop, `HandleUpdatedFavouriteTrack`,
  `SaveEqualizerPresetAsync`'s per-track preset lookup).
- **`DynamicPlaylistSampleProvider`**: most anchor/index-path matching (`AddProviders`'s anchor search,
  `ResolveAnchorIndex`) is plain `string.Equals` — only the cache-filename-specific spots noted above were
  fixed.

## Why this hasn't been fixed everywhere yet

This is explicitly a flagged-not-fixed item, not an oversight nobody noticed. It's mentioned in
`KNOWN_ISSUES.md` as an open, deliberately deferred cleanup: `trackUserData` got the case-insensitive treatment
because it had a concrete, reported symptom driving the fix (favourites appearing to reset); the rest of the
path-matching surface hasn't (yet) produced an equally concrete, reproduced bug report, so it hasn't been
prioritized ahead of other work. The practical risk is real but narrow: a mismatch only actually bites when the
*same* path is referenced with two different casings across two different code paths that then fail to match
each other — something that can happen (a user renames a folder only in casing, a path gets typed differently
somewhere), but hasn't been the dominant source of reported bugs the way the `trackUserData` case was.

## Known constraints

- No single sweep has audited every path comparison in the codebase for this issue systematically — this doc
  is based on the same targeted grep-across-the-codebase approach used elsewhere in this documentation
  initiative, not an exhaustive static analysis guarantee.
- A future fix, if undertaken, would need to decide between the two approaches already in use — Mongo-level
  collation (works automatically for every query against that collection, but only settable at collection
  creation, requiring a migration for existing collections) versus explicit `StringComparer.OrdinalIgnoreCase`
  at each in-memory comparison site (more surface area to get right, no schema migration needed) — rather than
  mixing both inconsistently the way the current partial fix does.
