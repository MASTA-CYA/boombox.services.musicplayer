# Backend documentation

This folder is a copy of the `features/` documentation from the companion `boombox.docs` repository, kept
alongside the code it describes. **`boombox.docs` is canonical**; if this copy and that repo ever disagree,
trust `boombox.docs`, and check there for the project's `KNOWN_ISSUES.md` (open bug backlog) and
`DOCUMENTATION_CHECKLIST.md` (the full feature inventory this folder is a subset of).

## Categories

0. [Architecture diagrams](features/00-architecture/) — class diagram, system overview, MongoDB ERD, and two
   end-to-end flow diagrams (startup, playback request)
1. [Playback engine](features/01-playback-engine/)
2. [Library & mapping](features/02-library-mapping/)
3. [Playlists](features/03-playlists/)
4. [Lyrics](features/04-lyrics/)
5. [Real-time sync / broadcast architecture](features/05-realtime-sync/)
6. [Frontend UI shell](features/06-frontend-ui-shell/)
7. [Persistence & storage architecture](features/07-persistence-storage/)
8. [Deployment & infrastructure](features/08-deployment/)

Every doc includes inline Mermaid diagrams where a diagram helps, and is written against a specific point in
the codebase's history (each doc's header states the date it was last verified against the code) — treat it as
a snapshot explanation, not a guarantee of the current state if significant time has passed.
