# Logging fix: what to do in the game code

**Version:** 1.2 (2026-10-01). Version 1.0 was the fix itself, 1.1 added the changes from two independent reviews, 1.2 added fields the log service can use later. All three are in the same three files; apply the files as they are.
**Short version:** replace three C# files, build, upload through MHS Builds. No prefab, asset, scene or host-page changes. The detailed write-up with the evidence and test results is `01-changes.md`; this page is only what you need to apply it: the files, the build and hand-over steps, one CI fix, and then the background on what each file fixes.

## The three files

Copy each file from `Game-Code/` over the file of the same name in the project. They were written against the project as of 2026-09-28 (changelist 12449, Unity 6000.0.74f1; the logger of builds 20260921-12438 and 20260925-12446). If you have changed any of these files since, diff before copying.

`SettingsSaveManager.cs` is delivered with LF line endings and the project's copy is CRLF, so a plain diff shows every line; diff ignoring line endings (or let your VCS normalise them) and the only change is the guard around `JsonUtility.FromJson` in `LoadSettingsCoroutine`. The other two files have the same line endings as the project's.

| From `Game-Code/` | To `Assets/Scripts/` |
|---|---|
| `Systems/Logging/GameLogger.cs` | `Systems/Logging/GameLogger.cs` |
| `Systems/Logging/LoggingData.cs` | `Systems/Logging/LoggingData.cs` |
| `Systems/Save Load System/SettingsSaveManager.cs` | `Systems/Save Load System/SettingsSaveManager.cs` |

The public API of `GameLogger` is unchanged (`Instance`, `LogEvent`, `SendToServer`, `SetUserId`, the eight `Log…Event` wrappers, `IsSendingLogs`, the `ICheckForUnitTransition` members), so nothing that calls it needs to change.

## Build and hand-over

1. Build the units as usual (release profiles). Set `bundleVersion` in the release build profile to a distinct string (date plus build number, as for 20260925-12446) so this build's entries can be told apart on the server; our test builds used `20260930-logfix`.
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the seven checks from `00-plan.md` on it (normal play, blocked log host, a store wedged by v2.8.1, the first event, details in a backlog, one instance, the built metadata). The same checks passed on our Unit 1 builds of these files on 2026-09-30 and 2026-10-01 (version 1.2 as unit1 v2.8.7 on the dev site).
4. Only after that is the collection made active for students.

## One thing to fix in CI

A build from a clean checkout fails to compile: `Assets/Imported/Samples/StarterAssets/InputSystem/PlayerInputs.inputactions` has *Generate C# Class* on, with the wrapper path `Assets/Third Party/StarterAssets/InputSystem/PlayerInputs.cs`. That folder does not exist in a fresh checkout, so the first import generates a second `PlayerInputs` class next to the checked-in one (`Assets/Imported/Samples/StarterAssets/InputSystem/PlayerInputs.cs`). Either turn generation off for that asset or point the path at the checked-in file. Machines that have already imported the project never hit this.

## What each file fixes (background, nothing to do here)

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

Added in version 1.1, from two independent reviews of version 1.0:

| Issue | What was wrong | What the new file does |
|---|---|---|
| Sending stalls while the game is paused. | The send loop's waits used scaled time; the game sets `Time.timeScale = 0` in its pause and unfocused states, so a backoff or pause in progress never elapsed until the player unpaused. | Every wait in the send loop is `WaitForSecondsRealtime` (verified by timing in headless runs, not under an actual pause). |
| An entry that cannot be serialized would end the send loop and throw into the component that logged it. | Request-body and cache serialization were not guarded (no current caller triggers this; it was the rule "nothing unsendable goes out" applied to serialization). | Both are wrapped; the offending entry is found by serializing candidates one by one, dropped with a warning, and the loop carries on. |
| The first batch waited for `AuthManager`'s identity fetch. | The host page gives `MHSBridge` the user id before the game starts, but the logger only learned it from `AuthManager` a few seconds later. | While an entry waits for its id, the loop asks `MHSBridge` for it (WebGL builds only; the Editor and standalone keep `AuthManager` as the source). |
| Small guards. | — | No logger is created during application teardown; the sending flag is cleared if the object is deactivated; cached entries load ahead of anything already queued; the batch size grows back after a success once a 413 halved it; the transition gate treats a gap of over a second as a new request. |

Added in version 1.2 (nothing to do; the log service stores unknown top-level fields as they are):

| Field or behaviour | Why |
|---|---|
| `session_id` (one per launch), `seq` (per-session counter) and `entry_id` (`session_id:seq`) on every entry | Lets the server de-duplicate, see gaps and order entries independently of the device clock, and gives analysts a hard key per entry, without another game build. |
| `recovered: true` on entries loaded from the saved cache | Tells a live event from one sent after a reload or an outage. Such entries keep the id and sequence of the session that logged them (entries cached by an earlier build get the flag but have no ids). |
| `sent_at` on every sent copy (not persisted) | Measures a device's clock skew against the server's received time. |
| A `429` with `Retry-After` is honoured as a pause of that length (1 to 300 s), otherwise the normal backoff | Lets a future server-side rate limit set the pace. |

Also kept from the September drop-in: the bounded cache (512 KB / 1,500 entries, oldest dropped first, with a `LogCacheOverflow` event reporting the gap), coalesced cache writes, and backoff from 10 s to 60 s.

### `LoggingData.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| Queued events' details are overwritten by later events from the same component (about 2.5% of dialogue-node events, 43% of puzzle-piece events in normal play; nearly all of a backlog), in every build since February 2026. | Each entry kept one `logDictionary`, cleared and refilled it on every event, and handed the same object to the logger, which stored the reference. The variable entries also stored the Soap variable objects, not their values. | `ExecuteLogData` builds a new dictionary per call, and the variable entries add the variable's current `Value`. As a backstop, `GameLogger.LogEvent` now copies the data it is given. |
| Version 1.1 tidy-ups. | — | The two variable-entry `eventKey` paths use the variables' values; `Vector3VariableEntry` checks the logger for null and passes a zero vector for an unassigned variable; `Vector3` values on the generic paths are written as `{x, y, z}` objects and `Vector2` values as `{x, y}`, the shape the position and map wrappers already use. |

### `SettingsSaveManager.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| `ArgumentException: JSON must represent an object type.` in the browser console on every scene load for players with no saved settings. | The save service answers the settings load with `200` and the body `null` when there are no settings; `JsonUtility.FromJson` throws on `null`. The coroutine died at that line, so the "No settings found, using defaults" branch and the callback never ran. | An empty or `null` body counts as "no settings" (the existing branch). Any other body `JsonUtility` cannot parse is reported to the debug console and counts as a failed load. Players with saved settings are unaffected. |
