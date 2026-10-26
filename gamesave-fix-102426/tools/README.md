# tools/

| File | Purpose |
|---|---|
| `SaveFixTests.cs` | PlayMode tests that run the real `GameSaveManager` and `DatabaseSaver` against a fake save service on localhost (`FakeSaveService` in the same file: up, down with 503, refusing with 401, rejecting with 400, answering an HTML page, hanging). Twenty-one tests, one per rule of the fix (T21 is the `GameLogger`'s unit-transition gate, driven the way `TransitionSceneLoader` polls it, with no log endpoint so nothing is sent); see `../01-findings.md` §6 and `../03-for-the-game-dev.md`, Verification record. |
| `../queries/` | The read-only mongosh scripts and the journal summariser that produced `../data/` (recipe in `../../gamelogger-fix-093026/tools/queries/README.md`). |

## Running the tests

The project has no play-mode test assembly for its game code (`Assembly-CSharp`), so the tests
need two temporary project settings (the same two as `../../unit4-gardenbox-crash-102326/tools/README.md`).
Neither belongs in a release build; remove both afterwards.

1. `ProjectSettings/ProjectSettings.asset`: `playModeTestRunnerEnabled: 1` (the Test Runner
   window's *Enable playmode tests for all assemblies*).
2. `Assets/csc.rsp` with one line, so `Assembly-CSharp` also sees `UnityEngine.TestTools`:

   ```
   -r:"<absolute path to the project>/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"
   ```

3. Put `SaveFixTests.cs` anywhere under `Assets/` outside an assembly definition (we used
   `Assets/Scripts/_Dev/SaveFixTests/`). It is wrapped in `#if UNITY_EDITOR`.
4. Run from the Test Runner window (PlayMode), or headless:

   ```
   Unity -batchmode -nographics -projectPath "<project>" -runTests -testPlatform PlayMode \
         -testFilter MHS.SaveFixTests -testResults results.xml -logFile unity.log
   ```

   The suite takes a little over a minute on a warm Library (most of it the real backoff waits: the
   first retry comes after five seconds, the first load retry after three).

How the tests stand in for the game: each test builds a "launch" (`Rig`): a `GameSaveManager` and
a `DatabaseSaver` on one object with runtime Soap variables set through reflection (the prefab's
fields), a `Saver` with one string so a save has recognisable content, and the PixelCrushers
`SaveSystem` singleton. The endpoint configs are pointed at the fake service through
`GameSaveManager.saveConfigOverride` / `loadConfigOverride`; `requestTimeoutSeconds` is shortened
for the timeout test. A relaunch is a new rig on the same PlayerPrefs. Setup and cleanup are
inline, not `[UnitySetUp]`/`[UnityTearDown]` (those overflow the test runner's stack in this
project). The fake service switches mode only after the previous answer has arrived (the tests
wait for the status line), otherwise an in-flight request of the old rig is answered by the new
mode.

## The checks on a build (dev site)

Browser recipe with `playwright-cli` (headless Chromium), the dev test member and its unit set
current; `<dev host>` and `<save host>` as shown on the site's settings and in the play page's
bridge config. The dev site's play page points the game at the **production** save service, so
clean up with the Manage page's *Delete saved state* afterwards.

```
pw() { playwright-cli -s=S "$@"; }
pw open https://<dev host>/login; pw fill e41 "<test member login id>"; pw click e42
# block the save host (both directions) before launching
pw run-code "async page => { await page.context().route(/<save host>/, r => r.abort('connectionrefused')); return 'blocked'; }"
pw goto https://<dev host>/missionhydrosci/play/unit1
pw click '#unity-canvas'; pw press Space; sleep 25            # loading screen -> main menu
pw mousemove 1010 247; pw mousedown; pw mouseup; sleep 15     # START GAME (viewport 1280x720)
pw requests | grep '<save host>/api/state'                    # the attempts and their outcomes
pw run-code "async page => { await page.context().unrouteAll({ behavior: 'ignoreErrors' }); return 'unrouted'; }"
```

To answer instead of block: `route.fulfill({ status: 401, body: 'Invalid API key' })` for the
refused key, `route.fulfill({ status: 200, contentType: 'text/html', body: '<html>blocked</html>' })`
for the filter page. The status line is the Alert Panel's text; headless, read it from the
canvas is not possible, so the checks rely on the requests seen by the browser, the records on
the service (`../queries/`), and the game's console lines (`DatabaseSaver:` warnings), which the
page exposes through the Unity console in DevTools.
