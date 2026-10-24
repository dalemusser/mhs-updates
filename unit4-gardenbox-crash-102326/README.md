# Unit 4 crash at Tera's garden boxes — "memory access out of bounds"

**Date opened:** 2026-10-23. **Fix written and verified in the editor:** 2026-10-23. **Test build on the dev site:** collection `20261023-gardenbox-GardenBoxFixTest` (unit4 v2.9.2), not active for students.
**Start with `03-for-the-game-dev.md`**: which two files to replace, the one prefab reference to
assign, how to build and hand the build back, and the background on what each change does.

## What is in this folder

| File | Purpose |
|---|---|
| `03-for-the-game-dev.md` | The short page for the developer applying the fix. |
| `Game-Code/` | The two changed files, mirroring `Assets/Scripts/`: `Mini-games/Glyph Games/Teras Garden Boxes/GardenBoxesUIController.cs` and `GardenBoxesEventHelper.cs`. |
| `tools/` | The PlayMode tests that reproduce the crash on the old file and pass on the new one, the YAML helper for the prefab edit, and how to run them (`tools/README.md`). |
| `01-evidence.md` | The production record: 15 runtime-error crashes since 2026-09-14, 14 of them this bug; where in play they happen; stacks; devices; rates; what the logged soil values say. |
| `02-hypothesis-and-plan.md` | The analysis the fix followed: the code path that fits every trace, why a C# index error becomes a WebAssembly trap in this build, the repro, the fix options, verification, the hand-off format. |
| `queries/unit4_crash_context.js` | Read-only mongosh script that produced `data/`; rerun it for new crashes (recipe in `../gamelogger-fix-093026/tools/queries/README.md`). |
| `data/unit4_crash_context.out` | Its output: every crash with device block, full stack, 40 game events before and 15 after (ids shortened to their last 6 hex, hosts replaced). |

## Summary

When a student re-opens a garden box after all three cameras are placed, and that box's camera
was placed without choosing a soil position, `GardenBoxesUIController.AnimateWateringPlant`
indexes `_inactiveCameraImagesToFlash[-1]`. The WebGL build uses *Explicitly Thrown Exceptions
Only*, so the index error is not a managed exception but a WebAssembly memory trap: the game dies
with "Uncaught RuntimeError: memory access out of bounds". Students relaunch and usually get past
it, losing the minutes since the last save. Confirmed in the editor on 2026-10-23 by driving the
controller through the students' sequence on the prefab (`tools/GardenBoxCrashReproTests.cs`).

The fix guards the indices in the animation (nothing to flash when no position is stored), and
removes the way into the bad state: the Place camera button is enabled only once a soil position
is chosen, and `PlaceCamera()` refuses a placement without one. The `GameObject.Find("Dialogue
Canvas")` dereferences in both files are guarded as well. Progression, dialogue variables and the
quest entries are unchanged; the game log stops recording `soilType: None` placements.

**Reference snapshot.** The files were written against the game team's project copy of
2026-09-28 (changelist 12449, Unity 6000.0.74f1), which the team holds; the two files are
identical in that copy and the 2026-09-21 copy. There is no separate snapshot zip for this bundle.
