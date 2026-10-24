# Unit 4 crash at Tera's garden boxes — "memory access out of bounds"

**Date opened:** 2026-10-23
**Status:** INVESTIGATION PACKAGE, nothing changed in the game yet. The production evidence
is collected (`01-evidence.md`, `data/`), the code has been read, and there is one strong
hypothesis with a concrete repro to verify (`02-hypothesis-and-plan.md`). The next session
verifies it in the Unity project, writes the fix, proves it with a build, and produces the
hand-off for the game team in this folder.

## Start here (new session)

Working directory for the session: the workspace that holds `mhs-updates`, `stratahub`
and `mhs2` (so the shared instructions and memory apply). Then:

1. Read this file, then `02-hypothesis-and-plan.md` (what to verify and how), then
   `01-evidence.md` (why we believe it; the production traces).
2. The Unity project copy to work in:
   `mhs2/20260928_CH12449_MHS 2.0 modified - logging fix v1.3/MHS 2.0` (Unity 6000.0.74f1,
   installed in Unity Hub). It is the copy that carries the logging fix v1.3; the garden
   box code is unchanged between copies (verify with a diff against the pristine zip
   `mhs2/20260928_CH12449_MHS 2.0.zip` if anything looks off).
3. Code under suspicion: `Assets/Scripts/Mini-games/Glyph Games/Teras Garden Boxes/`
   (seven small files) and the prefab `Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxesUI.prefab`.
4. Building and testing a WebGL unit on the dev site: follow `../gamelogger-fix-093026/00-plan.md`
   (build script `Assets/Scripts/AdroitBuilder.cs`, hosting through StrataHub's MHS Builds
   page, the dev test member). Nothing reaches students from this work directly.

## What is in this folder

| File | Purpose |
|---|---|
| `01-evidence.md` | The production record: 15 runtime-error crashes since 2026-09-14, 14 of them this bug; where in play they happen; stacks; devices; rates; what the logged soil values say |
| `02-hypothesis-and-plan.md` | The code path that fits every trace, why a C# index error becomes a wasm trap in this build, the repro, the fix options, verification, and the hand-off format |
| `queries/unit4_crash_context.js` | Read-only mongosh script that produced `data/`; rerun it for new crashes (recipe in the StrataHub project memory / `../gamelogger-fix-093026/tools/queries/README.md`) |
| `data/unit4_crash_context.out` | Its output: every crash with device block, full stack, 40 game events before and 15 after (ids shortened to their last 6 hex, hosts replaced) |

## Summary for the game team (to be confirmed by the next session)

When a student re-opens a garden box after all three cameras are placed, and that box's
camera was placed without choosing a soil position, `GardenBoxesUIController.AnimateWateringPlant`
indexes `_inactiveCameraImagesToFlash[-1]`. The WebGL build uses *Explicitly Thrown
Exceptions Only*, so the index error is not a managed exception but a WebAssembly memory
trap: the game dies with "Uncaught RuntimeError: memory access out of bounds". Students
relaunch and usually get past it, losing the minutes since the last save.
