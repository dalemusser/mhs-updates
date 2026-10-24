# 01 — Findings: the game's saving, reviewed the way logging was

**Date:** 2026-10-24 (evidence gathered read-only that day; dates below are UTC unless marked)
**Scope:** the save and load path of the Mission HydroSci Unity project (`GameSaveManager.cs`,
`CustomSavers/DatabaseSaver.cs`, `SettingsSaveManager.cs` on the `SaveSystem` prefab) against the
save service, in the project copy of 2026-09-28 (changelist 12449) with the logging fix v1.3 and
the garden box fix applied. The brief is `00-plan.md`; the page for the developer applying the fix
is `03-for-the-game-dev.md`; the drop-ins are under `Game-Code/`; the queries that produced the
numbers are under `queries/` and their redacted outputs under `data/` (ids shortened to their last
six characters, hosts and addresses replaced).

## 1. Summary

Saving had the logger's defect family, and production shows the consequences:

- **A failed save was final.** One request per checkpoint, no timeout, no retry, nothing kept.
  The service's own ledger shows the saves it failed (three in the last four weeks, each with the
  page reloaded or closed while the save was in flight); the saves that never reached it
  (connection failures, filters) leave no server-side trace at all.
- **A failed load started the unit over, on top of real progress.** Confirmed in production: a
  student with a Unit 4 save at checkpoint 3 relaunched at home in the evening, started at the
  spawn point with the opening conversation and the unit's first quest, and the next checkpoint
  wrote checkpoint 0 over the server's newest (§3.5).
- **Identity and configuration were not waited for**, the bearer value was printed to the browser
  console on every launch, the response parsing was unguarded, and the old API key was still a
  built-in fallback in two files.
- **Two things the brief did not list:** every checkpoint saves twice in about 7% of cases
  (two raisers, byte-identical bodies), and old saves from before the settings decoupling of May
  are up to 8.6 MB each, which is why the save collection is 4.4 GB for 5,592 documents.

The fix (§5, `03-for-the-game-dev.md`) replaces the transport and the policy in two files and
removes the key fallback from a third. It retries saves and loads for ever with a visible status,
keeps the newest save on the device and sends it at the next launch if needed, never starts a
unit over on a failed load, waits for identity and configuration, times requests out, caps the
unit transition wait, and prints no secrets. Nothing changes in the save service in this round;
§7 lists what it could do later. Fifteen PlayMode tests against a fake save service cover each
rule (§6).

## 2. How saving works (the 2026-09-28 code)

- **Trigger.** `QuestEventValueListener.DoAutoSave` raises the Soap event `OnSaveGame` when a quest
  entry with *SaveOnActivation/Success/Failure* changes state (and may set the scene checkpoint
  number first). Scrap pickups, cutscenes and the developer menu raise it too.
- **Record.** `DatabaseSaver.SaveGameToDatabase` asks PixelCrushers' `SaveSystem.RecordSavedGameData()`
  for the state of every registered `Saver` (dialogue/quest state, positions, door locks, object
  states, the `CheckpointSaver` of the unit) and gets back the **one shared** `SavedGameData`
  object the SaveSystem keeps.
- **Send.** After one frame, `GameSaveManager.SaveCoroutine` serializes `{user_id, game: "mhs",
  save_data}` with `JsonUtility` and POSTs it with the bearer value from the host page's bridge
  config (or the built-in fallback) to the state-save endpoint. No timeout. On success a toast
  "Game Saved!"; on any failure a toast "Caution: Game Save Failed..." and nothing else. The flag
  `saveGameInProgress` goes down either way.
- **Load.** START GAME raises `OnLoadGameFromSlot` → `LoadGameFromDatabaseCoroutine` → one POST of
  `{user_id, game, limit: 1}`; the body (a JSON array, newest first, `[]` when none) is wrapped and
  parsed with `JsonUtility` without a guard. `null` from the callback (request failed **or** no
  saves) with `startNewGameIfNoExistingSaveData` (on in the prefab) runs `LoadNextGameplayScene()`:
  the unit starts fresh. A save whose scene is in this unit's build is resumed through the
  Transition scene and applied once the scene is up; a save from another unit's scene starts this
  unit fresh (by design: the next unit starts at its beginning).
- **A second load at launch.** When `AuthManager` first learns the player id it calls
  `InitializeDataAfterSceneLoaded`, which loads the newest save in the main menu and applies only
  its dialogue data. So a launch made two load requests.
- **Unit transition.** `TransitionSceneLoader` polls every `ICheckForUnitTransition`; `DatabaseSaver`
  reported ready only while `saveGameInProgress` was false, with no cap.
- **Service.** `POST /api/state/save` inserts a document and trims the student to the newest 5
  (`max_saves_per_user`); `POST /api/state/load` returns the newest by server timestamp. Refused
  and failed requests go to the service's ledger; accepted ones only to hourly counters.

## 3. Production evidence (read-only, 2026-10-24)

### 3.1 Volumes

From the service's hourly counters (`data/apistats.out`), 2026-09-20 → 10-24:

| Endpoint | Requests | Errors | Typical latency | Slowest hour (max) |
|---|---|---|---|---|
| state save | 12,588 | 5 | 17–38 ms | 5.5 s |
| state load | 12,504 | 4 | 6–19 ms | 3.1 s |
| settings load | 23,622 | 3 | 85–470 ms | 20.5 s |
| settings save | 699 | 4 | 25–955 ms | 3.1 s |

A school day makes 500–970 state saves and about as many state loads (two loads per launch, §2).
Saving is fast when it works; the problems are the requests that get no answer, and those do not
appear in any counter.

### 3.2 Refused and failed requests (the service's ledger)

The ledger keeps only requests the service answered with 4xx/5xx (`data/ledger_state.out`). All
time, state endpoints:

| Path | Status | Message | Count | Period | What it was |
|---|---|---|---|---|---|
| save | 400 | Missing required fields | 916 | 2026-02-06 → 09-04 | `user_id: ""`: saves sent before identity was known (defect 5) |
| load | 400 | Missing required fields | 580 | 01-29 → 09-04 | the same, for loads |
| save / load | 400 | user_id must be 24 hex | 29 / 113 | 05-25 → 10-03 | `mhs_developer`: the standalone page's old localhost identity (fixed 2026-10-03 in `webgl-template-identity-100326`) |
| save | 400 | Invalid JSON payload | 145 | 02-18 → 09-22 | bodies the service could not decode |
| save | 500 | context canceled | 12 | 03-12 → 10-07 | the client went away while the insert ran (§3.6) |
| load | 500 | incomplete read … context canceled | 14 | 03-25 → 04-03 | database connection drops in spring |
| save | 405 | | 15 | 03-25 | a wrong method (testing) |

The `user_id: ""` requests stop on 2026-09-04, when the StrataHub play page began handing the game
its identity before Unity starts; on another host page the same race is still possible, which is
why the fix waits for the id. The spring 500s on saves carried bodies of 1.9–2.5 MB (§3.3).

Last 28 days: three 500s on saves (2026-09-29, 09-30, 10-07, §3.6); the 401s and 400s of
2026-10-02/03 are the key-replacement probes, not students.

### 3.3 What the saves look like

5,591 Mission HydroSci saves for 1,208 students; 1,050 students sit at the retention cap of five,
so the collection shows each student's last five checkpoints only (`data/saves_sequences.out`).

Saves made by the current builds (since 2026-09-14, n = 1,970) are **42–45 KB**, of which the
dialogue/quest state (`DialogueSystemSaver`) is ~42 KB; the rest is a few hundred bytes of
positions, door locks and object states, and a 15-byte checkpoint record. No save since
2026-09-14 carries a `SettingsSaver` or character-customization record.

Older saves do: `SettingsSaver` records average 1.45 MB (max **8.6 MB**, one student's five saves of
2026-03-25) and character-customization records average 310 KB (max 2 MB), from before the
settings decoupling of May. Across all time the median save is 47 KB but p90 is 2.1 MB and the
collection is 4.4 GB of data (906 MB on disk) for 5,592 documents. The service's request-body cap
is 2 MB, so such saves would be refused today; they belong to students who stopped playing in
spring and will stay until deleted (§7).

For the device copy this means ~45 KB per save; two copies plus the logger's 512 KB cache stay
well under WebGL PlayerPrefs' 1 MB.

### 3.4 Every checkpoint saved twice, often

374 pairs of consecutive saves by the same student are **under two seconds apart** (261 students),
and 336 of them are byte-identical (same scene, checkpoint, dialogue state and size); the typical
gap is 1–70 ms (`data/saves_sequences.out`, and the newest production save of 2026-10-23 is such
a pair, 5 ms apart). Of 4,383 gaps between a student's consecutive saves, 456 are under ten
seconds. The cause is upstream of the transport: two raisers of `OnSaveGame` fire for the same
checkpoint (a quest and its entry, or two listeners), and each raise sent its own request. Two of
a student's five retained saves are then the same save. The fix sends at most one request at a
time and does not resend a save identical to the one just sent in the same launch (§5).

### 3.5 Progress going backwards

Among the retained saves, 19 students have a later save in the **same scene with a lower
checkpoint** than an earlier one, and 14 have a save in an **earlier unit** than their previous
save (`data/saves_sequences.out`). The recent cases were traced in the game's event log
(`data/recent_sizes_and_restarts.out`, `data/restart_positions.out`):

- **Started over on a failed load (defect 4).** Student …c7f22a saved Unit 4 at checkpoint 3 twice
  on 2026-10-06 (14:24 and 16:10) and relaunched at 01:27 the next day (an evening at home, build
  20261005-12466). The game's first position events are the unit's spawn point (787, −69, −640),
  not the saved position (−100, −23, −341); the opening conversation (88) plays from node 0; the
  first quest of the unit ("Well, What Have We Here?") activates at 01:30 and that checkpoint-0
  save becomes the student's newest. Nothing reached the service's ledger, so the load never got
  an answer (a connection failure or a filter): the game took "no answer" for "no save" and
  started over. The retained saves show it exactly as the brief predicted: cp 3, cp 3, then cp 0.
- **Checkpoint number rewound on load (a game-logic issue, not the transport).** Student …8b8703
  saved Unit 2 at checkpoint 4 at 14:45 on 2026-10-15 and relaunched at 14:55:57. This time the
  save **was** loaded: the player spawned, then ten seconds later stood at the saved position
  (585, 139, 1452), and the quest in progress ("Foraged Forging") was active again. But that
  re-activation raised the auto-save with the quest's own checkpoint number, so the save written
  at 14:56:13 says checkpoint **1**; the later saves say 0. The quest log was right and the
  checkpoint value wrong, and the next launch would place the player at checkpoint 1's position.
  Same build 20261005-12466, the collection whose description mentions a Unit 2 save-checkpoint
  fix; the February cases (checkpoint 11 → 8, then 13) look the same. For the game team (§8).
- **Unit went backwards (defect 12, out of scope by decision 5).** Student …c7f231 was in Unit 3's
  dungeon at 13:47 on 2026-10-06; at 13:51 their StrataHub progress record was set to unit 2; at
  13:52 they relaunched into Unit 2, which started fresh (its first quest, "Escape the Ruin",
  at 13:54), and the Unit 2 saves became their newest. Going back to Unit 3 later would start it
  fresh too, because the Unit 3 save is older. The other unit-backwards cases have gaps of days to
  months: resets and repeat students.

The garden-box crash relaunches (`data/crash_resume.out`): retention hides the pre-crash save in
14 of the 15 cases (those students saved five or more times afterwards); in the one visible case
the student resumed at the same checkpoint (6) they had saved three minutes before the crash.

### 3.6 Saves the service failed

Three saves in the last 28 days ended in `500 Failed to save data: context canceled`
(`data/failed_saves_context.out`, `data/journal_summary.out`): the client's connection closed
while the insert was running (62, 364 and 508 ms in). In the clearest case (…8b6785, 2026-09-29
17:46) the game had started 11 s before the save and started again 15 s after it: the student
reloaded the page with the save in flight. The game's answer to a 500 was the toast, and the
checkpoint was gone; the next save of each of the three is days later. Under the fix the save
stays on the device and is sent at the next launch. The same mechanism explains the 2.3 MB saves
cut off in spring.

### 3.7 The old key

The service runs the old key (the one in the game's source) in observe mode: it lets requests for
other students through and logs them. In the last 28 days it logged 87 such uses, all between
2026-10-02 and 10-07 and 74 of them on Sunday 2026-10-05 between 06:00 and 09:00 Pacific, from ten
student ids (state saves 42, settings loads 33, state loads 11), most from one network: game pages
opened before the key change and left open, or an older copy of the standalone page. None since
2026-10-07. With this fix in the schools' build a refused key becomes a visible retry with the
save kept, not a toast and a loss, so the enforcement flip (decision 6) can follow it.

### 3.8 Other things seen on the way

- The service journal shows 9,181 retention cleanups, 58 "game state deleted" (the Manage page's
  *Delete saved state*), 21 settings deletions and the three failed saves; no slow-request signal.
- The settings load averages 85–470 ms per hour with a 20 s outlier, against 6–19 ms for the state
  load. Not this round's problem; noted for the service.
- The dev site's play page points the game at the **production** save service (the service URLs
  in its bridge config), so the dev test member's saves live in production under its id; the
  test runs below used that id and were cleaned up with the Manage page's *Delete saved state*.
- Nothing the play page sends to StrataHub carries the browser console, so the bearer value the
  game printed was visible only to someone opening DevTools on a student's machine (or in a
  console capture attached to a support report by hand). It was still printed on every launch of
  every build since the bridge config exists.

## 4. The defects, one by one

Verdicts: **confirmed** (seen in the code and reproduced in the editor tests or in production
data), **cleared** (not a defect as feared), or **documented** (out of this fix's scope).

| # | Defect (from the brief) | Verdict | Where it showed | What the fix does |
|---|---|---|---|---|
| 1 | Per-request state in fields | Confirmed | `SaveSystem.RecordSavedGameData()` returns the SaveSystem's one shared `SavedGameData`; the request serialized it a frame later, so two saves in one frame serialized the same (latest) object, and any retry would have sent whatever it had become. `GameSaveManager.SaveUrl/LoadUrl` were fields that the `api` overloads overwrote for good. | The record is serialized to a string when it is made and the queued item owns it; every request's data is local to its coroutine; URLs are read from the config per request. |
| 2 | No retry, no backoff | Confirmed | Code; §3.6 (three saves cut off and lost); the client-side failures leave no server trace by definition. | Retry for ever with backoff 5 → 60 s (loads 3 → 30 s), `Retry-After` honoured, a status line with reason, countdown and attempt; one request in flight. A 400 or 413 (the payload itself rejected) is the only thing not retried: it is logged as an error and the save is kept on the device, not resent. |
| 3 | Nothing kept for later | Confirmed | Code: the `PlayerPrefsSavedGameDataStorer` on the prefab was never written to (only deleted by `ClearData` at every launch). | The newest save is kept on the device (`mhs_last_save` in PlayerPrefs, IndexedDB on WebGL) with `sent`/`dropped` flags and metadata; an unsent one is found at the next launch and sent after a check that the service holds nothing newer; an unsent save of another player on a shared device is moved aside, not overwritten. |
| 4 | Failed load treated as no save | Confirmed | Code (`onLoaded(null)` for both); production §3.5 (…c7f22a). | Only the service's explicit `[]` may start a new game. Every failure waits and retries with status; with a device copy for this player the game resumes from it meanwhile and the service's answer wins when it arrives and is newer (decisions 1 and 3). The service's "no saves" discards a stale device copy (a staff reset) but not an unsent one. |
| 5 | Identity | Confirmed | Ledger §3.2: 1,496 requests with an empty id until 2026-09-04, 142 with `mhs_developer`. | Nothing is sent without a 24-hex id; on WebGL the bridge's id is used as soon as it exists; otherwise the request waits (status "waiting for your sign-in"). |
| 6 | Timeouts and the transition gate | Confirmed | Code: no `timeout`; `CallToTransitionToNextUnit` uncapped. | 30 s request timeout (a timeout is a retryable failure); the gate waits for an unsent save at most 5 s (frame-based new-request detection as in the logger) and says so in the console. |
| 7 | Serialization and parsing guards | Confirmed | Code: `JsonUtility.FromJson` on the raw body; `ToJson` unguarded. Production: filters that answer HTML exist (the download holds), the settings variant had the `null` form (G6). | Serialization failure is an error that keeps the previous save; a 200 with an unreadable body is a retryable failure, never an exception; the response is classified (accepted / no saves / failed, retryable or not). |
| 8 | Instances and lifetime | Cleared, with a finding | The prefab is in both core-systems prefabs, but PixelCrushers' `SaveSystem.Awake` destroys the second game object, so one `DatabaseSaver` lives (DontDestroyOnLoad). The duplicate *saves* of §3.4 come from two raisers, not two instances. | One request in flight; a save identical to the one just sent in this launch is not sent again; the coroutines are on the surviving object; a deactivated object clears its loop flags. |
| 9 | Console lines with the auth value | Confirmed | `GameSaveManager.Start` and `LoadCoroutine` printed the bearer value on every launch. | Removed; the URL may be logged, the auth never (a test asserts no log line carries the test's auth value). |
| 10 | `Time.timeScale = 0` | Confirmed | The toast and the post-save pause used scaled waits. | Every wait is `WaitForSecondsRealtime`. |
| 11 | The old key fallback | Confirmed | `GameSaveManager` (host and key constants) and `SettingsSaveManager` (`_apiKey`). | Both removed: with no config the game waits and looks again (state) or skips the request and reports it to the caller (settings). The Editor-only defaults in `MHSBridge` keep the old key for the developer id, which is what the service's restricted key is for. |
| 12 | Unit vs save mismatch | Documented (decision 5) | §3.5 (…c7f231). Today: a save whose scene is not in the launched unit's build makes that unit start fresh; "Set current" never touches the save; going back to a later unit then starts it fresh too because its save is older. | Unchanged. The per-unit scene check is the same; the metadata the game now writes (`mhs_meta.scene`, checkpoint, time) would let a later change keep one newest save per unit. |

Also fixed on the way: the main menu's background load and START GAME share one load per
launch (two before); the start path ignores a second START while one is running;
`CheckIfSceneShouldLoad` no longer indexes a scene name shorter than six characters or a null one.

## 5. What the fix does (short form; the files are described in `03-for-the-game-dev.md`)

**`GameSaveManager.cs` (transport).** One request per call, 30 s timeout, URL and auth from the
bridge config on every request (no built-in host or key), the answer classified
(`Accepted` / `NoSaves` / `Failed` with `retryable`, `Retry-After`, and a student-safe reason),
response parsing guarded, request construction guarded, nothing secret in the console. New entry
points `SaveJson` and `LoadLatest`; the old `SaveGame`/`LoadGame` signatures remain as wrappers.
Pure helpers (`BuildSavePayload`, `WithMeta`, `ReadMeta`, `TryParseLoadBody`, `IsValidUserId`) are
testable without a server.

**`DatabaseSaver.cs` (policy).** Serializes at record time; keeps the newest save as a device
record with metadata; a send loop that waits for identity and config, sends at most one request at
a time, retries for ever with backoff and a countdown in the existing Alert Canvas, drops only a
payload the service rejected for good; a shared fetch of the service's newest save with retries,
used by the main menu and START GAME; a start flow that never starts over on a failure, resumes
from the device copy while the service is unreachable and switches to the service's save when that
arrives newer; the transition gate capped at five seconds; a launch-time recovery of an unsent
save with a check against the service's newest; real-time waits throughout. Every serialized field
and public member the prefab and other scripts use is unchanged.

**`SettingsSaveManager.cs`.** The built-in host and key are gone (no config → the request is
skipped and reported, as before for an empty id) and both requests have the 30 s timeout. Settings
are not progress; a failed settings request is not retried.

**Metadata the game now writes** into `save_data` as `mhs_meta` (`seq`, `saved_at`, `session_id`,
`version`, `scene`, `checkpoint`): the service stores it as it is, old builds and the
`SavedGameData` reader ignore it, the device uses it to compare its copy with the service's, and
the service could order saves by it later (§7).

## 6. Verification record

**Editor PlayMode tests** (`tools/SaveFixTests.cs`, the real `GameSaveManager` and `DatabaseSaver`
against a fake save service on localhost that can be up, down, refusing, rejecting, answering HTML
or hanging; `tools/README.md` has the two temporary project settings): 15 tests, all passing on
2026-10-24 in Unity 6000.0.74f1 (about 50 s). They cover: the payload helpers; save retried with a
countdown until accepted and no auth value in any log line; a newer save replacing an unsent one
(one request once the service is back); an unsent save surviving a relaunch and sent then; an
unsent save dropped when the service already holds newer progress; a refused key retried, not
dropped; a rejected payload (400) not retried; a timeout retried; the save waiting for a valid
player id; the transition gate opening after about five seconds with the save still kept; a
failed load that never starts over and resumes when the service answers; `[]` starting a new game
while an HTML body counts as a failure; the device copy used while the service is down and left
alone when the service confirms the same save; the service's newer save winning over the device
copy; one load request per launch shared by the menu and START GAME.

**Dev site, Unit 1 build of the fixed files:** see `03-for-the-game-dev.md` § "The check on a
build" for the recipe and the record of the run.

## 7. Proposals for the save service (not in this round; decision 4)

1. **Order by the client's time when it is there.** The game now sends `save_data.mhs_meta.saved_at`
   (device clock, UTC) and `seq`. On save, the service could keep its append-only history but
   mark a save whose `saved_at` is older than the student's newest stored `saved_at` as
   `superseded`, and the load could skip superseded saves. That closes the remaining way for a
   late retry (an unsent save from a device that comes back after the student played elsewhere)
   to become the newest. Old clients send no `mhs_meta`; for them nothing changes. The game
   already protects against this on its side (an unsent save is checked against the service's
   newest before it is sent), so the service change is belt and braces.
2. **A size ceiling on `save_data`** (1 MB would be twenty times today's saves) answered with 413,
   which the game treats as final and logs, so a regression like the 8.6 MB settings records of
   March cannot fill the collection again. The HTTP body cap of 2 MB already refuses larger ones.
3. **Housekeeping of the legacy saves:** the documents with `SettingsSaver` or
   character-customization records (none made since 2026-05) are 4.1 GB of the 4.4 GB. Deleting
   those records from the documents, or the documents themselves for students who have not played
   since spring, is safe for the game (current saves never contain them).
4. **Expose `Retry-After`** (and `Access-Control-Expose-Headers: Retry-After`) if a rate limit is
   ever added; the game honours it on 429.
5. **The settings load's latency** (§3.8) deserves a look at the query plan; the state load on the
   same cluster is 20 times faster.

## 8. For the game team, outside this fix

- **The checkpoint number rewinds on load** (§3.5, …8b8703): re-activating the saved quest on load
  raises the auto-save with that quest's `AutoCheckpointOnActivation`, which is lower than the
  checkpoint saved later in the same quest. The quest log is restored correctly; only the
  checkpoint number (and so the position the next launch starts at) goes back. Seen in build
  20261005-12466 and in February's data. A guard that does not lower the scene checkpoint when the
  activation comes from `ApplyData` would fix it.
- **Two saves per checkpoint** (§3.4): two raisers of `OnSaveGame` for the same checkpoint. The
  fix de-duplicates identical consecutive saves within a launch, so this no longer doubles the
  traffic or eats two of the five retained saves, but the second raise is still wasted work.
- **Unit vs save** (§3.5, decision 5): a save holds one scene; StrataHub's "Set current" to an
  earlier unit makes that unit's first checkpoint the newest save, and the later unit then starts
  fresh when the student returns to it. If that matters, keep one newest save per unit on the
  service (the metadata now carries `scene` and `checkpoint`) and load the one for the launched
  unit.

## 9. Open questions

- Whether to show the retry status in the Alert Canvas at all in the main menu, or only in the
  gameplay scenes (today the canvas is on the prefab and visible in both; the fix uses it in both).
- The wording of the status lines (student-facing; `03-for-the-game-dev.md` lists them).
- Whether the service change of §7.1 is wanted before or after the enforcement flip (decision 6
  says the game fix ships first; the flip does not depend on §7.1).
