# Logging fix: what to do in the game code

**Date:** 2026-09-30
**Short version:** replace three C# files, build, upload through MHS Builds. No prefab, asset, scene or host-page changes. The detailed write-up with the evidence and test results is `01-changes.md`; this page is only what you need to apply it.

## The three files

Copy each file from `Game-Code/` over the file of the same name in the project. They were written against the project as of 2026-09-28 (the logger of builds 20260921-12438 and 20260925-12446). If you have changed any of these files since, diff before copying.

| From `Game-Code/` | To `Assets/Scripts/` |
|---|---|
| `Systems/Logging/GameLogger.cs` | `Systems/Logging/GameLogger.cs` |
| `Systems/Logging/LoggingData.cs` | `Systems/Logging/LoggingData.cs` |
| `Systems/Save Load System/SettingsSaveManager.cs` | `Systems/Save Load System/SettingsSaveManager.cs` |

The public API of `GameLogger` is unchanged (`Instance`, `LogEvent`, `SendToServer`, `SetUserId`, the eight `Log…Event` wrappers, `IsSendingLogs`, the `ICheckForUnitTransition` members), so nothing that calls it needs to change.

## What each file fixes

### `GameLogger.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| The schools' build (v2.8.1) stops logging for good after one request fails twice. | `logData` was a class field; `logData.Clear()` at the top of each pass emptied the queued entry itself, and the loop read the empty entry as an empty queue and exited. The cache kept the `{}` so every later launch did the same. | All per-send state is local to the send loop. The loop only exits when the queue is empty. |
| Builds 12438 and 12446 resend every accepted batch about 8 times a second, and loop forever on a refused one. | `toSend` was a class field that was never cleared. | `toSend` is a new list every pass. Sent entries are removed from the queue by identity (dequeue while the head is one of the entries just sent), not by count. |
| The first event of every session is refused (`user_id: null`). | The debug-menu component logs before `AuthManager` has called `SetUserId`. | `SetUserId` stamps the id onto queued entries that have none. The loop does not send an entry that lacks only its id while the id is unknown; it waits, then sends it with the id. |
| Two logger instances. | `Awake` never assigned `_instance`; the duplicate check destroyed a game object shared with other singletons. | `Awake` assigns `_instance`; a second component removes only itself (`Destroy(this)`). `isSendingLogs` is an instance field. |
| Nothing stopped a bad entry from flooding the server. | — | An entry with no fields, no `eventType` or no `user_id` is dropped when the cache is loaded and again before sending (the `{}` left by v2.8.1 never goes out). One-second pause after a 400 or 413. 30-second request timeout. Batches stay at 50 or fewer. |
| A unit transition could wait on the logger forever. | `CallToTransitionToNextUnit` reported not ready while sending; with retry-forever that is as long as the log host is unreachable. | Ready when the queue is empty, when no send is running, or five seconds after the transition first asked. The queue is persisted and the next unit's build sends it. |
| The hard-coded fallback to the legacy `/logs` URL and API key. | It pointed at the old endpoint and could never work without a user id anyway. | Removed. The endpoint comes from the host page through `MHSBridge`; if it is missing the loop waits and looks again every five seconds. |
| Queued position events all show the latest position. | `LogPlayerPositionEvent` reused two dictionary fields. | New dictionaries on every call. |

Also kept from the September drop-in: the bounded cache (512 KB / 1,500 entries, oldest dropped first, with a `LogCacheOverflow` event reporting the gap), coalesced cache writes, and backoff from 10 s to 60 s.

### `LoggingData.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| Queued events' details are overwritten by later events from the same component (about 2.5% of dialogue-node events, 43% of puzzle-piece events in normal play; nearly all of a backlog), in every build since February 2026. | Each entry kept one `logDictionary`, cleared and refilled it on every event, and handed the same object to the logger, which stored the reference. The variable entries also stored the Soap variable objects, not their values. | `ExecuteLogData` builds a new dictionary per call, and the variable entries add the variable's current `Value`. As a backstop, `GameLogger.LogEvent` now copies the data it is given. |

### `SettingsSaveManager.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| `ArgumentException: JSON must represent an object type.` in the browser console on every scene load for players with no saved settings. | The save service answers the settings load with `200` and the body `null` when there are no settings; `JsonUtility.FromJson` throws on `null`. The coroutine died at that line, so the "No settings found, using defaults" branch and the callback never ran. | An empty or `null` body counts as "no settings" (the existing branch). Any other body `JsonUtility` cannot parse is reported to the debug console and counts as a failed load. Players with saved settings are unaffected. |

## Build and hand-over

1. Build the units as usual (release profiles). Set the version string as you normally do.
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the seven checks from `00-plan.md` on it (normal play, blocked log host, a store wedged by v2.8.1, the first event, details in a backlog, one instance, the built metadata). The same checks passed on our Unit 1 build of these files on 2026-09-30.
4. Only after that is the collection made active for students.

## One thing to fix in CI

A build from a clean checkout fails to compile: `Assets/Imported/Samples/StarterAssets/InputSystem/PlayerInputs.inputactions` has *Generate C# Class* on, with the wrapper path `Assets/Third Party/StarterAssets/InputSystem/PlayerInputs.cs`. That folder does not exist in a fresh checkout, so the first import generates a second `PlayerInputs` class next to the checked-in one (`Assets/Imported/Samples/StarterAssets/InputSystem/PlayerInputs.cs`). Either turn generation off for that asset or point the path at the checked-in file. Machines that have already imported the project never hit this.
