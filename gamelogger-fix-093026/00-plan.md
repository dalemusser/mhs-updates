# Fixing the game's logging in the Unity project — plan and brief

**Date:** 2026-09-30
**Purpose:** the working brief for the session that fixes the logging code in the Mission HydroSci Unity project, builds and tests it, and delivers the changed files to the game team.
**Outcome (2026-09-30):** done. The fixed files are in `Game-Code/`, and `01-changes.md` has each defect's change and the verification results on the dev site (checks 1–7). Two test collections exist on the dev site (`20260930-logfix-LoggingFixTest`, `20260930-logfix2-LoggingFixTest`); nothing is active for students.

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

## Reproducing each defect (before the fix), and what to see after

Every recipe below was run on the dev site during 2026-09-18 to 2026-09-28 and gave the stated result. The old builds are still hosted there (collections MHS-Release_V1.0.0 = v2.8.1; 20260925-12446-ClassifierIssuesTesting = the new logger), so each defect can be shown on the original build before the fixed build is tried. Use the test member (memory `dev-mhs-test-member`); switch its collection on Mission HydroSci → Manage → Change version, and put it back on Default afterwards.

**The browser recipe (playwright-cli, headless Chromium).** `S` is a session name; `H` the store hash: `printf '%s' "https://<dev host>/missionhydrosci/play" | md5` for a member launch (one store per site, shared by all units and builds), or the device-test run's URL up to its last `/`.

```
pw() { playwright-cli -s=S "$@"; }
pw open https://<dev host>/login; pw fill e41 "<test member login id>"; pw click e42     # refs from `pw snapshot`
# wait until the Manage/units page shows the unit "Ready to play"
pw run-code "async page => { await page.context().route(/<log host>/, r => r.abort('connectionrefused')); return 'blocked'; }"
pw goto https://<dev host>/missionhydrosci/play/unit1
# wait until window.__mhsStepLog.entries has an entry matching /rendering/ (pw --raw eval "...")
pw click '#unity-canvas'; pw press Space; sleep 25            # loading screen → main menu
pw mousemove 1010 247; pw mousedown; pw mouseup; sleep 15     # START GAME (viewport 1280x720)
pw click '#unity-canvas'; pw press Space; sleep 8             # into the scene
for k in w a s d; do pw keydown $k; sleep 2; pw keyup $k; done   # produce events
pw requests | grep '<log host>/api' | sed -E 's/.*=> //' | sort | uniq -c    # attempts by outcome
tools/dump_store.sh S $H out.bin                              # the saved queue: format, count, empties, head
pw run-code "async page => { await page.context().unrouteAll({ behavior: 'ignoreErrors' }); return 'unrouted'; }"
```

Keep each command under a couple of minutes (a long-running command can take the sessions down with it), and leave a play page within seconds once a flood starts (`pw goto .../missionhydrosci/units`). `pw requests` resets on navigation. Server-side counts: `tools/queries/` (read-only).

**Defect 1 — the schools' build stops sending (v2.8.1).** Block the log host before launch; play into the scene. Expect: two refused attempts (the entry and its retry, about ten seconds apart), then no request at all for the rest of the session, blocked or not; the saved queue in the old format (`{"logs":[...]}`) with `{}` first and every later event behind it. Unblock and keep playing: still nothing for the rest of the session. A relaunch on the dev site now sends the backlog, because StrataHub's play page removes the `{}` before the game starts (since 2026-09-28); the game's own behaviour across launches is therefore seen in the exported store (the `{}` is there) rather than on the server. After the fix: the game keeps retrying with backoff while blocked, the store holds only real events, and the backlog arrives once after the unblock (`entries-for-account.js`: entries equal distinct).

**Defect 2 — the new logger resends and loops (12438 / 12446).** Put the test member on ClassifierIssuesTesting and launch normally with the host reachable, no block needed. Press Space at the loading screen and watch `pw requests`: about 8 accepted requests a second, each carrying the session's opening events again, until the page is left (observed: each of the two opening events accepted about 170 times in 21 s). Leave the page within 20 s. `entries-for-account.js` shows entries far above distinct. For the refusal loop, load a wedged store first (below) and launch the same build: every request refused, first for `entry 0: missing or invalid 'user_id'` then for `Batch size exceeds maximum of 100`, at about 10 a second (`refusals-ledger.js`; the body grows by exactly one device-only entry per retry). Both are reproduced offline by `../gamelogger-cache-overflow-091626/specimen/send-loop-simulation.py`. After the fix: one request per batch, each event once, and a store with a `{}` gives at most one refused request before the entry is dropped.

**Defect 3 — details overwritten.** Any build. Make events from one logging component arrive faster than they are sent: play through a dialogue quickly, or move the camera so several puzzle pieces change visibility at once; or block the host during play so a backlog forms, then unblock. Then compare each entry's details with its own key or timestamp: `overwritten-details.js` with the build's version string (the schools' build, normal play: 2.5% of dialogue-node, 2.9% of quest, 43% of puzzle-piece entries; a backlog: 96–99.9%). In an exported store the same check works on the queued entries. Position events: hold W while blocked, then read the store: every queued position event shows the same, latest position. After the fix: 0% in a burst and in a backlog, and consecutive position events differ.

**Defect 4 — the first event has no user id.** Any build: launch, press Space, look at the first log request (`pw request <n>` and its body, or `refusals-ledger.js`): the debug-menu event with `"user_id": null`, refused with 400 `MISSING_FIELD`. After the fix: that event either carries the id (stamped when `SetUserId` runs) or is dropped without a request; no 400 at session start.

**Defect 5 — two logger instances.** Hard to see from outside; the evidence is the pair of identical requests ten seconds apart in defect 1. In the Editor or a build with the console shown: log the instance id in `Awake` and count instances across the menu and gameplay scenes. After the fix: one instance, one send loop, one request per batch.

**Defect 6 — no guards.** Covered by the wedged-store and null-user-id runs above: a `{}` or an id-less entry loaded from the cache must be dropped without a request or with a single refused request, and a refusal must be followed by a pause, not an immediate retry.

**Loading a wedged store before a launch.** With the game not running (the units page), put the captured store at the member store key; then launch. The IndexedDB database `/idbfs` must already exist (launch the game once first):

```
B64=$(python3 -c "import base64;print(base64.b64encode(open('../gamelogger-cache-overflow-091626/specimen/PlayerPrefs-2026-09-28-wedged-by-v2.8.1.bin','rb').read()).decode())")
pw --raw eval "new Promise(function(resolve){ var s=atob('$B64'); var u8=new Uint8Array(s.length); for(var i=0;i<s.length;i++) u8[i]=s.charCodeAt(i); var req=indexedDB.open('/idbfs'); req.onsuccess=function(){ var db=req.result; var tx=db.transaction('FILE_DATA','readwrite'); tx.objectStore('FILE_DATA').put({timestamp:new Date(), mode:33206, contents:u8}, '/idbfs/$H/PlayerPrefs'); tx.oncomplete=function(){ db.close(); resolve('stored'); }; }; })"
```

That store holds 38 entries: `{}` first, then 37 events under the test member's id (one of them with a null id). Note that StrataHub's play page removes the `{}` before the game starts (since 2026-09-28), so to show defect 2's refusal loop on the new logger, or to test the fixed loader's own handling of a `{}`, either watch what the game does with an id-less entry instead, or test with a page that does not repair (a device-test run's store is repaired too; the repair can be observed in the step log's *Storage* line).

## Testing the fix

**Before a build:** the send loop can be exercised without Unity. `../gamelogger-cache-overflow-091626/specimen/send-loop-simulation.py` is a port of the loop with a fake server; extend it (or write a C# harness against stubs, as the original drop-in was compiled) to cover defects 2, 4 and 6. Unit-test the copy-on-enqueue with two events from one component in one frame.

**The build:** build one unit (Unit 1 is the smallest; Unit 2 is what the public device test runs) with `AdroitBuilder` in 6000.0.74f1, zip it in the developer format, upload it on the dev site's MHS Builds page ("Upload Build" creates the collection), and put the test member on that collection (Manage → Change version). Set a distinct version string so the build's entries are easy to find.

**On the dev site, with the test member** (the recipe and tools above; `tools/play-harness.py` is the Python variant; server-side counts from `tools/queries/`). The build passes when:

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
