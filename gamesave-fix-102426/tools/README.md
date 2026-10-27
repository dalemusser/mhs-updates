# tools/

| File | Purpose |
|---|---|
| `SaveFixTests.cs` | PlayMode tests that run the real `GameSaveManager` and `DatabaseSaver` against a fake save service on localhost (`FakeSaveService` in the same file: up, down with 503, refusing with 401, rejecting with 400, answering an HTML page, hanging). Fifty-five tests, one per rule of the fix (T21 is the `GameLogger`'s unit-transition gate, driven the way `TransitionSceneLoader` polls it, with no log endpoint so nothing is sent; T22–T30 the second review's findings; T31–T38 the review of version 1.2 and its follow-up; T39–T42 the review of version 1.3; T43–T56 the five passes and a focused read over the base logic); see `../01-findings.md` §6 and `../03-for-the-game-dev.md`, Verification record. |
| `../queries/` | The read-only mongosh scripts and the journal summariser that produced `../data/` (recipe in `../../gamelogger-fix-093026/tools/queries/README.md`). |

## Running the tests

The project has no play-mode test assembly for its game code (`Assembly-CSharp`), so the tests
need two temporary project settings (the same two as `../../unit4-gardenbox-crash-102326/tools/README.md`).
Neither belongs in a release build; remove both afterwards.

1. In the Unity Editor, open **Window → General → Test Runner**, and in its **⋮** menu choose
   **Enable playmode tests for all assemblies** (Unity asks to restart). This writes
   `playModeTestRunnerEnabled: 1` into `ProjectSettings/ProjectSettings.asset` and gives
   `Assembly-CSharp` the NUnit reference.
2. Only if the tests then fail to compile with *The type or namespace name 'UnityTest' could not be
   found* (or `UnityEngine.TestTools` is missing): add a file `Assets/csc.rsp` with this one line, then
   let Unity recompile (switch back to the Editor, or right-click a script → Reimport):

   ```
   -r:"<absolute path to the project>/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"
   ```

   **Where it goes and how Unity finds it.** The file must be named exactly `csc.rsp` and sit at the
   root of `Assets` (not in a subfolder). When Unity compiles the default assemblies
   (`Assembly-CSharp` and the others without an `.asmdef`), it reads `Assets/csc.rsp` and passes
   each line to the compiler; no other configuration is needed. The path in the line is the full
   path to your project folder followed by `/Library/ScriptAssemblies/UnityEngine.TestRunner.dll`,
   in double quotes (macOS example: `-r:"/Users/yourname/Projects/MHS 2.0/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"`;
   on Windows forward slashes work: `-r:"C:/Users/yourname/Projects/MHS 2.0/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"`).
   Unity does not always recompile when only `csc.rsp` changes: right-click any script and choose
   *Reimport*. It has worked when the `UnityTest` error is gone and the Test Runner lists
   `MHS.SaveFixTests` with 55 tests. To remove it, delete `Assets/csc.rsp` and `Assets/csc.rsp.meta`.

   **Why the file is needed at all.** The tests use the game's own classes (`DatabaseSaver`,
   `GameSaveManager`), which compile into Unity's default `Assembly-CSharp`, because the game code
   has no assembly definitions. A normal test assembly (an `.asmdef` marked as a test assembly) cannot
   reference `Assembly-CSharp`; Unity does not allow assembly definitions to reference the default
   assemblies. So the tests have to compile inside `Assembly-CSharp`. The Editor setting in step 1
   adds NUnit there, but in Unity 6000.0 with Test Framework 1.3.9 it did not add
   `UnityEngine.TestRunner`, where `[UnityTest]` lives. The Editor has no other place to add a
   reference to `Assembly-CSharp`; a `csc.rsp` file in `Assets/` passes extra compiler options to it.
   A newer Test Framework may add the reference itself, which is why step 2 is conditional.

   The path is absolute and specific to one machine, so the file should not be committed to the
   shared project. Remove it, and turn the setting in step 1 back off, before making a release build.
   The longer-term alternative is to move the game code into assembly definitions, after which a
   standard test assembly works with neither step; that is a larger change than this bundle.
3. Put `SaveFixTests.cs` anywhere under `Assets/` outside an assembly definition (we used
   `Assets/Scripts/_Dev/SaveFixTests/`). It is wrapped in `#if UNITY_EDITOR`.
4. Run from the Test Runner window (PlayMode), or headless:

   ```
   Unity -batchmode -nographics -projectPath "<project>" -runTests -testPlatform PlayMode \
         -testFilter MHS.SaveFixTests -testResults results.xml -logFile unity.log
   ```

   The suite takes about four and a half minutes on a warm Library (most of it the real backoff waits: the
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
mode. The game's `ConsoleLogManager` runs in the test scene with its flag on, so the savers'
`Print` lines reach `Debug.Log` and the no-auth check covers them; the fake service honours the
request's `limit`; two timing constants are public statics the tests shorten
(`DatabaseSaver.serverAnswerMaxAgeSeconds`, `noServiceConfigStartSeconds`).

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
