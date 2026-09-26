# Track user data consolidation

_Category: [Persistence & storage architecture](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-25_

## See the canonical write-up

The full design doc for this feature — the single-source-of-truth `trackUserData` collection, atomic
upsert/increment writes, the case-insensitive collation, the one-time migration off the old three-way split,
and the read-path overlay that keeps cache reads consistent — lives in [Library & mapping: Track user
data](../02-library-mapping/track-user-data.md). It's filed there rather than duplicated here because the
consolidation is inseparable from *how* mapping reads/writes it (`BuildUserDataLookup`, the [mapping-time bulk
lookups](../02-library-mapping/bulk-lookups.md) it's part of); this entry exists purely so the persistence
category's checklist item points somewhere concrete.

## What it replaced, briefly

Before `trackUserData` existed, favourite/times-played state lived in three places at once — embedded in each
`Album` document's `Tracks[]`, separately in the "Favourite" playlist, and in the per-album AppData JSON cache
— written by hand in three separate steps with nothing keeping them consistent. See [Track user
data](../02-library-mapping/track-user-data.md) for the full before/after, and [The "Favourite" playlist's dual
role](../03-playlists/favourite-playlist.md) for what the playlist's role became afterward (a real playlist,
kept in sync, but no longer an input to display state).

## Related, but separate

- [trackUserData disaster-recovery backup](track-user-data-backup.md) — the AppData JSON safety net for this
  collection specifically, covered as its own checklist item since it's a distinct mechanism (a local file, not
  a Mongo consolidation).
- [Storage map](storage-map.md) — where `trackUserData` sits among every other Mongo collection, Redis key, and
  cache file in the app.
