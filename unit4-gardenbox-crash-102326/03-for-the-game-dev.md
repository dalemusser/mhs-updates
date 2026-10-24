# Unit 4 crash at Tera's garden boxes: what to do in the game code

**Date:** 2026-10-24. **Short version:** replace two C# files, assign one button reference in one prefab, build Unit 4, upload through MHS Builds. No scene, dialogue-database or host-page changes. The evidence is in `01-evidence.md`, the analysis in `02-hypothesis-and-plan.md`; this page is only what you need to apply the fix, then the background.

## The crash, in one paragraph

Since the September builds, Unit 4 has died with **"Uncaught RuntimeError: memory access out of bounds"** for about one student in ten who reaches Tera's garden boxes (14 of the 15 runtime-error crashes recorded between 2026-09-14 and 2026-10-23, on unit4 v2.8.1, v2.8.4 and v2.8.9 alike). It happens when a student **re-opens a garden box after all three cameras are placed, and that box's camera was placed without choosing a soil position**. The Place camera button accepts a click with no position chosen; `PlaceCamera()` then stores −1 as that box's soil (the log shows it as `cameraPlaced … soilType None`, 21 % of all placements). When the box is re-opened after the wrap-up dialogue, `AnimateWateringPlant()` indexes `_inactiveCameraImagesToFlash[-1]`. In the editor that is an `IndexOutOfRangeException`; in the WebGL build it is a crash, because the build uses *Explicitly Thrown Exceptions Only* (no array bounds or null checks are generated), so the bad read goes through and WebAssembly traps. Students relaunch and lose the minutes since their last save.

Confirmed in the editor on 2026-10-23: driving the controller through that exact sequence on the `TerasGardenBoxesUI` prefab throws

```
IndexOutOfRangeException: Index was outside the bounds of the array.
  at MHS.GardenBoxesUIController+<AnimateWateringPlant>d__33.MoveNext ()
  (GardenBoxesUIController.cs line 112: _inactiveCameraImagesToFlash[_selectedSoilIndex].gameObject.SetActive(true);)
```

## The two files

Copy each file from `Game-Code/` over the file of the same name in the project. They were written against the project as of 2026-09-28 (changelist 12449, Unity 6000.0.74f1); both files are unchanged between that copy and the earlier 2026-09-21 copy, so if your copies still match, a straight replace is safe. Diff first if you have touched them since. Line endings are LF, as in the project.

| From `Game-Code/` | To `Assets/Scripts/` |
|---|---|
| `Mini-games/Glyph Games/Teras Garden Boxes/GardenBoxesUIController.cs` | `Mini-games/Glyph Games/Teras Garden Boxes/GardenBoxesUIController.cs` |
| `Mini-games/Glyph Games/Teras Garden Boxes/GardenBoxesEventHelper.cs` | `Mini-games/Glyph Games/Teras Garden Boxes/GardenBoxesEventHelper.cs` |

The public methods the prefab's buttons and the world boxes call (`TurnOnUI`, `CloseUI`, `SetCurrentPlanterIndex`, `SetSoilIndex`, `PlaceTemporaryCamera`, `PlaceCamera`, `ActivateNextCollider`, `KeepUIOpen`) keep their names and signatures, so no event wiring changes.

## The prefab: one reference

`GardenBoxesUIController` has a new serialized field, **Place Camera Button**. Assign it in `Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxesUI.prefab`:

- Open the prefab, select the root `TerasGardenBoxesUI`, find the `GardenBoxesUIController` component, and drag `TeraGardenBoxCanvas/Static UI/HUD/PlaceCamera` (the Button) onto **Place Camera Button**. Save.
- In the YAML that is one line in the controller's block (`--- !u!114 &5593944335515394450`), after the `buttons:` list: `_placeCameraButton: {fileID: 1276088063958510367}`. `tools/assign_place_button_in_prefab.py <path to the prefab>` makes that edit.

If the field is left empty the code finds the button at runtime (the entry of `buttons` whose onClick calls `PlaceCamera`), so the fix works either way; assigning it makes the dependency visible in the Inspector and saves the lookup.

## Build and hand-over

1. Build Unit 4 from the release profile as usual, with a distinct `bundleVersion` so the build's log entries can be told apart (ours was `20261023-gardenbox`).
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the check below on it (and the editor tests on your branch if you want them: `tools/GardenBoxCrashReproTests.cs`, see `tools/README.md`).
4. Only after that is the collection made active for students.

**The check on a build.** Play to the garden boxes. Open the first box: the Place camera button must be greyed out until a soil position is clicked, then enabled. Place all three cameras (re-open a placed box before the third is placed: Place is enabled, as before). After the wrap-up dialogue, re-open each box: the watering animation plays, the chosen position's camera image flashes while the feedback dialogue shows, and the UI closes when it ends. Nothing in the browser console, no crash panel on the StrataHub play page, and the game log shows `cameraPlaced` events with a real soil only (Gravel, Sand or Clay, never None).

## What changes for students and for the data

- **Place camera is disabled until a soil position is chosen** (option B in the analysis). Everything else about placing is the same: clicking a dirt patch shows the camera there, Place stores it, the next boxes unlock, re-opening a placed box lets the student move its camera.
- **No more `soilType: None` placements** in the game log. The grading rule for this point (`U4P6`) compares each box's latest `cameraPlaced` soil with the expected one, so a None was simply a wrong soil for it; the rule needs no change. The Dialogue System's `Cameras_Placed` counter now counts every placement (a −1 placement never changed the box's Soap variable, so the planter never counted it); `Cameras_Placed_Correctly`, the quest entries and the dialogue are untouched.
- **Re-opening a box that was placed without a soil in an older build** (a saved game from before the fix) no longer crashes: the animation plays, the feedback dialogue for a wrong placement plays where the dialogue defines one, nothing flashes, and the UI closes.

## What the code changes do (background, nothing to do here)

### `GardenBoxesUIController.cs`

| Issue | What was wrong | What the new file does |
|---|---|---|
| The crash. | `AnimateWateringPlant` indexed `_inactiveCameraImagesToFlash[_selectedSoilIndex]` and `plantUsables[_selectedPlanterIndex.Value]` unchecked; the soil is −1 for a box placed without a position. | Both indices are copied at the start of the coroutine and checked before use (`IsValidIndex`); with no valid soil there is simply nothing to flash. |
| The bad state could be made. | `PlaceCamera()` stored whatever `_selectedSoilIndex` held, −1 included, and the Place button was always clickable. | The Place button is `interactable` only while a soil position is chosen (`UpdatePlaceCameraButton`, called from `TurnOnUI` and `PlaceTemporaryCamera`); `PlaceCamera()` returns early when no position is chosen. New serialized field `_placeCameraButton`, with a fallback lookup by the button's `PlaceCamera` listener. |
| Closing the UI during the two-second watering animation. | `CloseUI()` sets the planter index and soil to −1; the coroutine read them after the wait, so `plantUsables[-1]` followed. Not reachable by students today (the Close button is inactive in the prefab and nothing activates it), but the same crash class. | The coroutine works on its copies of both values; `SetPlantGrowth` takes the planter index as a parameter. |
| The growth sprites were set through a child scan. | `_plants.GetComponentsInChildren<Image>()` skips inactive children and the result was indexed `[0..2]` unchecked. | The serialized `plantImageLeft/Center/Right` references (already assigned in the prefab to those three images) are used; the scan remains as a fallback, with a length check and a warning. |
| `GameObject.Find("Dialogue Canvas")` was dereferenced unchecked. | A missing or inactive canvas object is a null dereference, which under this build setting is the other crash message seen in production ("null function or function signature mismatch", one crash on 2026-10-08 with no trace to tie it to a line). | `FindDialogueCanvas()` returns null when absent; the coroutine then skips the wait. The `WaitWhile` also stops if the canvas is destroyed meanwhile. |

### `GardenBoxesEventHelper.cs`

| Issue | What the new file does |
|---|---|
| `ReopenUI` dereferenced `GameObject.Find("Dialogue Canvas")` unchecked. | Same guard as above: no canvas, no wait, the UI re-opens at once. |

### Not changed, for the record

- `TriggerFeedbackDialogue` for box 2 with no soil plays no feedback dialogue (the switch has no branch for −1); after the fix that state cannot be made, and for an old saved game the quest entry is still marked and the UI closes as the code always intended.
- The build setting *Explicitly Thrown Exceptions Only* stays. It is the reason an index error is a dead game rather than a console line, and it applies to every script in the project, not only this one. For diagnosing any future "memory access out of bounds", a one-off build with *Full Without Stacktrace* turns the trap into a logged managed exception with the method name; the team chose explicit-only for Chromebook performance, so we propose it as a diagnostic build, not as the release setting.

## Verification record

| Where | Before the fix | After the fix |
|---|---|---|
| Editor: the PlayMode tests in `tools/` on the `TerasGardenBoxesUI` prefab (Unity 6000.0.74f1, batch mode, 2026-10-23) | **A1**, the production sequence: `IndexOutOfRangeException` in `AnimateWateringPlant` at line 112 (`_inactiveCameraImagesToFlash[-1]`) — the crash. **A3**, closing the UI during the watering animation: `IndexOutOfRangeException` at line 110 (`plantUsables[-1]`). **B1**: Place camera clickable with no position chosen and `PlaceCamera()` stores −1. **A2** (re-open a box placed with a soil: flash, wait for the dialogue, close) and **B2** (a placed box can be re-placed) pass, so the normal path was already right. | All six pass: A1, A2, A3, B1, B2 and **B3** (the prefab reference left empty: the button is found by its listener and gated the same way). No error or exception logged in any test. |
| WebGL build of Unit 4 (release profile `Release Unit 4`, `bundleVersion 20261023-gardenbox`) uploaded to the dev site | — | TODO-VERIFY: build in progress. |
