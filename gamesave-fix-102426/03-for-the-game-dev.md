# Saving fix: what to do in the game code

**Version:** 1.0 (2026-10-24).
**Short version:** replace three C# files, build, upload through MHS Builds. No prefab, asset,
scene or host-page changes, and nothing changes in the save service. The evidence and each
defect's verdict are in `01-findings.md`; this page is only what you need to apply it: the files,
the build and hand-over steps, what students will see, and then the background on what each file
fixes.

## The three files

Copy each file from `Game-Code/` over the file of the same name in the project. They were written
against the project as of 2026-09-28 (changelist 12449, Unity 6000.0.74f1) with the logging fix
v1.3 applied (`SettingsSaveManager.cs` is that bundle's file plus this change). If you have changed
any of these files since, diff before copying. All three are delivered with LF line endings; if
your copy of `SettingsSaveManager.cs` is still the CRLF original, diff ignoring line endings.

| From `Game-Code/` | To `Assets/Scripts/` | What it is now |
|---|---|---|
| `Systems/Save Load System/GameSaveManager.cs` | `Systems/Save Load System/GameSaveManager.cs` | The transport: one request per call, classified answer, timeout, no built-in host or key, nothing secret in the console. |
| `Systems/Save Load System/CustomSavers/DatabaseSaver.cs` | `Systems/Save Load System/CustomSavers/DatabaseSaver.cs` | The policy: retry for ever with a visible status, the newest save kept on the device, a failed load never starts over, capped transition wait. |
| `Systems/Save Load System/SettingsSaveManager.cs` | `Systems/Save Load System/SettingsSaveManager.cs` | Only the built-in host and key are removed and a 30 s timeout added. |

Nothing that calls these classes needs to change:

- `DatabaseSaver`: every serialized field (the prefab's references) and every public member used
  elsewhere are unchanged (`saveData`, `startNewGameIfNoExistingSaveData`, `SaveGameToDatabase`,
  `LoadGameFromDatabase`, `LoadGameFromDatabaseCoroutine`, `InitializeDataAfterSceneLoaded`,
  `CheckIfSceneShouldLoad`, `ApplyDataAfterSceneLoaded`, `ClearData`, `ResetFlag`,
  `CallToTransitionToNextUnit`, `ReadyToTransition`, the addressables region). New: a developer
  button `ClearLocalSaveCopy()` on the component, and read-only `HasUnsentSave`, `ServerAnswered`,
  `StatusText`.
- `GameSaveManager`: `SaveGame(...)` and `LoadGame(...)` keep their signatures as wrappers
  (`LoadGame` still answers `null` for "failed or none", as the sandbox loader expects); the unused
  settings variant of `LoadCoroutine` is gone. New: `SaveJson`, `LoadLatest`, `SaveConfig`,
  `LoadConfig`, the result types, and static helpers. Two public statics exist for tests
  (`requestTimeoutSeconds`, `saveConfigOverride`/`loadConfigOverride`); the game leaves them alone.

## Build and hand-over

1. Build the units as usual (release profiles) with a distinct version string, so this build's
   saves and log entries can be told apart on the server.
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the checks below on it (the recipe is in
   `tools/README.md`). The same checks passed on our Unit 1 build of these files on 2026-10-24
   (see the verification record at the end).
4. Only after that is the collection made active for students. The enforcement of the old API
   key on the services follows the activation, not the other way round.

## The check on a build

With a test member that has progress, on the dev site, the save host blocked or answered by the
browser's request interception:

1. **Normal play to a checkpoint:** one request to the state-save endpoint, answered 201, the
   toast "Saving Game..." then "Game Saved!"; the stored document's `save_data` carries
   `mhs_meta`; the service's ledger shows nothing from this build.
2. **Save host blocked at a checkpoint:** the toast area shows *Not saved yet: no connection to
   the save service. Retrying in N s (attempt K)* counting down (5, 10, 20, 40, 60 s between
   tries). Unblock: exactly one request, accepted, "Game Saved!". Reload the page while still
   blocked, then unblock: the next launch sends the kept save once and the server has it.
3. **Save host blocked before launch, with progress on the server:** START GAME shows *Can't load
   your progress yet: no connection to the save service. Retrying in N s (attempt K)*; the unit
   does **not** start over. Unblock: the game resumes from the save. On a device that made the
   last save itself the game instead resumes at once with *Using the progress saved on this
   device. Still checking the save service...*, and the line goes away when the service confirms.
4. **A refused key** (answer the save with 401): the status says *the save service refused this
   game's access key* and keeps retrying; nothing is lost.
5. **An HTML page instead of JSON** (answer the load with 200 and `<html>`): counted as a failure
   and retried; not treated as "no saves".
6. **Unit transition with the save host blocked:** the transition proceeds within about five
   seconds; the console says the save is kept for the next unit's build; the next unit sends it.
7. **The browser console carries no `Bearer` value** at any point.

## What changes for students

- Saving looks the same when the service answers: "Saving Game..." then "Game Saved!" in the same
  toast as before. The same checkpoint raised twice in a row no longer sends two requests.
- When a save cannot get through, the toast area stays up with a status line instead of the old
  "Caution: Game Save Failed..." and silence; it names the reason in plain words and counts down
  to the next attempt. The game keeps playing. The status lines are:
  - `Saving Game... (attempt K)`
  - `Not saved yet: <reason>.` / `Retrying in N s (attempt K)`
  - `Loading your progress...`
  - `Can't load your progress yet: <reason>.` / `Retrying in N s (attempt K)`
  - `Using the progress saved on this device.` / `Still checking the save service...`
  - `Newer progress found on the save service.` / `Loading it...`
  - `Saving... waiting for your sign-in`, `Saving... waiting for the save service settings`
    (only when the host page gave the game no identity or no endpoint)
  - and the reasons: *no connection to the save service*, *the save service did not answer in
    time*, *the save service had an error*, *the save service refused this game's access key*,
    *the save service asked us to wait*, *the save service answered with something unreadable*,
    *the save service address is not answering*.
  The lines are two lines of text; if the Alert Panel on the `SaveSystem` prefab is too narrow for
  them, widen it or let the text wrap (that is the one cosmetic thing to look at).
- "Caution: Game Save Failed..." remains only for a save the service rejects for good (a 400 or
  413), which is a defect to look at, not a network condition.
- Pressing START GAME while the service cannot be reached no longer restarts the unit; the student
  waits at the main menu with the countdown, or plays on from this device's copy of their last save
  while the game keeps asking the service.

## What each file fixes (background, nothing to do here)

### `GameSaveManager.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| A stalled request never came back. | No `timeout`; a save that got no answer left `saveGameInProgress` up for good and held the unit transition. | 30 s timeout; a timeout is a failure the policy retries. |
| "Failed" meant three different things. | `onLoaded(null)` for a network failure, a 4xx/5xx and an unparsable body alike; `onSaved(false)` for everything. | Every answer is classified: accepted, no saves, or failed with `retryable` (network, timeout, 401/403, 404/405, 408, 429 with `Retry-After`, 5xx) or not (400, 413, other 4xx). |
| The bearer value was printed to the console on every launch. | `Start` and `LoadCoroutine` logged the configs including `auth`. | Never. Request logging names the URL and the status only. |
| An unreadable body ended the coroutine. | `JsonUtility.FromJson` on whatever came back; an HTML page from a filter threw, so neither callback ran and the launch sat. | `TryParseLoadBody` guards it and reports "unreadable" as a retryable failure; `[]`, `null` and an empty body are "no saves". |
| The old host and key were built in. | `Domain` and `ApiKey` constants used when the host page gave no config. | Gone. With no config the request is reported as failed (retryable, "the save service is not configured") and the policy waits. |
| URLs were fields a caller could overwrite for good. | The `api` overloads set `SaveUrl`/`LoadUrl`. | The URL comes from the config per request; an explicit URL applies to that request only. |
| Request construction could throw into the caller. | A bad header value from the host page. | Built inside a guard; a failure is a retryable result. |
| Nothing identified a save beyond the server's receive time. | — | `WithMeta` adds `mhs_meta` (`seq`, `saved_at`, `session_id`, `version`, `scene`, `checkpoint`) inside `save_data`; the service stores it unchanged, readers ignore it, and `TryParseLoadBody` reads it back. |

### `DatabaseSaver.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| A failed save was lost. | One request; on failure a toast and the flag cleared. Three such saves in the service's ledger in four weeks (the page closed with the save in flight); the saves that never reached the service are invisible. | The newest save is kept (in memory and on the device) and sent until the service accepts it: backoff 5 → 60 s, `Retry-After` honoured, a status line with reason, countdown and attempt. Only a 400/413 ends a save, loudly. |
| Saves were not owned. | The request serialized the SaveSystem's one shared `SavedGameData` a frame after the record. | Serialized at record time; the record owns its bytes; at most one request in flight; a newer save replaces an older unsent one; an identical repeat is not sent again. |
| Nothing was kept for the next launch. | The PlayerPrefs storer on the prefab was only ever cleared. | `mhs_last_save` in PlayerPrefs holds the newest record with `sent`/`dropped` flags; an unsent one is found at the next launch (any scene, as soon as the player id is known), checked against the service's newest, and sent; an unsent record of another player on a shared device is moved to `mhs_pending_save_<id>` instead of being overwritten; records over 256 KB stay in memory only (today's saves are 45 KB). |
| A failed load started the unit over. | `null` from the load, and `startNewGameIfNoExistingSaveData`, ran `LoadNextGameplayScene`; the next checkpoint then overwrote the real progress on the server. Seen in production. | Only the service's `[]` starts a new game. A failure is retried (3 → 30 s) with the countdown while the student waits at START GAME. With a device copy for this player the game resumes from it meanwhile; when the service answers with newer progress that is loaded instead; the service's "no saves" discards a sent device copy (a staff reset) but keeps an unsent one. |
| Two load requests per launch. | The main menu's background load and START GAME each asked. | One shared fetch per launch; START GAME reuses the menu's answer; an accepted save updates the known newest. |
| Requests went out without an id or a config. | `user_id: ""` (1,496 refused requests until September), `mhs_developer`. | Nothing is sent without a 24-hex id (on WebGL the bridge's id is used as soon as it exists) or without the endpoint config; both are waited for with a status line. |
| The unit transition could wait for ever. | Ready only while `saveGameInProgress` was false. | Ready when nothing is unsent, or five seconds after the transition first asked; the save stays on the device and the next unit's build sends it. |
| Scaled-time waits. | `WaitForSeconds` in the toast. | Every wait is real time, so a paused game does not stall saving or loading. |
| Small guards. | `CheckIfSceneShouldLoad` indexed character 5 of any scene name; a second START during a start; a deactivated object left flags set. | Length and null checks; a second START is ignored while one runs; `OnDisable` clears the loop flags; `OnEnable` restarts the send loop if a save is unsent. |

### `SettingsSaveManager.cs`

| Issue | What the new file does |
|---|---|
| Built-in host and key as the fallback when the host page gives no settings config. | Removed; without a config the request is skipped and the caller told `false`, like the empty-id guard. |
| No timeout. | 30 s on both requests; the `Authorization` header is set only when there is a value. |

## Verification record

| Where | Before the fix | After the fix |
|---|---|---|
| Editor: the PlayMode tests in `tools/SaveFixTests.cs` (Unity 6000.0.74f1, batch mode, 2026-10-24) | — (the tests describe the new rules; on the old files they do not compile against the new API) | 16 tests pass in about 55 s: payload helpers; save retried with a countdown until accepted and no auth value in any log line; a newer save replaces an unsent one (one request once the service is back); an unsent save survives a relaunch and is sent then; an unsent save is dropped when the service already holds newer progress; a refused key is retried; a rejected payload is not; a timeout is retried; the save waits for a valid id; the transition gate opens after about five seconds with the save kept; a failed load never starts over and resumes when the service answers; `[]` starts a new game while an HTML body is a failure; the device copy is used while the service is down and left alone when it confirms; the service's newer save wins; one load request per launch; an identical repeat of a checkpoint is not sent again. |
| WebGL build of Unit 1 on the dev site | — | (filled in below once the run is done) |
