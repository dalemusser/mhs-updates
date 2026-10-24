# 00 — The brief: the game's saving, reviewed the way logging was

**Date:** 2026-10-24
**Purpose:** the working brief for the session that reviews the save code of the Mission HydroSci
Unity project, fixes what is wrong, proves it on the dev site, and hands the changed files to the
game team. Line numbers are as of the 2026-09-28 project copy and approximate (the files are short;
search for the identifiers).

## 1. Why, and what is already known

The logging fix of 2026-09-30 (`../gamelogger-fix-093026/`) found seven defects in the game's
logger. Reading the save code alongside it (the bridge retrospective of 2026-10-02) showed that
saving shares the design and has none of the hardening the logger now has. What was noted then,
from reading the code and not yet reproduced:

- **A failed save is final.** `DatabaseSaver.SaveGameToDatabase` makes one request through
  `GameSaveManager.SaveGame`; on failure the callback shows the toast "Caution: Game Save Failed..."
  and clears `saveGameInProgress`. Nothing retries, nothing is kept for later. The progress since
  the previous checkpoint is lost when the page is reloaded.
- **A failed load starts the unit over.** `DatabaseSaver.LoadGameFromDatabaseCoroutine` calls
  `LoadGame(playerId, "mhs", 1, …)`; `GameSaveManager.LoadCoroutine` answers any request failure
  with `onLoaded(null)`, and the saver's `else` branch ("No saves found or error occurred.") then
  runs `LoadNextGameplayScene()` when `startNewGameIfNoExistingSaveData` is true, which it is in
  `SaveSystem.prefab`. A refused or timed-out load at launch looks like a new student. The next
  checkpoint then saves the fresh start on top of the real progress (the service keeps the last N
  saves per student, N from `max_saves_per_user` in its config, so the real one may still be
  recoverable server-side for a while).
- **No request timeout.** Neither coroutine sets `UnityWebRequest.timeout`; a stalled request
  never calls back. For a save that means `saveGameInProgress` stays true.
- **The unit transition waits on the save flag with no cap.** `DatabaseSaver.CallToTransitionToNextUnit`
  (`ICheckForUnitTransition`) reports ready only while `saveGameInProgress` is false. The logger's
  gate got a five-second cap in version 1.0.
- **The auth header is printed to the browser console.** `GameSaveManager.Start` (≈ l. 111) and
  `LoadCoroutine` (≈ l. 226) `Debug.Log` the save and load configs including the bearer value.
  Check what captures the console (the play page's crash reporter and step log, DevTools captures
  attached to problem reports) before deciding how far that went.
- **Unguarded JSON parsing of the responses.** `LoadCoroutine` runs `JsonUtility.FromJson<GameSaveList>`
  on the body; a non-JSON body (an HTML error page from a proxy or a school's scanning filter, the
  same filters that hold the last 12 MB of downloads) throws inside the coroutine, so neither
  branch of the callback runs and the launch sits wherever it was. The settings variant had the
  `null`-body form of this (G6 of the logging issues list), fixed in `SettingsSaveManager.cs` in
  the logger bundle; the state variant was not touched.
- **One save stream per student, launches per unit.** Saves are keyed `(user_id, game "mhs")`
  with the scene name inside the save data; StrataHub's "Set current" (Manage page, since
  2026-09) changes the student's unit without touching the save. What the game does when the
  launched unit and the saved scene disagree is to be established (`CheckIfSceneShouldLoad`,
  `IsSceneInBuildSettings` → `LoadNextGameplayScene`).
- **The old API key.** The save manager still carries the old key as a fallback (the Editor and
  localhost defaults); the services treat that key as restricted, in observe mode, with the
  enforce flip pending (StrataHub project memory `mhs-service-keys-2026-10`). Once enforced, a
  game tab opened before the key change has its saves refused, and with the current code that is
  a toast and a silent loss. The retry design must make that flip safe.

Also on record: `SaveLoadManager.cs` is dead code (all commented out); `settingsapi`'s load answers
`null` for a player without settings while `saveapi`'s load answers `{"saves": []}` (the two
services' contracts differ, and the game's two managers differ accordingly).

## 2. The code map

Game (`Assets/Scripts/Systems/Save Load System/`, all dated before 2026-09-28 except the settings
manager, which carries the logger bundle's guard of 2026-10-01):

| File | Role |
|---|---|
| `GameSaveManager.cs` (286 lines) | The transport. `SaveUrl = "/api/state/save"`, `LoadUrl = "/api/state/load"`, URLs and auth from `MHSBridge.Instance.GetStateSaveConfig()` / `GetStateLoadConfig()` in `Start`. `SaveCoroutine` (≈ l. 142): one POST of a `SavePayload` (`user_id`, `game`, `save_data`) built with `JsonUtility`, bearer header, `onSaved(success, text)`. `LoadCoroutine` (≈ l. 206): one POST of `{user_id, game, limit}`, parses `GameSaveList`, `onLoaded(saves)` or `onLoaded(null)`. Settings variants at ≈ l. 249. |
| `CustomSavers/DatabaseSaver.cs` (390 lines) | The policy, on the `SaveSystem` prefab. Listens to the Soap events `OnSaveGame` / `OnLoadGameFromSlot`; `SaveGameToDatabase` (≈ l. 114: `if (!autoSave) return`, flag up, `SaveSystem.RecordSavedGameData()`, "Saving Game..." toast, one `SaveGame`); `LoadGameFromDatabaseCoroutine` (≈ l. 235: the load, the scene check, the start-over branch; `#if UNITY_EDITOR` the flag is re-derived from `AuthManager.playerOverrideId`); `ClearData`; `CallToTransitionToNextUnit` (≈ l. 338). `startNewGameIfNoExistingSaveData` serialized, `1` in the prefab. |
| `SettingsSaveManager.cs` (286 lines) | Audio, controls, customization through `/api/settings/save` and `/api/settings/load`; already has the `null`-body guard. |
| `CustomSavers/*Saver.cs` | PixelCrushers `Saver` components that record pieces of state into the save: `CheckpointSaver` (scene checkpoint, fires checkpoint events on apply), `DoorLockSaver`, `CustomWaypointSaver`, `PuzzleSaver`, `MultiPositionSaver`, `MaterialColorSaver`, `LightColorIntensitySaver`, `SettingsSaver` (decoupled from the save system by the May 2026 bundle). |
| `Quest Listener/QuestEventValueListener.cs` | Where saves are triggered: raises the `_SaveGameEvent` Soap event at auto-checkpoints (≈ l. 530–548), driven by quest entry changes. |
| `Quest Checkpoint Navigator/` | `SimpleCheckpoints`, `CalcFurthestCheckpoint`, `CheckpointUI`, `BeyondFirstCheckpoint`: checkpoint bookkeeping and the developer navigation buttons. |
| `Assets/Prefabs/_Core/SaveSystem/SaveSystem.prefab` | PixelCrushers `SaveSystem` (`saveCurrentScene: 1`, `framesToWaitBeforeApplyData: 0`), `PlayerPrefsSavedGameDataStorer`, `JsonDataSerializer`, `DatabaseSaver`, `GameSaveManager`, `CharacterCustomization_DataController`, `TransitionSceneLoader`, and the Alert Canvas the toasts use. |
| `Assets/Scripts/Systems/Logging/GameLogger.cs` (fixed, v1.3) | The pattern to reuse: per-send state local to the loop, retry with backoff (`WaitForSecondsRealtime`), a bounded persisted queue, identity wait, 30 s request timeout, a capped transition gate, serialization guards, one instance. |

Service (`stratasave`, Go, deployed on the save host; same DocumentDB cluster as the other services):

| Where | What |
|---|---|
| `internal/app/features/saveapi/handler.go` | `POST /api/state/save` `{user_id (24 hex), game, save_data}` → 201, then async cleanup to `max_saves_per_user`; `POST /api/state/load` `{user_id, game, limit (default 1)}` → `{"saves": [...]}` newest first, `[]` when none; `POST /api/state/delete` (admin key only). Legacy `/save`, `/load` kept. |
| `internal/app/features/settingsapi/` | `/api/settings/save` and `/load` (the `null` body for no settings). |
| `internal/app/features/ledger/` | Middleware on the API routes that records refused and failed requests (`ledger_entries`: time, path, status, error class and message, a body preview). The first place to look for failed saves. |
| `internal/app/bootstrap/routes.go` ≈ l. 290–325 | The wiring, API keys (`api_key`, `api_keys_extra`, the restricted old key, the delete key). |
| `docs/api-documentation.md`, `docs/database-schema.md` | The contracts and collections. |

StrataHub side (no change expected; context): the play page hands the game the save and load URLs
and auth through the bridge config (`MHSBridge.GetStateSaveConfig`); the Manage page's "Delete
saved state" / "Delete saved settings" call stratasave's delete with the server-only admin key
(`missionhydrosci/savedata.go`); "Set current" changes `mhs_user_progress` only;
`docs/single_launch_design.md` describes the intended launch-and-resume behaviour (the game
restores the position within the unit from the save; the next unit starts fresh).

## 3. The defects to check, one by one (the logger's list applied to saving)

For each: confirm or clear it in the code, then in the editor or on the dev site, then in the
production evidence. Write the result into `01-findings.md`.

1. **Per-request state in fields.** Does any save or load coroutine keep its working data in class
   fields that a second call can overwrite (the logger's `logData` / `toSend`)? Two saves close
   together (two auto-checkpoints in one frame, a save during a scene transition), a load during a
   save.
2. **No retry, no backoff.** What a save does on each outcome: network error, timeout, 4xx, 5xx,
   non-JSON body. The expectation (Dale's rule): keep retrying for ever with visible status
   (reason, countdown, attempt), never a dead end; a button is only "retry now".
3. **Nothing kept for later.** A save that cannot be sent now is lost. The logger keeps a bounded
   queue in PlayerPrefs; the save system already has `PlayerPrefsSavedGameDataStorer` on the
   prefab: is a local slot written at all, and is it used when the server cannot be reached at
   launch? What ordering rule protects a newer save from an older retried one (a sequence number
   or the client timestamp, checked by the service)?
4. **Failed load treated as no save.** Separate "the service says there is no save" (an empty
   `saves` array) from "the request failed" (network, timeout, 401/403 from a refused key, 5xx,
   unparsable body). Only the first may start the unit over; the second must wait and retry with
   status, because starting over then overwrites real progress at the next checkpoint.
5. **Identity.** The first save or load before `AuthManager` has set `playerId` (the May bundle's
   issue A for settings): is there a wait, or does the request go out with an empty id and get a
   400? The logger now waits for the id and asks the bridge for it on WebGL.
6. **Timeouts and the transition gate.** No `UnityWebRequest.timeout`; `CallToTransitionToNextUnit`
   with no cap. The logger uses 30 s and a five-second gate.
7. **Serialization and parsing guards.** `JsonUtility.ToJson` of the save payload (size, nested
   types, anything that cannot serialize) and `FromJson` of the responses. What is the typical and
   the largest save in production (DocumentDB's 16 MB document limit; the school filters'
   behaviour on large uploads)?
8. **Instances and lifetime.** One `SaveSystem` prefab instance across the menu and gameplay
   scenes? A save in flight across a scene load (`WaitForEndOfFrame`, coroutine on a destroyed
   object)? `OnSaveGame` subscribed twice?
9. **The console lines with the auth value.** Remove them; check where console output goes.
10. **Behaviour under `Time.timeScale = 0`** (pause, unfocused): the logger's waits had to become
    `WaitForSecondsRealtime`.
11. **The old key fallback** in the save manager: remove (the host page always supplies the config;
    the standalone page asks StrataHub for it), as the logger did.
12. **Unit vs save mismatch.** What happens when StrataHub sets a unit current and the save holds a
    scene of another unit; what the game does on the first launch of a new unit (the save has the
    previous unit's last scene). Decide with Dale whether this is in scope.

## 4. Evidence to gather first (read-only, before touching code)

Use the read-only recipe in the StrataHub project memory (`prod-db-readonly-query-recipe`: ssh with
the key to the log host, `mongosh` with the URI from the local file, scripts under `/tmp`, redact
the URI in anything you keep). The save host's clock runs on Pacific time for `journalctl --since`;
use relative times.

- **Refused and failed saves and loads:** stratasave's `ledger_entries` (status, error class,
  path) over the last four weeks, by day and by school if the ids map (the Members Report CSV is the
  crosswalk; never put names or hosts in the repo).
- **Saves per student:** count, sizes, timestamps, scene names in the save data; how many students
  have more than one save and what the sequence of scene names looks like (a unit starting over
  after progress is the signature of defect 4).
- **Lost progress after crashes and reloads:** pair stratalog `gameStart` events with the last
  save before them (how far back a student lands after the Unit 4 crash of the garden box package,
  for example), and `questActiveEvent`s that repeat a unit's opening after a later one had fired.
- **The service journal** on the save host (`-u stratasave`): request errors, slow requests,
  payload-size refusals, the restricted-key observe lines (who still uses the old key).
- **The week review of 2026-10-16 → 23** (`~/Desktop/mhs-reports-102326/`, outside the repo) and
  the Unit 2 "save checkpoints" issue the game team fixed in `20261005-12466` (the dev
  collection's description): what it was, whether anything of it remains.
- **Deployed config:** `max_saves_per_user` and the timeouts in `stratasave_update/config.toml`
  (print only those keys, never the keys or secrets).

Keep the raw outputs under `data/` with ids shortened and hosts replaced, as the garden box
package did, and the scripts under `queries/`.

## 5. Reproduce on the dev site

Test member, collections and the browser recipe: `../gamelogger-fix-093026/00-plan.md`
§"Reproducing each defect" and the StrataHub memory `dev-mhs-test-member`. For saving:

- Block the save host before launch (`page.context().route(/<save host>/, r => r.abort('connectionrefused'))`)
  and launch a unit the member has progress in: does the unit start over (defect 4)? Fulfil the
  load with an HTML body or a 500 instead (`route.fulfill`) for defect 7.
- Block the host during play, reach an auto-checkpoint: the toast, then unblock: does anything
  resend (defects 2, 3)? Reload: where does the student land?
- Between runs, the Manage page's "Delete saved state" clears the member's saves on the service;
  "Set current" moves the unit without touching the save (defect 12).
- The step log (`window.__mhsStepLog.entries`) and `playwright-cli requests` show the launch and
  the save-service calls; the ledger shows what the service refused.

A Unit 1 test build from the release profile takes about 2.5 minutes with the warm Library
(`Assets/Editor/LogFixTestBuild.cs`; the pipeline's post-process stamps the version string as
`yyyyMMdd-<changelist>`, `0000` without a changelist argument). Upload through MHS Builds on the dev
site, put the member on the collection, test, put the member back on Default and Unit 1.

## 6. Rules the fix must follow

- **Never give up:** retry failed saves and loads for ever with backoff and a visible status
  (reason, countdown, attempt); a button is only "retry now" (StrataHub memory `never-give-up-retries`).
- **Students never see errors they did not cause**, and must not lose progress to a transient
  refusal: a failed load never starts a unit over; a failed save is kept and sent later.
- **The game must run without StrataHub** (standalone page, another host): keep the game's own
  transport; take URLs and auth from the bridge config as now; no new dependency on the play page.
- **Minimal, now-or-never, forward-compatible:** the game team applies files to their mainline;
  additive fields and server-tolerant behaviour (the service keeps unknown top-level fields) are
  fine; changes to the service go in a separate, coordinated step and must accept old clients.
- **Reuse the logger's proven pieces** (`GameLogger.cs` v1.3): the realtime waits, the identity
  wait, the request timeout, the capped transition gate, the serialization guards, the bounded
  persisted queue, one instance.
- **Review before shipping:** two independent reviews of the drop-ins worked well for the logger.
- **Keep the public repo clean:** placeholders for hosts, no school names, no keys (the old key is
  in tracked files already; do not add any new value, and prefer removing the fallback).

## 7. Testing the fix

Editor PlayMode tests against the game code are possible with the two temporary project settings
described in `../unit4-gardenbox-crash-102326/tools/README.md`; a fake save service can be a local
HTTP listener or the `UnityWebRequest` seams. Then a Unit 1 build on the dev site, the checks of §5
repeated: a blocked host at launch does not start the unit over and the game resumes when the host
returns; a blocked host at a checkpoint keeps the save and sends it once after the unblock; a
timed-out save does not hold the unit transition beyond the cap; the ledger shows no refusals from
the new build; a refused key (simulate with a wrong bearer through `route.fulfill` 401) ends in a
visible retry state, not a toast and silence; the console carries no auth value.

## 8. Delivery

The repository's pattern: `03-for-the-game-dev.md` (which files, what each fixes, how to apply,
how to hand the build back, actions first and background last), `Game-Code/Systems/Save Load System/…`
drop-ins mirroring `Assets/Scripts/`, any prefab edit with the exact object path, `tools/` with
the queries, tests and browser helpers, `01-findings.md` with the evidence and each defect's
outcome. If `stratasave` changes (sequence numbers, `Retry-After`, a sanity check on payloads),
that is a StrataHub-side commit with its own note here. Add the folder to `../README.md` and
`../ai/context.md` (already listed as an investigation; update the status line when the fix exists).

## 9. Open questions for Dale (ask early)

1. When the load fails at launch: wait and retry for ever with status (never start over), or
   start over after some number of attempts? The first is the rule; confirm it holds here.
2. Should the game keep a local copy of the last save (IndexedDB through PlayerPrefs) so a student
   can continue through an outage and the save is sent when the host returns, and if so how a
   conflict between the local and the server save is resolved?
3. Is the service expected to protect against an old retried save overwriting a newer one (a
   client sequence number or timestamp check), and may the service change in this round?
4. Is the unit-vs-save mismatch (StrataHub "Set current", a new unit's first launch) in scope now?
5. The restricted-key enforce flip waits on a safe retry path in the game; is the order "ship the
   save fix, then flip" acceptable, and what is the deadline?
