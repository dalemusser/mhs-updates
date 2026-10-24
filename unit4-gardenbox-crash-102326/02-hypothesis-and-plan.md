# 02 — Hypothesis, repro, fix options, plan

All paths are relative to the Unity project copy
`mhs2/20260928_CH12449_MHS 2.0 modified - logging fix v1.3/MHS 2.0`. Line numbers are as of
2026-10-23 and approximate (the files are short; search for the identifiers).

## 1. Why a C# error shows up as "memory access out of bounds"

`ProjectSettings/ProjectSettings.asset` has `webGLExceptionSupport: 1` = **Explicitly Thrown
Exceptions Only**. In that mode IL2CPP emits no null checks and no array bounds checks; a C#
`IndexOutOfRangeException` or `NullReferenceException` is not raised — the generated code
reads or writes through the bad address, and WebAssembly traps: `RuntimeError: memory access
out of bounds` (out-of-range index) or `null function or function signature mismatch`
(virtual call on null). So the crash message tells us "a managed index or null error
happened somewhere", and the game log tells us where in play.

Other settings worth knowing: `webGLInitialMemorySize: 32`, `webGLMaximumMemorySize: 2048`,
`webGLMemoryGrowthMode: 2` (geometric growth), `allowUnsafeCode: 0`, no Burst, no unsafe
code outside two bridge files. Memory growth is not the issue (heaps flat at the crash).

## 2. The code path that fits every trace (H1)

Files: `Assets/Scripts/Mini-games/Glyph Games/Teras Garden Boxes/`

- `GardenBoxesUIController.cs`
  - `TurnOnUI(plantType)` (≈ l. 56): `_selectedSoilIndex = _soapPlanterList[_currentPlanterIndex].Value;`
    (the soil index previously placed for this box, **−1 if a camera was placed without
    choosing a position**), then `if (finishedPlacingCameras.Value) StartCoroutine(AnimateWateringPlant());`
    (≈ l. 77).
  - `AnimateWateringPlant()` (≈ l. 83–120): after the 2 s watering animation,
    `plantUsables[_selectedPlanterIndex.Value].enabled = false;` → `TriggerFeedbackDialogue(...)`
    (fires a `DialogueSystemTrigger.OnUse()` — this is the dialogue event logged in the same
    second as the crash) → **`_inactiveCameraImagesToFlash[_selectedSoilIndex].gameObject.SetActive(true);`**
    (≈ l. 112) and `var flashImage = _inactiveCameraImagesToFlash[_selectedSoilIndex];`.
    With `_selectedSoilIndex == -1` this is an out-of-range index → the trap.
  - `PlaceCamera()` (≈ l. 300): `_soapPlanterList[_currentPlanterIndex].Value = _selectedSoilIndex;`
    with no check that a soil was chosen; `_selectedSoilIndex` starts at −1 for a box with no
    camera (set in `TurnOnUI`) and is only changed by `SetSoilIndex → PlaceTemporaryCamera`.
  - `CloseUI()` (≈ l. 272): sets `_selectedPlanterIndex.Value = -1` and `_selectedSoilIndex = -1`.
- `GardenBoxLogger.cs`: `GetSoilValue` returns "None" for any index outside 0–2 — the 213
  "None" placements in production are exactly these −1 placements.
- `GardenBoxesPlanter.cs`: `CheckIfCameraCorrectlyPlaced` counts `Cameras_Placed` /
  `Cameras_Placed_Correctly` in Dialogue Lua; the dialogue (conversation 92, the wrap-up at
  nodes 32–33) sets `BoolVariable_FinishedPlacingCameras` (referenced from
  `Assets/Data/Dialogue System/DialogueDatabase.asset` at two UnityEvent targets).

Prefab `Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxesUI.prefab` (controller at l. 3786):
`_inactiveCameraImagesToFlash` has **3** entries, `plantUsables` 3, `_soapPlanterList` 3,
`buttons` 4, `dialogueSystemTriggers` 9. The Place button's onClick: `PlaceCamera` (l. 3136)
then `LogCameraPlaced` (l. 3148). The three soil buttons: `SetSoilIndex` → `PlaceTemporaryCamera`
→ `LogSoilSelected`. The world boxes are `Assets/Prefabs/Mini-games/GardenBoxes/TerasGardenBoxes.prefab`
/ `Assets/Prefab Models/_Unsorted/MHS/Prefab_Planter_Box.prefab` (the "Press E To Place
Cameras" prompt); the scene is `Assets/Scenes/Gameplay/Unit 4/Unit 4 Dev.unity`
(also `Assets/Scenes/Colin TerasGardenBoxes Prototyping.unity` for isolated testing).

**Sequence that reproduces the traces:** open a box, press *Place camera* without choosing a
position (soil −1 is stored, logged "None"), place the other two boxes normally, finish the
wrap-up dialogue (`FinishedPlacingCameras` becomes true), re-open the "None" box →
`AnimateWateringPlant` → `_inactiveCameraImagesToFlash[-1]`. Re-opening a box with a real soil
value works (crash 13 shows exactly that two seconds earlier).

### Secondary candidates in the same coroutine (check while there)

- H2: `SetCornOrPeasGrowth` etc. index `images[0..2]` from `_plants.GetComponentsInChildren<Image>()`,
  which skips inactive children by default; fewer than three active images → out of range.
- H2b: the 2 s `WaitForSeconds` inside `AnimateWateringPlant`: if the student closes the UI
  during it, `CloseUI` sets `_selectedPlanterIndex.Value = -1` and `plantUsables[-1]` follows.
- H3 (the "null function" variant): `GameObject.Find("Dialogue Canvas").GetComponent<Canvas>()`
  in `AnimateWateringPlant` and `GardenBoxesEventHelper.ReopenUI` with no null check.

## 3. Verify in the editor (no build needed)

1. Open the project in Unity 6000.0.74f1; open `Unit 4 Dev.unity` (or the prototyping scene).
2. Play; reach the garden boxes (the game has a debug menu — `DEBUGMenu` events — and the
   quest is "Desert Delicacies", entries 13–15; a saved state near the boxes may be faster).
3. Follow the sequence above. In the editor the index error is a normal
   `IndexOutOfRangeException` in the Console with a C# stack at
   `GardenBoxesUIController.AnimateWateringPlant`. That console line is the confirmation.
4. Also try H2b (close during the watering animation) and look for the H2 condition
   (count the active `Image` children of `_plants` at that moment).

## 4. Fix options (do the first two; consider the third)

A. **Guard the indices** in `AnimateWateringPlant`: if `_selectedSoilIndex < 0 ||
   _selectedSoilIndex >= _inactiveCameraImagesToFlash.Length`, skip the flash (keep the
   feedback dialogue and the close); guard `plantUsables[_selectedPlanterIndex.Value]` the same
   way; null-check the Dialogue Canvas lookups (H3).
B. **Prevent the bad state**: the Place button should be `interactable` only once a soil
   position is selected (`_selectedSoilIndex >= 0`), and `PlaceCamera()` should return early
   when it is −1. This also removes the 21 % "None" placements from the data, which the
   grading rules may care about (check `mhsgrading` for the garden box rule before changing
   logged values).
C. **Build setting, for a diagnostic build only**: Exception Support = *Full Without
   Stacktrace* turns every such error into a logged managed exception instead of a dead game.
   It costs performance (the team chose explicit-only for the Chromebooks), so propose it as a
   one-off test build, not a change to the release setting — unless measurements show the
   cost is small.

Keep the dialogue variables (`Cameras_Placed`, `Cameras_Placed_Correctly`) and the quest
entry states unchanged; the fix must not alter progression.

## 5. Prove it

- Editor: the sequence no longer throws; a box placed with a soil still flashes and plays its
  feedback dialogue; closing during the animation is harmless.
- WebGL: build Unit 4 with the fix (per `../gamelogger-fix-093026/00-plan.md`), host it on the
  dev site through StrataHub's MHS Builds page, and run the sequence on a Chromebook or in
  Chrome with 4 GB device emulation; check the StrataHub play page shows no crash record and
  the game log shows `cameraPlaced` events with real soil values only (if B is applied).
- Optional, for the record: the same build with Debug Symbols on, to confirm
  `wasm-function[108552]` maps into `GardenBoxesUIController`.

## 6. Hand-off format

Follow the repository's pattern: this folder gets `03-for-the-game-dev.md` (which file, what
it fixes, how to apply, how to hand the build back), `Game-Code/Mini-games/Glyph Games/Teras
Garden Boxes/GardenBoxesUIController.cs` as a drop-in, and a note of any prefab edit (Place
button interactable binding) with the exact object path in `TerasGardenBoxesUI.prefab`.
Add the folder to `../README.md` and `../ai/context.md` (already listed for the
investigation; update the status line when the fix exists).

## 7. Context from the StrataHub side (no change needed there)

The play page's crash reporter posted every one of these; the activity page classifies them
as real crashes (they are). After the fix ships, the weekly review query
(`~/Desktop/mhs-reports-102326/queries/week.js` in the project owner's workspace) or the
`queries/unit4_crash_context.js` script here will show whether Unit 4 runtime errors stop.
