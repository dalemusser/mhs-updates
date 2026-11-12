# Save/resume fix: what a resumed game gets back

**Date:** 2026-11-11. **Version:** 1.0. **Verified** in a copy of the game team's 2026-10-29 project (changelist
12527) with the changes applied. **Test build on the dev site:** collection `20261111-saveresume-SaveResumeFixTest`
(Units 2, 3 and 4 with the fix; not active for students).

**Start with `03-for-the-game-dev.md`.** Part 1 is what to do: four scripts, the quest table, the Unit 2 replay
prefab, five boxes in the Unit 2 scene, the tests, build and hand-over. Part 2 is the Editor tests. Part 3 is the
supporting information: what each change fixes, how it was checked, and what was found and left alone.
`01-findings.md` holds the evidence.

## What is in this folder

| File | Purpose |
|---|---|
| `03-for-the-game-dev.md` | The page for the developer applying the fix (Part 1 what to do, Part 2 tests, Part 3 supporting information). |
| `Game-Code/` | The changed files, mirroring `Assets/`: `Scripts/Systems/Save Load System/CustomSavers/{CheckpointSaver.cs, DatabaseSaver.cs}`, `Scripts/Map/FOWMapSwitcher.cs`, `Scripts/Map/ModularMap/Scripts/MapDataProcessor.cs`, `Data/Dialogue System/QuestAndUnitSO.asset`, `Prefabs/_Core/SaveSystem/AutoSaveTracker - Unit 2.prefab`, and `Scenes/Gameplay/Unit 2 Prod/Unit 2 Prod (Refactor).unity.diff` (the five scene lines, not the scene). |
| `tools/` | `SaveResumeTests.cs` (19 Edit Mode tests), `Fixtures/` (15 game saves), `README.md` (install, run, what each test checks). |
| `01-findings.md` | How resumes work, what was measured, and what each finding rests on. |

## Summary

A resume is meant to give the student back the world as it was when the game saved. Measured against live play, it
fell short in four ways (1 to 4), and Unit 2 had long stretches with no save at all (5):

1. **The savers apply in an undefined order** (Pixel Crushers keeps them in a `HashSet`). The checkpoint replay and
   other savers set some of the same objects, so the result depended on the order: a Unit 3 supply crate disappeared,
   and a Unit 4 puzzle collider came back on. Fixed by applying the checkpoint replay first, in MHS code.
2. **Five Unit 2 quests were listed as Unit 1**, so every Unit 2 resume reset them to unassigned. Among them is the
   final quest, *Claim of Command* (checkpoint 14). Fixed in the quest table.
3. **The fog-of-war flag was toggled**, not set, and it is not reset between scene loads. Fixed in two scripts.
4. **The Unit 2 map hints** came back wrong after resumes at 4 to 14, because of the replay events. Fixed in the
   replay prefab.
5. **Long stretches without a save** in Unit 2: five steps had checkpoint numbers and replay events but no save.
   Turned on at 2, 3, 5, 9 and 11.

Saved data, the save format, the save service and StrataHub are unchanged. Saves made before the change still load.
