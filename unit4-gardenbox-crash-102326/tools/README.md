# tools/

| File | Purpose |
|---|---|
| `GardenBoxCrashReproTests.cs` | PlayMode tests that drive `GardenBoxesUIController` through the students' sequence on the real `TerasGardenBoxesUI` prefab: the crash (A1), the normal re-open with a soil (A2), closing during the watering animation (A3), and the Place button gating of the fix (B1, B2). They reproduce the crash on the unfixed file and pass on the fixed one; see `../03-for-the-game-dev.md`, Verification record. |
| `assign_place_button_in_prefab.py` | Inserts the `_placeCameraButton` reference into the controller's block of `TerasGardenBoxesUI.prefab` (the one prefab edit of the fix) for anyone who prefers editing the YAML to dragging the reference in the Inspector. `python3 assign_place_button_in_prefab.py "<project>/Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxesUI.prefab"`. Idempotent. |

## Running the tests

The project has no play-mode test assembly for its game code (`Assembly-CSharp`), so the tests
need two temporary project settings. Neither belongs in a release build; remove both afterwards.

1. `ProjectSettings/ProjectSettings.asset`: `playModeTestRunnerEnabled: 1` (the Test Runner
   window's *Enable playmode tests for all assemblies*). This gives `Assembly-CSharp` the NUnit
   reference.
2. `Assets/csc.rsp` with one line, so `Assembly-CSharp` also sees `UnityEngine.TestTools`
   (`[UnityTest]`), which that setting alone does not add in Unity 6000.0 / Test Framework 1.3.9:

   ```
   -r:"<absolute path to the project>/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"
   ```

3. Put `GardenBoxCrashReproTests.cs` anywhere under `Assets/` outside an assembly definition
   (we used `Assets/Scripts/_Dev/GardenBoxCrashRepro/`). It is wrapped in `#if UNITY_EDITOR`.
4. Run from the Test Runner window (PlayMode), or headless:

   ```
   Unity -batchmode -projectPath "<project>" -runTests -testPlatform PlayMode \
         -testFilter MHS.GardenBoxCrashRepro -testResults results.xml -logFile unity.log
   ```

   Each test takes 2 to 7 seconds; a headless run of the project takes about a minute on a warm
   Library.

What the tests stand in for: the Unit 4 scene's `DialogueSystemController_Gameplay` prefab is
instantiated as the scene has it (database, Lua, dialogue UI), because the first `DialogueLua` or
`QuestLog` call with no dialogue controller present overflows the editor's stack in this project;
its `Dialogue Canvas` object is renamed and a bare `Canvas` named `Dialogue Canvas` stands in,
so the tests control the "dialogue is showing" state the garden box code waits on. The prefab's
nine dialogue triggers are disabled with their conversation cleared, so no conversation starts.
The planter usables, the four colliders and the event helper's object switcher, which the scene
assigns and the prefab leaves empty, get stand-ins. The `GardenBoxLogger` component is removed
from the instance so no `GameLogger` is created.

Two things about the Unity Test Framework learned on the way, in case the tests are extended:
`[UnitySetUp]`/`[UnityTearDown]` on this fixture made the test runner overflow its stack before
the setup body ran, so setup and cleanup are inline (`Setup()` / `Cleanup()` in a `finally`); and
when the runner fails a test on an unhandled exception log, that `finally` does not run, so every
object a test creates carries a `GardenBoxTestObject` marker and the next test's setup removes
leftovers first.
