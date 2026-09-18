# GameLogger Cache Overflow — 2026-09-16

This drop fixes the Mission HydroSci logger so an unreachable log server can
no longer break local log caching, and so a broken log path no longer costs
a whole session's worth of events.

## TL;DR

- A Windows classroom machine started printing
  `Failed to save cached logs (WebGL): Could not store preference value`
  on every game event. That is Unity's `PlayerPrefsException`: the game
  stores its **entire** unsent-log queue as one PlayerPrefs string, and
  Unity caps WebGL PlayerPrefs at **1 MB**.
- The queue got that large because the send loop retries the same entry
  forever on anything except HTTP 400. One POST that cannot succeed (a
  blocked host, a wedged entry) stops everything behind it, and the cache
  grows until it hits the cap. After that nothing new is persisted.
- The fix bounds the cache (oldest entries dropped first, with the gap
  reported to the server), stops the loop wedging on one entry, keeps
  cached entries small, coalesces cache writes, and drains backlogs in
  batches.

## What's in this folder

| Path | Purpose |
|------|---------|
| `01-gamelogger-cache-overflow.md` | The full write-up: symptom, mechanism, why one machine, how to confirm, the immediate remedy for an affected machine, the five required changes with rationale, server-side facts, verification. **Start here.** |
| `02-reproduction-and-specimen.md` | **Added 2026-09-18.** The failure reproduced on purpose in two minutes (block the log host at launch), the captured PlayerPrefs store, and the observed mechanism: an emptied dictionary at the head of the queue makes the send loop exit forever. Names the two additions the drop-in still needs. |
| `specimen/` | The captured store (`PlayerPrefs-…bin`, Unity's own file), its cache decoded to JSON, and the browser's request list from the reproduction. |
| `Game-Code/Systems/Logging/GameLogger.cs` | Drop-in replacement for `Assets/Scripts/Systems/Logging/GameLogger.cs`. Tree mirrors `Assets/Scripts/Systems/` as in the 2026-05-26 drop. Predates `02-…`; see its "What this changes in the fix" for the two additions still to make. |

## Where to start

1. Read `01-gamelogger-cache-overflow.md`. The "What is happening" section
   is the explanation; "Required changes" is the work.
2. Copy `Game-Code/Systems/Logging/GameLogger.cs` into place. It is the
   only file that changes. No prefab, asset, or host-page edits, and the
   public API of `GameLogger` is unchanged.
3. Run the four verification steps at the end of the doc. Step B (play
   offline, then reconnect) is the one that reproduces the original bug.

## Reference snapshot

There is **no new project snapshot** for this drop. The drop-in file was
written against `GameLogger.cs` as shipped in
`mhsbridge-userid-cleanup-052626/` (the version currently in the mainline)
and was **not built inside the Unity project**. It was compiled with the
Roslyn C# compiler (C# 9) against minimal stubs of the Unity, Newtonsoft,
Soap and MHSBridge APIs it uses, for both the `UNITY_WEBGL` and standalone
defines, with no errors. That catches syntax and type-usage mistakes, not
runtime behaviour, so the in-project build and the verification steps in
the doc are on the applying developer.

## For teachers / support (no code needed)

If a machine is already showing the error, clear site data for the game's
origin in that browser profile (padlock → Site settings → Delete data).
Progress and settings are server-side and are not affected; the game's
assets re-download once. If the school network blocks
`log.adroit.games`, the cache will fill again until that is fixed. Details
in the doc under "How to confirm" and "Immediate remedy".
