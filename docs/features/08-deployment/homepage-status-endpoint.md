# Homepage dashboard stats tile (`/api/status`)

_Category: [Deployment & infrastructure](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-25_ ·
_Implemented 2026-08-19, see `KNOWN_ISSUES.md` #31_

## What it does

`GET /api/status` — a minimal API endpoint added directly in `MusicServer/Program.cs` — returns
`{ isRunning, cpuPercent, ramMB, ioBytesPerSec }` for the `MusicServer` process, feeding a tile on the user's
separate home-lab "Homepage" dashboard.

## Only the `MusicServer`-side half lives in this repo

This feature spans two independently-managed systems, and only one of them is in scope here:

- **In this repo**: the `/api/status` endpoint itself, described below.
- **Outside both Boombox repos entirely**: the Homepage dashboard's own configuration (`services.yaml`/
  `docker.yaml`) and a Docker socket proxy running on a separate CT (container) on the user's Proxmox setup,
  managed by whichever assistant/session handles that home-lab infrastructure — not this one. See
  `reference_boombox_homepage_dashboard` in project memory for the explicit scope boundary.

The endpoint was built by following a plan document the user supplied (written by whatever assistant manages
the Homepage/Proxmox side), implementing only the part of that plan that touches `MusicServer`.

## Implementation

`PerformanceCounter` (added as a NuGet package — no longer part of the .NET 8 shared framework) samples CPU% and
RAM for the `MusicServer` process, plus a combined I/O rate. A CPU-percent reading needs two samples separated
by a short delay to compute a rate; the endpoint uses `await Task.Delay(200)` between them rather than the
`Thread.Sleep(200)` shown in the user's supplied plan — a deliberate deviation, so the request doesn't pin a
thread-pool thread for that 200ms window. `isRunning` has no separate check of its own — the endpoint
responding *at all* is the running signal; if the process is down, the request simply can't be answered, which
Homepage's own dashboard interprets as "down" the same way it would any unreachable service.

## Known constraint: `PerformanceCounter`'s process-name matching

`PerformanceCounter` matches by the process's `ProcessName`, not its PID. A same-named process restarting under
certain patterns could theoretically cause the counter to read a stale instance rather than the current one.
This is a known, accepted gotcha rather than something actively guarded against — not expected to bite in
practice given NSSM's one-instance-at-a-time management of `MusicServer` (see [Full deployment
topology](deployment-topology.md)), but worth knowing if the CPU/RAM figures ever look implausible after a
restart.

## Related

- [Full deployment topology](deployment-topology.md) — where `MusicServer` runs and how it's reached.
- [Storage map](../07-persistence-storage/storage-map.md) — this endpoint reads live process state only, no
  database or file involved.
