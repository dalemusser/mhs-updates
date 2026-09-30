# Fixing the game's logging in the Unity project — plan and brief

**Date:** 2026-09-30
**Purpose:** the working brief for the session that fixes the logging code in the Mission HydroSci Unity project, builds and tests it, and delivers the changed files to the game team.
**Start here** (new session, working directory `/Users/dale/Documents/catchupstratahub` so its memory loads): read this file, then `../gamelogger-cache-overflow-091626/03-builds-12438-12446-test.md` (what the last builds did) and `stratahub/docs/mission-hydrosci/mhs-game-logging-issues-092826.md` (the full issue list). Memory: `mhs-gamelogger-cache-overflow`, `mhs-log-details-overwritten`, `dev-mhs-test-member`.

## The goal and the flow

Fix every known logging defect in the game's code, prove the fix with a build, and hand the changed files to the game team. They incorporate the changes, build, host the build through StrataHub's MHS Builds page, and the build is tested again there before it is made active for students. Nothing reaches students from this work directly.

## The two project copies

Both are Unity **6000.0.74f1** (`ProjectSettings/ProjectVersion.txt`). Logging code: `Assets/Scripts/Systems/Logging/` (`GameLogger.cs`, `LoggingData.cs`), identity: `Assets/Scripts/Systems/Authentication/AuthManager.cs` (calls `GameLogger.Instance.SetUserId`), per-unit build script: `Assets/Scripts/AdroitBuilder.cs` (WebGL; build reports in `Assets/BuildReports/`; the pipeline is described in `../build-automation-update-051226/`).

1. `/Users/dale/Documents/catchupstratahub/mhs2/20260921MHS 2.0/MHS 2.0` — **what students play now** (collection MHS-Release_V1.0.0, units v2.8.1, the 2026-09-14 builds). `GameLogger.cs` dated 2026-08-18: one entry per request, the whole queue saved to PlayerPrefs on every change.
2. `/Users/dale/Documents/catchupstratahub/mhs2/20260928_CH12449_MHS 2.0/MHS 2.0` — **the game team's new logger** (builds 20260921-12438 = v2.8.2 and 20260925-12446 = v2.8.3). `GameLogger.cs` dated 2026-09-25: our drop-in from `../gamelogger-cache-overflow-091626/Game-Code/` merged into their file (batches, bounded cache, backoff), with their working variables kept as class fields. `LoggingData.cs` is byte-identical in the two copies.

**Base the fix on copy 2.** It already carries the drop-in's structure and the game team's integration; the defects left in it are small and local. Diff against copy 1 first so nothing else that changed between the two is lost.

Unity on this Mac: Hub has 6000.0.40f1, 6000.0.44f1, 6000.2.13f1 and 6000.4.2f1. Install **6000.0.74f1 with WebGL Build Support** through Unity Hub to build the project as the game team does; a different editor version means a project upgrade or downgrade and is not a fair test.

## The defects, in the source (line numbers as of the copies above)

1. **The empty entry that stops the schools' build sending (copy 1, fixed in copy 2).** `GameLogger.cs` line 303 declares `Dictionary<string, object> logData` as a class field; the send loop starts every pass with `logData.Clear()` (line 324) and then `logData = logQueue.Peek()` (line 330). After a failed send the loop waits ten seconds and the next pass's `Clear()` empties the queued entry itself; `Peek()` returns the same, now-empty entry and `if (logData.Count == 0) break;` (line 334) exits. The entry stays in the queue, `{}` first, and every later pass exits the same way; the cache persists it. Copy 2's batch loop no longer does this. (StrataHub's play page removes such entries before every launch since 2026-09-28, so on v2.8.1 the damage is one session's delay.)

2. **Copy 2 resends every accepted batch and loops on a refused one.** `GameLogger.cs` line 386 declares `List<Dictionary<string, object>> toSend` as a class field; the loop adds to it every pass (line 415) and never clears it (line 408 clears the unused `logData` instead). After a 2xx, `RemoveFromQueue(toSend.Count)` (line 446) empties the queue but the list keeps its entries, so the next pass sends them again (`toSend.Count == 0` is never true). After a 400 the single-send mode adds the head to the still-full list, so a longer batch goes out and is refused again, at once, forever. Observed: about 8 requests a second from the second request of every session; about 700,000 duplicate entries in the log data from dev testing. **Fix:** `toSend` becomes a new list at the top of every pass (or `toSend.Clear()` there); remove the leftover `logData` field and its `Clear()`; also `logData["device"] = cachedDeviceInfo` (line 428) is a leftover that does nothing useful.

3. **Queued entries' details are overwritten by later events (both copies, every build since February 2026).** `LoggingData.cs`: each `ScriptableVariableEntry` and `LogEntry<T>` keeps one `logDictionary` (lines 119 and 244 area), `ExecuteLogData()` clears and refills it and passes the same object to `GameLogger.LogEvent`, which stores it as the entry's `data` (line 96 area); `SendToServer` (line 82 / copy 2 line 152) queues the caller's dictionary. While an entry waits, the next event from the same component overwrites its `data`. `ScriptableVariableEntry` also adds the variable objects, not their values (`logDictionary.Add(entry.key, entry.value)`), so the value is whatever the variable holds at sending time. The same pattern in `GameLogger.LogPlayerPositionEvent` (copy 1 lines 217–239, copy 2 lines 299–321): `playerPositionData` and `positionData` are reused fields, so queued position events all show the latest position. Measured in production: about 2.5% of dialogue-node events, 2.9% of quest events and 43% of puzzle-piece events in normal play carry a later event's details; nearly all of a backlog. **Fix:** every event gets its own dictionaries. In `LoggingData.ExecuteLogData` build a new dictionary per call and add the variables' current values (`entry.value.Value`, or whatever the Soap variable exposes), not the objects; in `LogPlayerPositionEvent` build new dictionaries per call; as a backstop, `LogEvent` (or `SendToServer`) stores a copy of `data` (`new Dictionary<string, object>(data)`, and a deep copy if nested dictionaries can be shared). The affected event types and the fields that stay reliable are listed in the issues document, §1 G2 and §2 D2.

4. **The first event of a session goes out without a user id.** The debug-menu component logs before `AuthManager` has called `SetUserId` (copy 1 `AuthManager.cs` lines 56 and 81), so `user_id` is null and the server refuses the entry; with defect 2 that also starts the refusal loop. **Fix:** in `SetUserId`, stamp the id onto queued entries that have none, and do not start sending until the id is known (or drop entries that still have none when sending).

5. **Two logger instances.** `Awake` never assigns `_instance` (only the getter does, copy 2 lines 75 and 79), the component sits on the console-manager object in both core-systems prefabs, and the getter can create a third object. With `isSendingLogs`, `queueLock` and `userId` static but the queue per instance, two live instances can each load the saved queue and send it (a second identical send ten seconds after the first, seen in every reproduction, fits this), or strand the sending flag. **Fix:** assign `_instance` in `Awake`, destroy only the duplicate component (not a shared game object), and keep one queue.

6. **Hardening, so a slip like defect 2 cannot flood the log service** (from note 03): when loading the cache and before sending, drop entries with no fields or no `user_id` and never attach the device block to one; pause about a second after a 400 or 413; remove sent entries by identity (dequeue while the head is one of the entries just sent) rather than by count; keep batches at 50 or fewer. Keep the drop-in's bounded cache, coalesced writes and backoff.

7. **Smaller things.** `IsNetworkAvailable()` (`Application.internetReachability`) does not stop attempts on WebGL; harmless, but do not rely on it. The hard-coded fallback URL and key (`logServerUrl`, `apiKey`) point at the legacy endpoint; the host page always supplies the config, so consider removing the fallback. `bundleVersion` in `ProjectSettings` is `20260323-`; the build pipeline overwrites it, and the schools' build reports `20260914-` with no build number — cosmetic, but analysts group by it. Console errors seen during headless runs of v2.8.1 (`ArgumentException: JSON must represent an object type`, `ArgumentOutOfRangeException: Index was out of range`) were not investigated; check whether they come from the logger.

## Testing the fix

**Before a build:** the send loop can be exercised without Unity. `../gamelogger-cache-overflow-091626/specimen/send-loop-simulation.py` is a port of the loop with a fake server; extend it (or write a C# harness against stubs, as the original drop-in was compiled) to cover defects 2, 4 and 6. Unit-test the copy-on-enqueue with two events from one component in one frame.

**The build:** build one unit (Unit 1 is the smallest; Unit 2 is what the public device test runs) with `AdroitBuilder` in 6000.0.74f1, zip it in the developer format, upload it on the dev site's MHS Builds page ("Upload Build" creates the collection), and put the test member on that collection (Manage → Change version). Set a distinct version string so the build's entries are easy to find.

**On the dev site, with the test member** (the playwright-cli recipe: block the log host with `page.context().route`, launch, click the canvas, Space, click START GAME, Space, hold WASD; `tools/dump_store.sh <session> <hash> <out>` reads the game's saved queue, where `<hash>` is the MD5 of the play page URL up to its last `/`; `tools/play-harness.py` is the Python variant; server-side counts come from the log service's database, read-only, per the `prod-db-readonly-query-recipe` memory). The build passes when:

1. Normal play for a minute: every event reaches the server exactly once (entries equal distinct event type + timestamp pairs for the account), and an idle game makes no requests.
2. Log host blocked for two minutes, then unblocked: the game keeps retrying with backoff, the saved queue holds only real events, the backlog arrives once after the unblock, the store empties, the request rate returns to normal.
3. A store wedged by v2.8.1 put in place before launch (`../gamelogger-cache-overflow-091626/specimen/PlayerPrefs-2026-09-28-wedged-by-v2.8.1.bin`): the first launch drains it with no refusal loop.
4. A batch with one entry lacking `user_id` (for example the session's first event): that entry is fixed or dropped once, the rest are recorded, no loop.
5. Details survive: in a burst and in a backlog, dialogue-node and quest entries' `data` match their `eventKey`, `PuzzlePieceVisibleEvent.data.timestamp` matches the entry's `timestamp`, and consecutive position events differ while the player moves.
6. Only one logger instance exists across the menu and gameplay scenes (a log line in `Awake`, or a count in the step log), and the session makes exactly one request per batch.
7. `tools/extract_meta.py` + `meta.py` can confirm what a built `.data` file contains (the metadata of the built logger: fields, methods, constants) if a build's provenance is ever in doubt.

## Delivery

Put the changed files under `Game-Code/` in this folder mirroring `Assets/Scripts/...` (as the earlier bundles do), with a short `01-changes.md`: each defect, the change, and the verification results with dates and the build's version string. The game team applies them to their mainline, builds, and uploads through MHS Builds; the same seven checks run again on their build before it is made active. Update `stratahub/docs/mission-hydrosci/mhs-game-logging-issues-092826.md` (G1–G7) as items close.

## Open points to settle early

- Install Unity 6000.0.74f1 with WebGL support (Unity Hub), and confirm `AdroitBuilder` builds one unit on this Mac (time and disk).
- How the game team wants the changes: the files, or a patch against copy 2.
- Whether copy 2's other changes (outside logging) are already in the game team's mainline; the diff between the copies says what else moved.
