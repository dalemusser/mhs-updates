# Mouse / Scroll / Drone Sensitivity & Invert — Current State, Issues, and Plan

Working snapshot of Mission HydroSci 2.0 (Unity 6 / `6000.0.44f1`, URP 16.0.4).
Author: handoff doc for the Perforce maintainer who will fold these changes back
into the active project.

This is document #1 in the `docs/` series. Subsequent documents (`02-…`, `03-…`)
describe each pass as it lands, with the exact edits needed to reproduce the
change file-by-file.

---

## 1. Scope

The Settings panel exposes a single **Mouse Sensitivity** slider. The reporter
flagged the following:

1. The slider only **increases** sensitivity. The left end produces `0` and
   freezes look; the right end becomes erratic.
2. A second input device — mouse scroll wheel / two-finger trackpad swipe —
   was added so that look works on iPad, where cursor lock is not available
   and a single-finger swipe cannot be used. Mouse delta and scroll currently
   share the same slider even though their characteristic magnitudes differ
   considerably across devices (notably, the same scroll calibration that
   feels right on iPad trackpad feels significantly slow on MacBook trackpad).
3. A separate **Drone Sensitivity** control was expected but is not present
   in the UI. The drone-side data is configured as absolute angular speeds,
   not multipliers, so a slider added without redesign would behave the same
   way as the broken mouse one.
4. There is no user-facing **Invert** option for look. The current swipe
   directions were picked by the team but are a personal preference; a real
   product needs per-axis user toggles.
5. Spelling errors exist: `Sensitiviy` and `MousSensitivity`.

---

## 2. Current Implementation

### 2.1 Data layer (Obvious SOAP `FloatVariable` ScriptableObjects)

| Asset | Path | `_minMax` | `_value` | `_defaultValue` | Consumer |
|---|---|---|---|---|---|
| `FloatVariable_Input_MouseSensitivity` | `Assets/Data/Scriptables/Input/` | `{0.1, 1}` (clamped) | `1` | `0` | `CharacterControllerAdapter_Player`, `RotateCameraTarget` |
| `EventFloat_Input_MouseSensitivityChanged` | `Assets/Data/Scriptables/Input/` | — | — | — | Raised in parallel by the slider's `onValueChanged` |
| `Drone_FirstPersonXAxisSensitivity` | `Assets/Data/Scriptables/Drone Settings/` | `{100, 1000}` | `190` | `0` | `CharacterControllerAdapter_Drone` |
| `Drone_FirstPersonYAxisSensitivity` | `Assets/Data/Scriptables/Drone Settings/` | (same shape) | — | — | `CharacterControllerAdapter_Drone` |
| `Drone_ThirdPersonXAxisSensitivity` | `Assets/Data/Scriptables/Drone Settings/` | (same shape) | — | — | `CharacterControllerAdapter_Drone` |
| `Drone_ThirdPersonYAxisSensitivity` | `Assets/Data/Scriptables/Drone Settings/` | (same shape) | — | — | `CharacterControllerAdapter_Drone` |

Drone invert is already a `BoolVariable` per axis (FP-X, FP-Y, TP-X, TP-Y), all
consumed by `CharacterControllerAdapter_Drone.cs`. There is **no** invert data
for the character; invert today lives in the input-action processors.

### 2.2 UI layer

* `Assets/Prefabs/UI/Canvas_Gameplay_Settings.prefab` (in-game pause/settings)
  and `Assets/Prefabs/UI/Canvas_MainMenu_Settings.prefab` (main menu) each
  contain a `MouseSensitivity` GameObject with:
  * A `UnityEngine.UI.Slider`: `m_MinValue=0`, `m_MaxValue=2`, `m_Value=1`.
  * An `Obvious.Soap.BindSlider` referencing `FloatVariable_Input_MouseSensitivity`,
    `_useMaxValueFromVariable=0`, `_maxValue.LocalValue=100`.
  * The slider's `onValueChanged` also wired directly to
    `EventFloat_Input_MouseSensitivityChanged.Raise`.
* Label GameObject name: **`MousSensitivity Label`** (typo). Label text:
  **`Mouse Sensitiviy`** (typo).
* The panel root carries `MHS.GameplaySettings`
  (`Assets/Scripts/Settings/GameplaySettings.cs`), which snapshots in `Start()`
  and reverts in `OnDisable()` / `OnApplicationQuit()`.
* No drone slider, no invert toggle, no reset button in either settings panel.

### 2.3 Input layer

`Assets/Settings/Input/Controls.inputactions` defines two relevant action maps:
`Player` and `Drone`. In **both**, the `Look` action (Vector2 / Value /
PassThrough) has these bindings collapsed onto it:

```
<Mouse>/delta   processors: InvertVector2(invertX=false),ScaleVector2(x=0.05,y=0.05)
<Mouse>/scroll  processors: InvertVector2,ScaleVector2(x=0.1,y=0.1)
```

Because both bindings feed the same action, the consumer cannot distinguish
which device fired the value. The `InvertVector2` processors are the project's
current "default direction" picker; they are not user-toggleable.

### 2.4 Consumer layer

* `Assets/Scripts/Player/CharacterControllerAdapter_Player.cs:54,406-407,588`
  ```csharp
  _cinemachineTargetYaw   += _inputLook.x * deltaTimeMultiplier * _mouseSensitivity;
  _cinemachineTargetPitch += _inputLook.y * deltaTimeMultiplier * _mouseSensitivity;
  ```
  `_mouseSensitivity` is a direct multiplier of look input.
* `Assets/Scripts/Camera/RotateCameraTarget.cs:12,41,45-46` does the same in a
  standalone camera-rotation script.
* `Assets/Scripts/Player/CharacterControllerAdapter_Drone.cs:230-318` writes
  the four drone `FloatVariable`s **directly** into Cinemachine
  `m_HorizontalAxis.m_MaxSpeed` / `m_VerticalAxis.m_MaxSpeed` / FreeLook
  `m_XAxis.m_MaxSpeed` / `m_YAxis.m_MaxSpeed`. These are absolute angular
  velocities in degrees/sec — not multipliers.

### 2.5 Persistence

`Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`
(PixelCrushers `Saver`) serializes `mouseSensitivity` alongside audio buses and
character-customization data; restores on load. Any new variables we introduce
must be added here too.

---

## 3. Issues Identified

### 3.1 Slider is not a real sensitivity slider

`BindSlider.Awake()` overrides the slider's `maxValue` to its serialized
`_maxValue.LocalValue=100`, so at runtime the slider runs **0..100** even
though the Inspector says 0..2. The variable is written raw and used as a
multiplier, so `0` freezes look and high values are erratic.

Compounding this: the `FloatVariable_Input_MouseSensitivity` asset is in an
**older SOAP serialization format** that uses a `_minMax: Vector2` field.
The current `FloatVariable` clamps with `_min`/`_max` `FloatReference`
fields instead, so the legacy field is silently ignored. The asset has
`_isClamped: 1` but no working clamp — at runtime the bounds are
`[0, float.MaxValue]`. The same legacy format exists on at least four
`FloatVariable` assets and one `IntVariable` asset elsewhere in the
project; see `docs/02-pass1-centered-mouse-sensitivity.md` for the full
audit. None are currently producing visible bugs because their stored
values happen to be in-bounds, but the hidden-clamp class of bug should
be fixed for hygiene.

### 3.2 Mouse delta and scroll share one sensitivity, and the per-device
       calibration is genuinely incompatible

The `Look` action has two bindings with different scale processors but they
flatten to one Vector2. We cannot fix this with one slider plus per-device
calibration constants, because:

> The default for scrollwheel for two-finger swipe on iPad trackpad was set
> to feel right on the iPad. But if that same value is used and a two-finger
> swipe is done on a MacBook trackpad, it is significantly slow. — reporter

Both surfaces feed `WheelEvent` through the same browser path, but their
pixel magnitudes diverge by enough that no single constant satisfies both. The
right user-facing answer is **two separate sliders** — one for mouse delta,
one for trackpad/scroll — so a user can tune the slider that matters for their
current device. Same logic applies to the drone.

### 3.3 Drone sensitivity is not user-controllable

Settings panels expose no drone sliders. The four `Drone_*Axis*Sensitivity`
values are written directly as Cinemachine max-speeds (deg/sec), not as
multipliers of tuned defaults.

### 3.4 No user-facing invert

Invert lives in the `InvertVector2` processors on the action bindings. Half
the player base (literally — it's a personal preference) wants the opposite
direction. Per-axis invert toggles are needed and should be applied uniformly
to both input devices on a given controllable (axis preference is not a
device preference).

The drone has per-axis `BoolVariable_*Invert` already in its data layer but no
UI binding. The four bools also distinguish FP and TP, which is more
granularity than a user UI needs.

### 3.5 Misspellings

`Sensitiviy` appears in:
* `Assets/Scripts/Settings/GameplaySettings.cs` — field `_mouseSensitiviy`.
* `Assets/Prefabs/UI/Canvas_Gameplay_Settings.prefab` — label GameObject
  `MousSensitivity Label`, text `Mouse Sensitiviy`.
* `Assets/Prefabs/UI/Canvas_MainMenu_Settings.prefab` — same.

### 3.6 Apply/Revert is half-finished

`GameplaySettings.ApplyGameplayChanges()` only updates the snapshot; it does
not commit. `OnDisable()` and `OnApplicationQuit()` always revert. If a panel
Apply button is wired to it, fine; if a user closes the panel after dragging,
their change is silently lost.

### 3.7 Once a user moves off the default, they cannot find it again

Reported pain point. Mitigated by adding a "Reset to defaults" button per
section.

---

## 4. Agreed Design

* **Slider math:** centered multiplier with range **0.5 ↔ 2** (full left =
  half speed, midpoint = baseline, full right = double). Variable value is a
  pure multiplier of look input.
* **Slider count:** two per controllable — one for **Mouse** delta, one for
  **Trackpad / Scroll**. Total four sliders (Character Mouse, Character Scroll,
  Drone Mouse, Drone Scroll). Justified by the iPad/MacBook divergence — no
  single calibration constant can satisfy both surfaces.
* **Invert:** per-axis (X and Y) per controllable, applied uniformly to both
  input devices on that controllable. Two toggles per controllable; four
  total. The four existing drone-axis invert bools are collapsed at the user
  level into two — one Invert X drives both FP-X and TP-X bools; one Invert Y
  drives both FP-Y and TP-Y bools.
* **Defaults:** match today's observed direction so existing users don't see
  a flip.
* **Reset to defaults:** one button per section (Character, Drone).
* **UI structure** (target):

```
LOOK SENSITIVITY
  Character
    Mouse Sensitivity        [====slider====]
    Trackpad / Scroll        [====slider====]
    Invert X                 [ ]
    Invert Y                 [x]
    Reset to defaults        ( button )
  Drone
    Mouse Sensitivity        [====slider====]
    Trackpad / Scroll        [====slider====]
    Invert X                 [ ]
    Invert Y                 [x]
    Reset to defaults        ( button )
```

* **Invert lives in consumer code**, not in action processors. We strip the
  `InvertVector2` processors from `<Mouse>/delta` and `<Mouse>/scroll` (or
  set them to no-op) and apply a `±1` multiplier in the controller based on
  the user bools. This unifies the codepath with the drone, which already
  does invert in the controller.
* **Per-device scale processors stay**, but the *base* calibration values
  (`0.05` and `0.1`) can be re-tuned during QA without touching user UI.

---

## 5. Plan — Passes

Each pass is small enough to land and test independently, and is paired with
a per-pass document (`docs/02-…`, `docs/03-…`) describing the exact edits.

### Pass 1 — Centered-multiplier math for the existing mouse sensitivity slider

Smallest blast radius. Just fix the math/asset on what's already there.

* `FloatVariable_Input_MouseSensitivity.asset`: `_minMax = {0.5, 2}`,
  `_value = 1`, `_defaultValue = 1`.
* `Canvas_Gameplay_Settings.prefab` and `Canvas_MainMenu_Settings.prefab`:
  * `Slider.m_MinValue = 0.5`, `m_MaxValue = 2`, `m_Value = 1`.
  * `BindSlider._useMaxValueFromVariable = 1` so the slider's max tracks the
    FloatVariable's `MaxReference` (single source of truth at the asset).
    Note `BindSlider` does not manage `minValue`, so we set the slider's
    `m_MinValue` directly.
* No consumer code changes — the multiply is already in place.

### Pass 2 — Fix misspellings

* `GameplaySettings.cs`: rename `_mouseSensitiviy` → `_mouseSensitivity`,
  annotated with `[FormerlySerializedAs("_mouseSensitiviy")]` so prefab
  serialization survives the rename.
* Both settings prefabs: rename label GameObject `MousSensitivity Label` →
  `Mouse Sensitivity Label`; update label text `Mouse Sensitiviy` →
  `Mouse Sensitivity`.

### Pass 3 — Split mouse delta from scroll; add character Trackpad/Scroll slider

* In `Controls.inputactions`, add a new action **`LookScroll`** (Vector2,
  Value, PassThrough) to both `Player` and `Drone` maps. Move the
  `<Mouse>/scroll` binding off `Look` and onto `LookScroll`. `Look` keeps
  `<Mouse>/delta` and `<Gamepad>/rightStick`.
* Add `FloatVariable_Input_ScrollLookSensitivity.asset` with the same shape
  as the mouse one (`_minMax = {0.5, 2}`, `_value = 1`, `_defaultValue = 1`).
* `CharacterControllerAdapter_Player.cs`: add `_inputLookScroll` field,
  `OnLookScroll(InputAction.CallbackContext)` callback, accumulate yaw/pitch
  with the scroll-specific multiplier alongside the existing mouse one.
* `RotateCameraTarget.cs`: mirror the change if used in the active scene.
* Add a second slider — "Trackpad / Scroll Sensitivity" — to both settings
  prefabs, using the same `BindSlider` pattern.
* Update `SettingsSaver.cs` to serialize/restore the new variable.
* **Drone scroll is wired in Pass 5.** Pass 3 only touches the character.

### Pass 4 — Character Invert X + Invert Y

* In `Controls.inputactions`, remove the `InvertVector2` processors from the
  `<Mouse>/delta` and `<Mouse>/scroll` bindings on the Player map's `Look` and
  `LookScroll` actions (Drone bindings touched in Pass 5).
* Add `BoolVariable_Input_InvertLookX.asset` and
  `BoolVariable_Input_InvertLookY.asset`. Defaults set so existing users see
  the same direction as today.
* In `CharacterControllerAdapter_Player.cs` (and `RotateCameraTarget.cs`),
  multiply yaw delta by `(invertX.Value ? -1f : 1f)` and pitch delta by
  `(invertY.Value ? -1f : 1f)`. Apply to both mouse and scroll branches.
* Add two `Toggle` controls on both settings prefabs, with SOAP `BindToggle`
  bound to the two new BoolVariables.
* Update `SettingsSaver.cs` to serialize/restore the new bools.

### Pass 5 — Drone sensitivity, invert, and Reset-to-defaults (both sections)

* Add `FloatVariable_Drone_MouseSensitivity.asset` and
  `FloatVariable_Drone_ScrollSensitivity.asset`. Same centered range.
* Add `BoolVariable_Drone_InvertLookX.asset` and
  `BoolVariable_Drone_InvertLookY.asset` (collapsed user-facing version of
  the existing four FP/TP per-axis bools).
* Refactor `CharacterControllerAdapter_Drone.cs`:
  * Stop writing `m_MaxSpeed` directly from a single variable.
  * Capture the **base** `m_MaxSpeed` values for FP/TP X/Y from tuned defaults.
  * Apply `m_MaxSpeed = baseSpeed * userMultiplier` for the mouse path.
  * For the scroll path, switch off `CinemachineInputProvider` for the
    `LookScroll` action and manually accumulate
    `m_HorizontalAxis.Value += scrollX * dt * baseSpeed * scrollMultiplier`
    in the drone controller (mirroring the character path).
  * Invert: drive both `_firstPersonXAxisInvert` and `_thirdPersonXAxisInvert`
    from the single user-facing `BoolVariable_Drone_InvertLookX`; same for Y.
    (Or: stop using the four legacy bools entirely and read the two new ones
    directly. TBD during the pass — depends on what's least invasive.)
* Add a "Drone" section to the settings panel prefab(s): two sliders + two
  toggles.
* Add a **Reset to defaults** button per section (Character, Drone) that
  writes `_defaultValue` back into every bound variable.
* Update `SettingsSaver.cs` to serialize/restore the four new drone variables.

### Pass 6 — Apply/Revert tidy in `GameplaySettings`

* Move snapshot capture from `Start()` to `OnEnable()`.
* `ApplyGameplayChanges()` becomes a real commit: overwrite the snapshot with
  the current values so subsequent `OnDisable` reverts are no-ops.
* Track `_isCommitted` (or similar) so `OnDisable()` reverts only when the
  user has not pressed Apply during the panel lifetime.
* Update for the additional variables added in earlier passes so the snapshot
  covers all of them.
* Wire the existing Apply button to the updated `ApplyGameplayChanges()`.

---

## 6. Out of Scope

* Touch / mobile input redesign beyond the existing scroll-wheel binding.
* Cinemachine input-provider replacement on the character side (the
  character already bypasses `CinemachineInputProvider` for look — only the
  drone uses it).
* Gamepad right-stick sensitivity — currently tuned by a `ScaleVector2(x=4,y=4)`
  processor and not exposed to users. Could be a follow-up.
* Re-tuning the base `ScaleVector2` constants on the action bindings.
  Possible during QA; not a code change in these passes.

---

## 7. Verification Plan

Per pass:

1. Open the relevant scene (`Init` → `MainMenu` → unit scene) in the Editor.
2. Open the Settings panel; exercise all controls added by that pass.
3. Smoke-test that earlier passes still work (regression coverage grows pass
   over pass).
4. Build to WebGL via the Adroit / MHS Bridge template; repeat the test on
   desktop browser (mouse + trackpad), MacBook trackpad (scroll path), and
   iPad Safari (scroll path) so device-specific calibration is exercised.
5. Use the panel's Apply / close paths to confirm persistence (especially
   after Pass 6).

Acceptance for the full series:

* Slider midpoint = baseline feel on the device the team tuned the base
  processors for.
* Slider full-left ≈ half speed; full-right ≈ double speed.
* Invert X and Y toggles flip behavior independently for both mouse and
  scroll on the same controllable.
* Reset-to-defaults restores all values in a section in one click.
* Closing the panel without Apply reverts; closing after Apply persists.

---

## 8. Document Index

* `01-sensitivity-current-state-and-plan.md` — this file.
* `02-…` and onward — per-pass change records, added as each pass lands.
* `build-automation-postprocess-robustness.md` — standalone, unrelated to
  this series. Documents a fix to `BuildFromBuildProfiles.cs` that came up
  while attempting a verification build after Pass 2. Distribute to the
  build-automation maintainer separately.
* `webgl-template-build-and-run-fix.md` — standalone, unrelated to this
  series. Documents a fallback added to `MHS Bridge` and
  `MHS Bridge - iPad Test` WebGL templates so they work with Unity's
  "Build and Run", in addition to the existing production multi-build
  hosting pattern. Distribute to whoever owns the WebGL templates.
