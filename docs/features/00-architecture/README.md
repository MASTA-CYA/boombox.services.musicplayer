# Architecture diagrams

_Last verified against code: 2026-09-26_

Unlike the other categories, these docs don't document one feature each — they cut across all of them, showing
how the pieces fit together as a whole. Read these first if you're orienting yourself in the codebase for the
first time; read the numbered feature categories after, for the detail behind any one box or arrow here.

1. [Class diagram — MusicPlayer core domain](class-diagram.md) — the singletons (`Player`, `LibraryManager`,
   `LyricsManager`), the audio pipeline classes, and how they relate to the two persistence gateways.
2. [System architecture overview](system-architecture.md) — the full picture: three .NET projects, the Angular
   UI, MongoDB/Redis, and the physical/network topology (Windows host, Proxmox/Docker, Tailscale) they run on.
3. [ERD — MongoDB collections](mongodb-erd.md) — the seven collections and the string-matched (not
   foreign-keyed) relationships between them.
4. [Flow diagram — MusicServer startup sequence](startup-sequence.md) — from the bootstrap logger through the
   trackUserData migration to the server accepting its first connection.
5. [Flow diagram — end-to-end playback request](playback-request-flow.md) — tracing a single "click Play" from
   the Angular UI through the STA thread hop, the gapless audio pipeline, and back out via the broadcast loop.
