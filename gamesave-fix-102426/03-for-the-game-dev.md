# Saving fix: what to do in the game code

**What this bundle changes:**

- **Saving and loading.** A failed save was lost, a failed load started the unit over, and more
  (the evidence is in `01-findings.md`). Saves and loads are now retried with a visible status,
  the newest save is kept on the device, and only the service's explicit "no saves" starts a new
  game.
- **The logger's unit-transition gate.** The v1.3 logger could hold a unit transition for ever
  while the log host was unreachable; one line fixes it.
- **The service key.** The old key is removed from the game's source (`MHSBridge.cs` Editor
  defaults) and from both page templates (localhost defaults); a developer config file kept
  outside version control replaces it. Two dead files that still carry the key are deleted.
- **The alert panel's fade** runs on real time, so a status line set while the game is paused is
  visible.

Applying it is: replace eight files, delete two dead ones, set up one developer config file,
build, upload. No prefab, scene or asset changes; nothing changes in the save service or on the
StrataHub page.

## Part 1: what to do

### Step 1. Replace eight files

`Game-Code/` in this bundle mirrors `Assets/` in the project. Each file below goes over the file
of the same path.

**Diff first.** The files were written against the project as of 2026-09-28 (changelist 12449,
Unity 6000.0.74f1) with the logging fix v1.3 applied. Before overwriting a file, diff ours against
yours; if your copy has changed since then, carry your change into ours (or tell us) rather than
losing it. Ours use LF line endings; if your copy is CRLF (`SettingsSaveManager.cs` was), diff
ignoring line endings.

| File (same path under `Game-Code/` and `Assets/`) | What changed |
|---|---|
| `Scripts/Systems/Save Load System/GameSaveManager.cs` | Rewritten: the request transport. |
| `Scripts/Systems/Save Load System/CustomSavers/DatabaseSaver.cs` | Rewritten: the save/load policy. |
| `Scripts/Systems/Save Load System/SettingsSaveManager.cs` | Built-in host and key removed; 30 s timeout. |
| `Scripts/Systems/Logging/GameLogger.cs` | The logging fix v1.3 file with one change (the unit-transition gate's timing rule). Required with the others; see Part 3. |
| `Scripts/Helpers/UI/UICanvasGroupFadeInOut.cs` | One line: the fade runs on real time. |
| `Sandbox/Brian/MHS Bridge Testing/MHSBridge.cs` | Editor defaults read a developer config file instead of carrying a key. |
| `WebGLTemplates/MHS Bridge - iPad Test/index.html` | Localhost defaults read the same developer config file instead of carrying a key. |
| `WebGLTemplates/MHS Bridge/index.html` | Same change as the other template. |

Nothing that calls these classes needs to change: every serialized field the prefabs reference and
every public member other scripts use is still there (details in Part 3).

### Step 2. Delete two dead files

- `Assets/Scripts/Systems/Logging/LogManager.cs` (the logger before `GameLogger`; unused, but it
  holds the old service key as a constant and is compiled into every build).
- `Assets/Scripts/Systems/Save Load System/SaveLoadManager.cs` (entirely commented out).

### Step 3. Set up the developer config file (once per developer)

The game's source no longer carries a service key. The Editor and a page served from `localhost`
read identity and service settings from a file instead.

1. Copy `mhs-bridge-dev-config.example.json` from this bundle to the project root (next to
   `Assets/`) as `mhs-bridge-dev-config.json`.
2. Fill in the service URLs and the current developer key. Dale provides both; they are not in
   this repository, which is public. Keep the developer identity in the file, or put a player id
   you want to test with.
3. Keep the file out of version control (`.p4ignore` or the equivalent). Each developer keeps
   their own copy.
4. To test a local build on `localhost`, put a copy of the same file next to the built
   `index.html`.

Without the file nothing hangs: the Editor (or the localhost page) runs with the developer
identity and no services, and START GAME plays a new game without saving after about five seconds,
saying so on screen.

### Step 4. Build and hand over

1. Build the units as usual (release profiles) with a distinct version string, so this build's
   saves and log entries can be told apart on the server.
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the checks listed in Part 3 on it; the same checks passed
   on our own Unit 1 builds of these files.
4. Only after that is the collection made active for students.

That is all there is to do to apply the fix. Part 2 covers the Editor tests, for your own checks; Part 3 is reference material.

---

# Part 2: running the Editor tests

The fix comes with 55 PlayMode tests in `tools/SaveFixTests.cs`. They run the real
`GameSaveManager` and `DatabaseSaver` against a fake save service on your own machine, so they test
every rule of the fix without the dev site. You don't need them to apply the fix. They are there
if you want to check the files in your project, or check later changes to the save code. Full
instructions are in `tools/README.md`. In short:

1. In the Unity Editor, open **Window → General → Test Runner**, and from its **⋮** menu choose
   **Enable playmode tests for all assemblies** (Unity restarts).
2. Put `SaveFixTests.cs` anywhere under `Assets/` that is not inside an assembly definition.
3. If it compiles, run the tests from the Test Runner's PlayMode tab.
4. If it does not compile, and the Console shows *The type or namespace name 'UnityTest' could not
   be found*, set up `csc.rsp` as described next, then run the tests.

## Setting up csc.rsp

**Where it goes.** Create a plain text file named exactly `csc.rsp` directly in the project's
`Assets` folder, at `Assets/csc.rsp`, next to the top-level folders such as `Scripts`. It must be
at the root of `Assets`, not in a subfolder.

**How Unity uses it.** When Unity compiles the project's default assemblies (`Assembly-CSharp`,
`Assembly-CSharp-Editor` and their `firstpass` versions), it looks for `Assets/csc.rsp` and passes
each line to the C# compiler as an extra option. Nothing else needs to be configured: the file's
name and place are how Unity finds it. (A `csc.rsp` next to an `.asmdef` file applies to that
assembly instead; this project's game code has no assembly definitions, so the root file is the
one that matters.)

**What goes in it.** One line: `-r:` followed by the full path to the Test Framework's compiled
assembly inside your project's `Library` folder, in double quotes because the path may contain
spaces. For example:

```
-r:"/Users/yourname/Projects/MHS 2.0/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"
```

On Windows, forward slashes work too:

```
-r:"C:/Users/yourname/Projects/MHS 2.0/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"
```

To get your project's full path, copy it from the file manager: on macOS, right-click the project
folder in Finder while holding Option and choose *Copy … as Pathname*; on Windows, Shift-right-click
it in Explorer and choose *Copy as path*. Then add `/Library/ScriptAssemblies/UnityEngine.TestRunner.dll`.
The file exists once Unity has compiled the Test Framework package, which it does on its own (the
package is part of every new project; if `Library/ScriptAssemblies` has no such file, install
*Test Framework* from the Package Manager first).

**Making Unity pick it up.** Unity does not always recompile when only `csc.rsp` changes. To force
it, right-click any script in the Project window and choose *Reimport*. When it has worked, the
`UnityTest` error is gone from the Console and the Test Runner's PlayMode tab lists
`MHS.SaveFixTests` with 55 tests.

**Removing it.** Delete `Assets/csc.rsp` and the `Assets/csc.rsp.meta` file Unity created for it.

**Why `csc.rsp` may be needed at all.** The tests use the game's own classes. Those compile into Unity's
default `Assembly-CSharp`, because the game code has no assembly definitions. A test assembly
(`.asmdef`) cannot reference `Assembly-CSharp`, so the tests have to compile inside it. The setting
in step 1 gives `Assembly-CSharp` NUnit, but in Unity 6000.0 with Test Framework 1.3.9 it did not
add `UnityEngine.TestRunner`, where `[UnityTest]` lives. The Editor has no other way to add that
reference, and `csc.rsp` passes it to the compiler. A newer Test Framework may add it by itself.

## After testing

**Before a release build**, remove the test file, `csc.rsp` and its `.meta` file, and turn the
setting in step 1 back off. Don't commit `csc.rsp` to the shared project: its path is specific to one machine.

**How long it takes.** A full run takes about four and a half minutes. That is expected: many tests
simulate the save service being down and wait through the game's real retry delays (5 seconds,
10 seconds and so on). You can also run the tests from a terminal without opening the Editor,
using the command in `tools/README.md`.

---

# Part 3: supporting information (nothing to do here)

## Compatibility notes

- `DatabaseSaver`: every serialized field (the prefab's references) and every public member used
  elsewhere are unchanged (`saveData`, `startNewGameIfNoExistingSaveData`, `SaveGameToDatabase`,
  `LoadGameFromDatabase`, `LoadGameFromDatabaseCoroutine`, `InitializeDataAfterSceneLoaded`,
  `CheckIfSceneShouldLoad`, `ApplyDataAfterSceneLoaded`, `ClearData`, `ResetFlag`,
  `CallToTransitionToNextUnit`, `ReadyToTransition`, the addressables region). New: a developer
  button `ClearLocalSaveCopy()` on the component, and read-only `HasUnsentSave`, `ServerAnswered`,
  `StatusText`, `NewGamesStarted`, `IsDuplicate`, `PlayingWithoutService`.
- `GameSaveManager`: `SaveGame(...)` and `LoadGame(...)` keep their signatures as wrappers
  (`LoadGame` still answers `null` for "failed or none", as the sandbox loader expects); the unused
  settings variant of `LoadCoroutine` is gone. New: `SaveJson`, `LoadLatest`, `SaveConfig`,
  `LoadConfig`, the result types, and static helpers. Public statics exist for tests
  (`requestTimeoutSeconds`, `saveConfigOverride`/`loadConfigOverride`, and on `DatabaseSaver`
  `serverAnswerMaxAgeSeconds`, `noServiceConfigStartSeconds`); the game leaves them alone.
- `GameLogger`: the public API is unchanged; one line differs from v1.3.
- `UICanvasGroupFadeInOut` and `MHSBridge`: no API change; the bridge's change is in its
  Editor-only branch. The fade helper is also on the scene fader prefab and the sandbox loader;
  both have their scene-load fades off, so only their explicit fades change, and they now run on
  real time, which is what each of them wants while the game is paused.
- `SettingsSaveManager.cs` and `GameLogger.cs` are the logging fix v1.3 files plus this bundle's
  change to each. The `playerOverrideId` field on the `AuthManager` prefab works as before.
- Build every unit from the same files. The device copy lives in one `PlayerPrefs` slot that unit
  builds served next to each other share; a unit built from the previous files does not know the
  marks the new ones write (a superseded record, for one) and would treat such a record as a
  resume candidate.

## Why `GameLogger.cs` is part of this fix

The unit transition proceeds only when every `ICheckForUnitTransition` says it is ready. The v1.3
logger took a gap of more than one frame between the loader's polls as a new transition request,
and the loader polls a not-ready check every second frame, so the logger's five-second cap never
fired: with the log host unreachable, the unit transition waited until the host answered. The
saver's own cap cannot help there. The one-line change makes the rule a one-second time gap. Test
T21 in `tools/SaveFixTests.cs` drives the gate the way the loader does and expects it open after
about five seconds. Nothing else differs from v1.3; diff to confirm.

## Why the developer key moved out of the source

The Editor defaults in `MHSBridge.cs` and the localhost defaults in both page templates carried
the old service key. It still works for the developer identity `000000000000000000000001` (the
services keep it as a restricted key for that one id), but it is public, it stops working for any
other id once the services enforce the restriction, and a key in source ends up in every copy of
the project and, through `LogManager.cs`, in every build. Both places now read
`mhs-bridge-dev-config.json`, the same JSON shape as the host page's `window.__mhsBridgeConfig`.
The WebGL bridge and any host other than localhost are unchanged (identity and settings come from
StrataHub as before); builds uploaded through MHS Builds do not use the template page at all.

## The checks we run on a build

With a test member that has progress, on the dev site, the save host blocked or answered by the
browser's request interception (recipe in `tools/README.md`):

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
   A checkpoint reached while that line shows is kept on the device and goes out once the
   service has answered (the countdown on the line keeps running meanwhile; after five minutes
   without any answer it goes out unchecked).
4. **A refused key** (answer the save with 401): the status says *the save service refused this
   game's access key* and keeps retrying; nothing is lost.
5. **An HTML page instead of JSON** (answer the load with 200 and `<html>`): counted as a failure
   and retried; not treated as "no saves".
6. **Unit transition with the save host and the log host both blocked:** the transition proceeds
   within about five seconds; the console says the save and the log entries are kept for the next
   unit's build; the next unit sends them. A build without this bundle's `GameLogger.cs` fails
   this check: it sits at the transition until the log host answers.
7. **The browser console carries no `Bearer` value** at any point.
8. **A page without save settings** (a build served from a plain page: not localhost, not
   StrataHub): START shows *Loading your progress... waiting for the save service settings* for
   about five seconds, then *No save service on this page. Playing without saving.* and a new game
   starts; checkpoints send nothing and keep nothing. A page with settings but no identity keeps
   waiting with *waiting for your sign-in*.
9. **The developer file** on localhost: with `mhs-bridge-dev-config.json` next to the page, saves
   and loads go to the services with the key from the file; without it the page reports
   *developer identity only, no services* and the game behaves as in check 8.

## What changes for students

- Saving looks the same when the service answers: "Saving Game..." then "Game Saved!" in the same
  toast as before. The same checkpoint raised twice in a row no longer sends two requests.
- When a save cannot get through, the toast area stays up with a status line instead of the old
  "Caution: Game Save Failed..." and silence; it names the reason in plain words and counts down
  to the next attempt. The game keeps playing. The status lines are:
  - `Saving Game... (attempt K)`
  - `Not saved yet: <reason>.` / `Retrying in N s (attempt K)`
  - `Loading your progress...`, `Loading your progress... (attempt K)`
  - `Can't load your progress yet: <reason>.` / `Retrying in N s (attempt K)`
  - `Saving an earlier checkpoint...` and `Saving an earlier checkpoint: <reason>.` /
    `Retrying in N s (attempt K)` (a save left unsent by an earlier launch is being checked
    against the service before it goes out)
  - `No save service on this page.` / `Playing without saving.` (only on a page that gave the
    game no save-service settings at all)
  - `Using the progress saved on this device.` / `Still checking the save service...` and, between
    attempts, `Still checking the save service... N s (attempt K)`
  - `Newer progress found on the save service.` / `Loading it...`
  - `Not saved: the save service has newer progress.` (a checkpoint of this session not sent,
    because the service was found to hold progress this session did not start from) and
    `Not saved: your saved progress was cleared.` (the same, after the cleared-progress answer)
  - `Your saved progress was cleared.` / `Reload the page to start over.` (the service had accepted
    this device's copy and now has no saves at all: the progress was cleared while this device
    could not load; the session plays on unsaved and the next launch starts a new game, as a
    launch with the service answering would have)
  - `Newer progress was saved on another device.` / `This session is not saved.`
    (the service holds progress further on than the scene on screen; the player plays on
    unsaved, and the next launch resumes the further progress. Should a save of the session
    have reached the service just before that was decided, the further progress is posted
    again so that it stays the newest.)
  - A load-only outage (saves get through, loads do not) holds a START-resumed session's
    checkpoints on the device for up to five minutes while the game waits for the service's
    answer; after that they go out, and the service is not asked again in that launch, so
    progress made on another device during the outage shows at the next launch.
  - `Saving... waiting for your sign-in`, `Saving... waiting for the save service settings`
    (only when the host page gave the game no identity or no endpoint)
  - `Loading your progress... the page's save service settings are incomplete`,
    `Saving... the page's save service settings are incomplete` (one endpoint configured, the
    other not: a page or developer-file error; the game waits rather than guess)
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

## What each file fixes

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
| Nothing was kept for the next launch. | The PlayerPrefs storer on the prefab was only ever cleared. | `mhs_last_save` in PlayerPrefs holds the newest record with `sent`/`dropped` flags; an unsent one is found at the next launch (any scene, as soon as the player id is known), checked against the service's newest, and sent; an unsent record of another player on a shared device is moved to `mhs_pending_save_<id>` instead of being overwritten; records over 128 KB stay in memory only (today's saves are 45 KB). |
| A failed load started the unit over. | `null` from the load, and `startNewGameIfNoExistingSaveData`, ran `LoadNextGameplayScene`; the next checkpoint then overwrote the real progress on the server. Seen in production. | Only the service's `[]` starts a new game. A failure is retried (3 → 30 s) with the countdown while the student waits at START GAME. With a device copy for this player the game resumes from it meanwhile; when the service answers with newer progress that is loaded instead; the service's "no saves" discards a sent device copy (a staff reset) but keeps an unsent one. |
| Two load requests per launch. | The main menu's background load and START GAME each asked. | One shared fetch per launch; START GAME reuses the menu's answer; an accepted save updates the known newest. |
| Requests went out without an id or a config. | `user_id: ""` (1,496 refused requests until September), `mhs_developer`. | Nothing is sent without a 24-hex id (on WebGL the bridge's id is used as soon as it exists) or without the endpoint config; both are waited for with a status line. |
| The unit transition could wait for ever. | Ready only while `saveGameInProgress` was false. | Ready when nothing is unsent, or five seconds after the transition first asked; the save stays on the device and the next unit's build sends it. |
| Scaled-time waits. | `WaitForSeconds` in the toast. | Every wait is real time, so a paused game does not stall saving or loading. |
| Small guards. | `CheckIfSceneShouldLoad` indexed character 5 of any scene name; a second START during a start; a deactivated object left flags set. | Length and null checks; a second START is ignored while one runs; `OnDisable` clears the loop flags; `OnEnable` restarts the send loop if a save is unsent. |
| *From the second review (`04-review-102526.md`):* a newer save could be lost while an older one was in flight (R1). | The record whose request had just finished was written to the device slot, over a newer unsent record; a reload in that window lost the newer save. | After a request the slot keeps the newest record. |
| START GAME reused the menu's answer however old (R2). | One fetch per launch, cached; a tab left at the menu resumed stale progress and the next checkpoint wrote it over newer progress on the service. | An answer older than a minute is fetched again at START; the usual menu → START flow still makes one request. |
| The service's later answer could rewind progress made since a device-copy resume (R3). | The comparison used the copy the game started from, not the checkpoints recorded since. | It uses this device's newest progress at that moment; the service's save is applied in place only when it is newer than that. |
| A page without save-service settings waited at START for ever (R4). | The brief's rules applied strictly; the old code fell back to a built-in host and started an unsaved game. | After five seconds without any save-service settings START plays a new game without saving, with a line saying so. The settings are static for a page, so this is never a network condition; a page with settings but no identity still waits. |
| A load answer whose newest document is unreadable counted as "no saves" (R5). | `[]` and "nothing readable" led to the same new game. | Ask for the last five and resume from the newest readable one; none readable is a retried failure, never a new game. The usual launch still asks for one. |
| The recovered-save wait had no reason or countdown, two loops wrote one line, and the load countdown froze during a request (R6, R7). | — | One owner of the line, the fetch's countdown, with the right first line for the player's wait or for the recovered save; the attempt number shows while a request is out. |
| A copy the service had accepted could beat the service's newest on clock skew (R8). | Two device clocks compared with no tolerance. | The service's answer is authoritative for accepted copies; only unsent progress on this device can beat it. |
| A full device storage blocked the slot write for the session (R9). | The move-aside of another player's unsent copy and the slot write shared one guard. | Separate guards: the oldest pending copy is evicted and the move retried, then the slot is written regardless; the per-record ceiling is 128 KB (today's saves are 45 KB). |
| A newer save waited out the previous countdown and inherited its attempt number (R11). | — | It goes out at once with its own count. |
| A second saver lived for a frame in every gameplay scene (R12). | The gameplay core-systems prefab carries the SaveSystem prefab too; the PixelCrushers singleton removes it a frame later, after its `Start`. | A second instance stays inert (it also silences the bare `DatabaseSaver` component on the pause menu's Load Button, which should be deleted). |
| *From the review of 1.2 (`04-review-102526.md` §7):* a page with neither identity nor save settings waited at START for ever (V1). | The id was checked before the settings, so the no-settings rule never ran without an id. | The settings are decided first: a page without them plays a new game without saving after five seconds whether or not it has an identity; one with settings but no identity still waits. |
| The refetch at START threw the known answer away (V2). | A service outage at that moment resumed an older device copy, or waited at the menu, although a recent answer was known. | The earlier answer is the fallback when the refetch fails: it outranks an accepted device copy, an unsent copy newer than it still wins, a stale "no saves" is not acted on, and the later reconcile compares against the state actually resumed from. |
| An unsent save of this launch went out over newer progress the service was known to hold (V3; reworked after the review of 1.3, W1). | The "service holds newer progress" check covered recovered saves only, and a first attempt to widen it compared clocks across devices, which dropped real checkpoints when the other device's clock ran ahead. | Decided by identity, not clocks: the game remembers the service answer this launch adopted as its base (resumed from, applied in place, or its own accepted save); a save of this launch is dropped only when the service's newest, as last fetched, is a different answer (someone else saved since), and when a newer service save is applied in place the unsent record made before that moment is dropped then. Every drop shows *Not saved: the save service has newer progress*. A recovered save (from an earlier launch, no base of its own) keeps the time rule. |
| Two writers during the first in-flight load with a recovered save (V4); toasts cut short by a hide (V5). | — | The recovered-save line goes through the line's owner; the loops' hides skip while a toast shows. |
| A page without a save service, with a leftover record, waited the cap at every unit transition (V6). | — | The gate ignores a record that cannot be sent from this page. |
| The reference-less stray saver could take charge in Editor flows (V7). | The in-charge mark was set before the references were checked. | A saver without its references, or without a `GameSaveManager` on its object, is never in charge and no longer throws. |
| The pending index could list keys that were gone, defeating the eviction retry (V8). | — | The index is written before the data; eviction skips entries already gone. |
| The no-settings decision lived in the fetch loop only (V9). | A scene opened directly with a leftover record waited for the settings for ever. | One shared decision, used by both loops. |
| *From the review of 1.3 (`04-review-102526.md` §8):* the no-settings flag could start a new game over a known answer, and a page with a load endpoint but no save endpoint counted as "no service" (W2). | — | The decision needs both endpoints missing; the new-game branch runs only when the service has not answered. A partial setup is waited on with the settings line. |
| A drop was invisible, and a session played behind the service's progress in another unit dropped every checkpoint silently (W3). | — | A toast on every drop; a line when the game stays where the service has moved past: *Newer progress was saved on another device. This session is not saved.* |
| The send loop checked the id before the settings (W4); the in-place apply could rewind this launch's progress on the server's clock alone (W5); two writers remained while a recovered save waited for the first fetch (W9); the pending index could over-evict once (W10); the wired check would have silenced a saver on the Addressables path (W11). | — | Settings first in the send loop too; the in-place apply needs a device-clock time when progress was made this launch; the fetch loop's lines own the recovered-save wait; the index is written before any deletion; the Addressables path is tolerated. |
| *From the second pass over the base logic (`04-review-102526.md` §10):* a record dropped as superseded came back as a resume candidate at the next launch (Y1); the follow-up save after an in-flight supersede could record the menu scene (Y2); a held checkpoint showed a frozen *Saving Game...* (Y3); the checkpoint an apply re-fires counted as progress and could beat newer saves from elsewhere (Y4). | — | A superseded record is marked and is never resumed; the follow-up waits for the apply and is never made from the menu; a held save leaves the line to the device-copy countdown, and the hold is bounded at five minutes; a checkpoint within five seconds of an apply is a replay, weighed as the state it replays. Small: the refusal sentinel, the failure count after a discarded answer, a toast no longer cut by *Saving Game...*. |
| *From the third pass (`04-review-102526.md` §11):* after the five-minute hold ran out, the launch's own accepted save came back through the reconcile as "newer progress" (Z1); a save accepted after START had refused another unit's progress lifted the refusal (Z2); the reconcile's wait for a resume's scene load was bounded by a clock, which a slow Addressables load could outrun (Z3), and ended one frame before the apply (Z4); the replay rule rested on the checkpoint number, which the re-fire need not repeat (Z5). | — | An answer carrying this launch's own save is in step; the window alone decides a replay. Small: the hold's state is reset with the loop; a save about to be held writes no *Saving Game...*; a stale follow-up request is cleared; countdowns wait for a toast to finish. |
| *From the fourth pass (`04-review-102526.md` §12):* the reconcile's wait for the resume's scene load had a clock that a slow Addressables start could outrun, after which the newer save was refused against the main menu (AA1); a save in flight when START refused the service's further progress was accepted afterwards and left the service's newest stale (AA2); a newer save for an earlier unit refused the session (AA3); the hold's expiry survived into a second START (AA4); a re-fire from the end-of-frame step in a hidden tab could fall outside the replay window (AA8). | — | The decision is made against the scene a pending resume is loading (it is handed the newer state and applies it when the scene is up) or the scene on screen, with no wait; the refusal stands and the further progress is posted again over the accepted save, with no *Game Saved!* for it; an earlier unit by number is not further on and keeps the player's position; START resets the hold's expiry; a record within two frames of an apply is a replay however long the frames took. Small: a held save owns no line from its first second; the scene-load subscription is dropped when a scene that will not apply comes up; a launch gets a new session id in the Editor too. |
| *From the fifth pass (`04-review-102526.md` §13):* another scene of the same unit (Unit 4 has three) was taken as not further on (AB1); the loading scene's name was hard-coded into the resume's bookkeeping (AB2); a second START during the re-post adopted the stale save (AB3); saves cleared on purpose while the loads were down were recreated by the session's next checkpoint (AB4); the decision could be made at the menu with the wrong words (AB5). | — | Only an earlier unit by number is known not to be further on; no scene name but the menu's is hard-coded (unit scenes are told by their number); a START after a refusal decides on the refused progress, and the re-post stops once a START has adopted something else; a "no saves" answer after the service had accepted this device's copy means the progress was cleared: the session plays on unsaved (*Your saved progress was cleared. This session is not saved.*) and the next launch starts anew; at the menu the newer state is adopted and waits for START. Small: toasts about a pending resume show once it has applied; flags reset when the object is deactivated. |
| *From the focused review of the fifth pass (`04-review-102526.md` §14):* a refusal from an earlier START crashed the send loop during a second START (AC1); the decision did not wait through a lesson (AC2); START was ignored at the menu for the whole outage (AC3); a save in flight at the menu could leave a stale save newest (AC4); the re-post's start and stop (AC5). | — | A second START decides afresh and holds its saves; the decision waits through the loading scene, lessons and the customization; START at the menu during the wait resumes the device's progress again; the adopted base is posted again when there is no gameplay scene to save from; the re-post runs while its answer is the refusal or the base. Small: any scene the game names ends a redirected resume; the refusal and cleared lines reworded. |
| *From the focused review of the base logic (`04-review-102526.md` §9):* START's refusal of an answer could be undone by the send loop's first-knowledge adoption (X1); a fetch answer arriving after the game's own save was accepted replaced the base and dropped every later checkpoint (X2); a checkpoint in the quarter second before START decided was dropped (X3); a checkpoint made during the resume window went out before the service's first answer (X4, pre-existing); a save superseded while in flight became the base (X5); the base was adopted after the apply (X7). | — | The refusal is explicit and the send loop never adopts while START decides; two answers are the same when they carry the same save, and a stale answer is discarded; the decision about a save waits while START decides; a save made while START is deciding waits for the service's first answer (the device keeps it, the device-copy line shows); a superseded save that is accepted is followed by the current state; the base is adopted before the apply. A page with a save endpoint but no load endpoint waits with *the page's save service settings are incomplete* (X6). |

### `SettingsSaveManager.cs`

| Issue | What the new file does |
|---|---|
| Built-in host and key as the fallback when the host page gives no settings config. | Removed; without a config the request is skipped and the caller told `false`, like the empty-id guard. |
| No timeout. | 30 s on both requests; the `Authorization` header is set only when there is a value. |

### `GameLogger.cs`

| Issue | What the new file does |
|---|---|
| The transition gate's five-second cap never fired: `TransitionSceneLoader` polls a not-ready check every second frame, and the gate took a gap of more than one frame as a new request, resetting its clock on every poll. With the log host unreachable the unit transition waited until the host answered. | A new request is a gap of more than one second between calls. Everything else is the v1.3 file. |

### `UICanvasGroupFadeInOut.cs`

| Issue | What the new file does |
|---|---|
| The alert canvas fades with scaled time. A fade-in started while the game is paused (`Time.timeScale = 0`) first sets the alpha to 0 and then never moves, so a status set while paused was invisible until the game was unpaused; the retry waits are real time, so a countdown could run its course unseen. | The fade advances with real time. The two other canvases using this helper gain the same (their fades complete while paused). |

### `MHSBridge.cs` and the two page templates

| Issue | What the new files do |
|---|---|
| The Editor defaults in `MHSBridge.cs` and the localhost defaults in both templates carried the old service key. | No key in source: both read `mhs-bridge-dev-config.json` (the project root for the Editor, next to the page on localhost), the same JSON shape as `window.__mhsBridgeConfig`. Without the file: developer identity, no services. WebGL behaviour and any host other than localhost are unchanged. |

## Verification record

| Where | Before the fix | After the fix |
|---|---|---|
| Editor: the PlayMode tests in `tools/SaveFixTests.cs` (Unity 6000.0.74f1, batch mode, 2026-10-24; T21 added 2026-10-25; T22–T30 with version 1.2, 2026-10-25; T31–T56 with version 1.3 and the passes over its base logic, 2026-10-26) | — (the tests describe the new rules; on the old files they do not compile against the new API) | 55 tests pass in about four and a half minutes: payload helpers; save retried with a countdown until accepted and no auth value in any log line; a newer save replaces an unsent one (one request once the service is back); an unsent save survives a relaunch and is sent then; an unsent save is dropped when the service already holds newer progress; a refused key is retried; a rejected payload is not; a timeout is retried; the save waits for a valid id; the transition gate opens after about five seconds with the save kept; a failed load never starts over and resumes when the service answers; `[]` starts a new game while an HTML body is a failure; the device copy is used while the service is down and left alone when it confirms; the service's newer save wins; one load request per launch; an identical repeat of a checkpoint is not sent again; the logger's transition gate opens after about five seconds with an entry queued and no log endpoint (the v1.3 rule never opened it). From 1.2: START asks again when the menu's answer is older than the limit; progress made since a device-copy resume is not rewound by the service's later answer; a page with no save settings plays a new game without saving after the grace period and keeps nothing; an unreadable newest document leads to a second request for the last five and a resume from the first readable one, and only unreadable documents are a retried failure; the recovered-save wait shows the fetch's countdown and the device-copy line holds after START; a newer save recorded during a retry wait goes out at once with its own count; a second saver stays inert; a copy the service accepted does not beat the service's answer. From 1.3: START plays without saving on a page with neither identity nor settings, and a leftover record there holds no transition; an older save accepted while a newer one waits leaves the newer one on the device; the attempt line shows during a load request; the pending index after a vanished key, and a record over the ceiling stays in memory; the earlier answer is used when the refetch fails, over an accepted device copy too; an unsent save is dropped when the service is known to hold newer progress, decided by identity for a save of this launch (a service save with a clock three minutes ahead does not stop this launch's checkpoints; a checkpoint made while the service is further along in another unit is dropped, with the toast, although it is newer by the clock); a partial setup is waited on, not taken for no service; a save recorded with neither identity nor settings is kept quietly; a saver without references stays inert even when it enables first; START's refusal of an answer is not undone by saving; a checkpoint during the refetch waits and goes out when the same save comes back; a save superseded in flight is followed by the current state; a save endpoint without a load endpoint waits visibly; a superseded record is not resumed at the next launch; a held checkpoint shows the device-copy countdown; a re-fired checkpoint does not beat a newer save from elsewhere; when the hold runs out the own save goes out, is not applied back as newer progress, ends the fetch's countdown, and later saves are not held; a save accepted after START refused the service's further progress does not lift the refusal, and the further progress is posted again; a second START in the same launch holds its checkpoints again; unit order decides what counts as further on, and another scene of the same unit counts as further; saves cleared while the loads were down are not recreated and the next launch starts anew; a second START after a refusal resumes the refused progress; a second START after a refusal with the loads down holds its saves and sends them when the hold runs out; an unsent copy still saves after a "no saves" answer. |
| WebGL builds of Unit 1 on the dev site (release profile `Release Unit 1`, Unity 6000.0.74f1, batch mode; collections `20261024-savefix1-SaveFixTest` = unit1 v2.9.5, version string `20261024-savefix1`, before the review changes, and `20261024-savefix2-SaveFixTest` = unit1 v2.9.6, `20261024-savefix2`, the 1.0 files; neither active for students), 2026-10-24, with the dev test member, headless Chromium, the save host blocked or answered through request interception | On the schools' code a blocked save host at START GAME started the unit over (the production case in `01-findings.md` §3.5) and a failed save was a toast. | Both builds launch, render and play (the built data carries the new members and none of the old key strings; the console carries no `Bearer` value). **One load per launch** (the menu and START GAME share it; two before). **Normal play to the first checkpoint** ("Gear Up"): one save request, 201, and the service's document carries `mhs_meta` (seq 1, the build's version string, scene, checkpoint 0). **Save host blocked at a checkpoint** (savefix2): the toast shows *Not saved yet: no connection to the save service. Retrying in 34 s (attempt 5)*, the attempts come at 5, 10, 20, 40 s, play continues. **Page reloaded with that save still unsent**: the next launch finds it (*an unsent save from an earlier launch was found on this device*), checks the service, and, because another profile had meanwhile stored a newer save for the same member, drops it (*the save service already holds newer progress … the unsent save is dropped*), as designed; the resend of a pending save when the service holds nothing newer is the editor test T04. **Save host blocked before launch, no copy on the device** (a fresh profile): START GAME shows *Can't load your progress yet: no connection to the save service. Retrying in N s (attempt K)* with attempts at 3, 6, 12, 24, 30 s and stays at the main menu (the unit does not start over); unblocked, the next attempt gets the save and the game resumes at "Gear Up — Proceed to the door", not at the intro. **Same, with the device copy** (the profile that made the save): after the third failed attempt the game resumes from the copy (*Using the progress saved on this device. Still checking the save service…*), the service keeps being asked, and when unblocked its answer (the same save) changes nothing. **An HTML page instead of JSON on the load**: *load not answered (200 the response is not a list of saves; … unreadable)*, retried on the same schedule, not taken as "no saves"; the real answer then resumes the game. **The refused key (401) and the transition gate** were not reached on the site (a 401 needs a save to go out; the gate needs the end of the unit) and rest on the editor tests T06, T10 and T21. Two things seen: the browser reports two refused connection attempts per request while the host is blocked (the same request, once more from the WebGL plumbing; harmless), and the two-line status text overflows the Alert Panel's width on the `SaveSystem` prefab (see "What changes for students"). The test member's saved state was deleted and its collection put back afterwards. |
| WebGL build of Unit 1 of version 1.2 on the dev site (release profile `Release Unit 1`, Unity 6000.0.74f1, batch mode; collection `20261025-savefix3-SaveFixTest` = unit1 v2.9.7, version string `20261025-savefix3`; not active for students), 2026-10-25, with the dev test member, headless Chromium, the save host blocked through request interception | The 1.0 build behaved as the row above says; the items of the second review were not covered. | The build launches, renders and plays. **The stale answer (R2):** 70 s at the main menu, then START GAME: a second load request goes out before anything is resumed. **A new game on the service's `[]`** (not observed on the site before): START led to the Transition scene and Unit 1 from the beginning. **A real checkpoint save:** the intro played through to "Gear Up", one save request, 201. **Relaunch with the save host blocked, a copy on the device:** the menu's load attempts fail at 3, 6, 12 s; START resumes from the device copy at once (console: *resuming from the copy on this device (Unit 1 Dev, checkpoint 0, …)*), the Transition screen shows *Using the progress saved on this device. Still checking the save service... 5 s (attempt 4)*, and the unit comes up at "Gear Up — Proceed to the door", not at the intro. **The re-fired checkpoint save** shows *Not saved yet: no connection to the save service. Retrying in N s (attempt K)* with attempts at 5, 10, 20, 40 s; that line owns the display while the fetch keeps retrying (24, 30 s). **Unblocked (R3):** one load answered 200, then the unsent save accepted 201, the line clears, the game stays where it was; the console has no *holds newer progress; loading it* line, so the earlier service save was not applied over the progress made since. Not reached on the site: a page without save settings (the StrataHub page always supplies them), an unreadable newest document, the second saver, the refused key, the unit transition (editor tests T24, T25, T29, T06, T10, T21). Seen: the WebGL request layer reports a refused connection as status 500 "Unknown Error"; it is classified as *no connection to the save service*, which is the right wording. The two-line status text still overflows the Alert Panel. The member's saved state was deleted and its collection put back to Default afterwards. |
| WebGL build of Unit 1 of version 1.3 (release profile `Release Unit 1`, Unity 6000.0.74f1, batch mode; version string `20261026-savefix4`), 2026-10-26: on the dev site as collection `20261026-savefix4-SaveFixTest` (unit1 v2.9.8, not active for students) with the dev test member, and served from a plain page on a LAN address with neither identity nor settings (no StrataHub, not localhost) | 1.2 waited for ever on the plain page (*waiting for your sign-in*), and threw the menu's answer away at START when the service did not answer at that moment. | **Plain page (V1):** the page reports *Not connected to StrataHub — no identity available*; the game logs *no load endpoint configured … waiting*, after five seconds *no save-service settings on this page … nothing will be loaded or saved*, then *starting a new game without saving*; the Transition scene and Unit 1 follow; no request leaves the game. **Dev site, stale answer with the host down at START (V2):** the menu's load answered (no saves); START 66 s later with the save host blocked: the refetch fails at 3, 6, 12 s, the stale "no saves" is not acted on, the main menu shows *Can't load your progress yet: no connection to the save service. Retrying in N s (attempt K)*; unblocked, the next attempt answers, the Transition scene and Unit 1 follow (a new game on the service's `[]`). The remaining 1.3 items (the earlier answer used as the fallback when it holds a save, an unsent save dropped under newer progress, the owner-routed line, the toasts, the gate on a no-service page, the inert stray saver, the pending index) rest on the editor tests T31–T38, T23 and T29; the drop rule's rework after the review of 1.3 (identity instead of clocks, W1–W11) and the five passes over that logic (X1–X9, Y1–Y10, Z1–Z11, AA1–AA8, AB1–AB7, AC1–AC7) came after this build and are covered by the editor tests T38–T56 and the reworked T23, T29, T31, T36, T37, T44, T47, T48. The member's saved state was deleted (none had been made) and its collection put back to Default afterwards. |

## Version history

- 1.0 (2026-10-24): the fix, with the changes from an independent review of the same day.
- 1.1 (2026-10-25): `GameLogger.cs` made a required part of the bundle; its test added.
- 1.2 (2026-10-25): the findings of a second review applied (`04-review-102526.md`); the
  developer key taken out of the game's source and page templates; eight tests added.
- 1.3 (2026-10-26): the findings of a review of 1.2 and of three passes over the base logic
  applied (`04-review-102526.md` §7–§14); eighteen tests added. If you applied an earlier
  version, replace all eight files again.
