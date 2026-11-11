# Editor tests for the save/resume fix

`SaveResumeTests.cs` holds 19 Edit Mode tests; `Fixtures/` holds the 15 game saves they resume.

## Install

| From this folder | To the project |
|---|---|
| `SaveResumeTests.cs` | `Assets/Tests/SaveResume/Editor/SaveResumeTests.cs` |
| `Fixtures/*.json` | `Assets/Tests/SaveResume/Fixtures/` |

The `Editor` folder puts the tests in `Assembly-CSharp-Editor`, which can see the game's classes. `[UnityTest]` needs
`UnityEngine.TestRunner`, which the project gets through `Assets/csc.rsp` (see Part 2 of `../03-for-the-game-dev.md`).
Delete `Assets/Tests/SaveResume/` before a release build.

## Run

In the Editor: **Window → General → Test Runner → EditMode → MHS.SaveResumeTests → Run All**.

From a terminal, with the Editor closed (macOS path shown; the project path is yours):

```
/Applications/Unity/Hub/Editor/6000.0.74f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics \
  -projectPath "<project>" -runTests -testPlatform EditMode -testFilter MHS.SaveResumeTests \
  -testResults results.xml -logFile unity.log
```

Exit code 0 means every test passed; `results.xml` lists each test.

## What the tests check

Each scene test runs in a play session of its own. It puts only its unit's scene in the build scene list (the game
reads its unit from that list), enters play mode, loads the scene, applies a fixture save through
`CheckpointSaver.ApplySaveCheckpointFirst`, waits for the resume to finish (3 s), checks, and leaves play mode. The
Editor's build scene list is put back after every test. Unity's batch mode never signals the end of a frame, so there,
and only there, the test runs the checkpoint saver's end-of-frame step (`ApplyWaypoints`) itself.

| Test | Unit, save | Checks |
|---|---|---|
| T01 | 3, checkpoint 5 | With the checkpoint saver put where it applied last before the fix, Supply Crate 1 still comes back shown, as saved. |
| T02 | 4, checkpoint 2 | Same, for the Infiltration glyph's collider: it comes back off, as saved. |
| T03 | 2, checkpoint 4 | The replay runs once per apply, and again on a second apply in the same scene. |
| T04 | (asset) | The five Unit 2 quests are Unit 2 in `QuestAndUnitSO`. |
| T05 | 2, checkpoint 14 | *Claim of Command* and its entry 1 are active; Jasper is set up for entry 1. |
| T06 | 2, checkpoint 12 | The side quest *Creating a Catalogue* is still active. |
| T07 | 2, checkpoint 7 | `FOWIsActive` matches whether the fog image is shown. |
| T08 | 2, checkpoint 4 | Map hint text and panel are on. |
| T09 | 2, checkpoint 6 | Map hint panel off, text on. |
| T10–T14 | 2, the save before each | Starting the step saves exactly once, with checkpoint 2, 3, 5, 9 or 11. |
| T15–T19 | 2, checkpoints 2, 3, 5, 9, 11 | A save made there resumes at that checkpoint with the step active. |

## With and without the fix

In a copy of the 2026-10-29 project: 19 of 19 pass with the fix. With the team's original files, 13 fail, each on the
problem it is about. T03 and T15–T19 pass either way: T03 guards the new apply path, and T15–T19 show that saves made
at the new save points resume there, which the existing replay events already support.

## The fixtures

Game saves in the game's own JSON form (`SavedGameData`): quest states, Dialogue System variables, saver data,
positions. Units 3 and 4 are real student saves; the Unit 2 ones were recorded by the game in the Editor, starting from
real student saves. They hold no user id, no name and no typed text.

## Unity notes

- A unit scene started on its own logs errors of its own (no StrataHub page, no services). The tests let them through
  (`LogAssert.ignoreFailingMessages`, set inside each test because the framework resets it when a test starts).
- Changing `EditorBuildSettings.scenes` after play mode has started does not reach the game; that is why the tests
  enter play mode themselves.
- Checks sit in the test bodies, not in the helper coroutines: a check that fails inside a nested coroutine left the
  Edit Mode runner unable to give the next test a clean play session.
- If play mode ends while a test is still running, Unity can report that test as passed. Each test therefore marks
  when its checks have run (`Checked()`), and the tear-down fails any "passed" test that did not get there.
