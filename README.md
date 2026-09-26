# Boombox — Music Player Backend

The backend for **Boombox**, a personal self-hosted music player: gapless local playback over ASIO/WASAPI,
a real-time SignalR API for a companion web UI, live lyrics, a 9-band parametric equalizer, and a library
mapping pipeline that turns a folder of audio files into a browsable, metadata-rich collection.

This is a solo, self-hosted project built to run one person's own music library on their own hardware — not a
general-purpose product, and not accepting external contributions, but documented here in the interest of
sharing how it's built.

## Projects

| Project | Framework | Purpose |
|---|---|---|
| `MusicPlayer` | .NET Framework 4.8, C# 7.3 | The playback engine — audio pipeline, library mapping, playlists, lyrics, equalizer, persistence. A class library with no web/API concerns of its own. |
| `MusicServer` | .NET 8 | The ASP.NET Core host — five SignalR hubs (`LibraryHub`, `PlayerHub`, `PlaylistHub`, `ServerHub`, `AutoScrollHub`), background broadcast loops, and a small `/api/status` REST endpoint. References `MusicPlayer`. |
| `PlayerConsole` | .NET 8 | A console harness for exercising the playback engine directly, outside the SignalR host. |

`MusicPlayer` targets an older .NET Framework version specifically because `AsioOut`/`WasapiOut` (NAudio's
audio output wrappers) are COM-based and were originally built against that runtime; `MusicServer` and
`PlayerConsole` are current .NET 8.

## Architecture, in brief

- **Playback** runs through a custom NAudio `ISampleProvider` (`DynamicPlaylistSampleProvider`) that turns an
  entire queue of tracks into one continuous stream — gapless track transitions, a lazy on-demand resampling
  window (only a handful of tracks around the current one are ever resampled to disk at once), and a live
  9-band equalizer applied per-sample.
- **The library** is mapped from disk on demand — tag/image extraction per file, with a fast disk-cache path
  for normal startup and a full re-scan as the fallback. Display metadata (name, artist, cover art, duration)
  lives in a per-album JSON cache in AppData; only queryable state (paths, favourites, equalizer assignments)
  lives in MongoDB.
- **Real-time state** — now playing, the library, playlists, mapping progress — is pushed to connected clients
  over SignalR, using a shared broadcast-loop pattern and a plain-C#-event bridge between `MusicPlayer` and
  `MusicServer` (the two projects can't reference each other in the other direction, so `MusicPlayer` raises
  events that `MusicServer` subscribes to once at startup).
- **Persistence** spans MongoDB (queryable state), Redis (two small UI-state keys), and local AppData JSON
  files (display metadata plus a disaster-recovery backup of favourites/play-counts).

Every one of these is written up in detail, with diagrams, under [`docs/features/`](docs/features/) — see
below.

## Documentation

The [`docs/features/`](docs/features/) folder in this repo is a copy of the design documentation from the
companion [`boombox.docs`](../boombox.docs) repository, kept here so the backend's own architecture docs travel
with the code that implements them. **`boombox.docs` is the canonical source** — if the two ever diverge, that
repo is correct; this copy also carries a bug-tracking file (`KNOWN_ISSUES.md`) and a live feature checklist
that aren't duplicated here.

Docs are organized by category, each with inline Mermaid diagrams:

0. [Architecture diagrams](docs/features/00-architecture/) — a class diagram of the core domain, a system-wide
   architecture overview, an ERD of the MongoDB collections, and two end-to-end flow diagrams (startup sequence,
   a full playback request) — start here if you're orienting yourself in this codebase for the first time.
1. [Playback engine](docs/features/01-playback-engine/) — the audio pipeline, queue management, lazy
   resampling, playback modes, transport controls, output switching, equalizer, and settings persistence.
2. [Library & mapping](docs/features/02-library-mapping/) — the mapping pipeline, the fast cache-load path,
   single-album refresh, mapping statistics, and favourite/play-count tracking.
3. [Playlists](docs/features/03-playlists/) — the "Favourite" playlist and real-time sync.
4. [Lyrics](docs/features/04-lyrics/) — the embedded-tag → LRCLIB → sidecar-file lookup chain and lazy caching.
5. [Real-time sync / broadcast architecture](docs/features/05-realtime-sync/) — the SignalR hub topology, the
   broadcast-loop pattern, the event-bridge pattern, and how backend warnings become client toasts.
6. [Frontend UI shell](docs/features/06-frontend-ui-shell/) — documented here too since several of these
   (search, scroll persistence) are driven by backend hubs this repo owns.
7. [Persistence & storage architecture](docs/features/07-persistence-storage/) — the full storage map across
   MongoDB, Redis, and local disk.
8. [Deployment & infrastructure](docs/features/08-deployment/) — how this backend is actually run (Windows
   service under NSSM, bridged to the UI over Tailscale) and its remote start/stop/restart mechanism.

## Running it

This is built around one person's specific hardware and library layout (a Focusrite Scarlet Solo over ASIO, a
Corsair HS80 over WASAPI, a Windows host, MongoDB and Redis on the LAN) — see [Deployment &
infrastructure](docs/features/08-deployment/deployment-topology.md) for the full picture, including the parts
that live outside this repo entirely (NSSM service configuration, Task Scheduler jobs, Tailscale). It isn't
designed to be dropped onto arbitrary hardware without adjusting the audio driver names and connection strings
in `MusicPlayer`/`MusicServer` first.

For local development: open `MusicPlayer.sln` in Visual Studio (or `dotnet build` the individual `.csproj`
files), point `MongoDbClient`/`RedisCache` at a local MongoDB/Redis instance, and run `MusicServer` — it hosts
the SignalR hubs the [companion Angular UI](../boombox.ui.web) connects to.
