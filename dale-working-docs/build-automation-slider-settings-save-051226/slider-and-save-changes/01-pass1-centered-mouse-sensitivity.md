# Pass 1 — Centered-Multiplier Mouse Sensitivity Slider

Pass #1 of the sensitivity/invert series. See
`01-sensitivity-current-state-and-plan.md` for context.

---

## Issue

The user-facing **Mouse Sensitivity** slider only increased sensitivity. The
left end produced `0` (look freezes), and the right end produced erratic
high-speed look. The slider was not a centered-multiplier — it was a raw
scaler.

Root causes:

1. The `BindSlider` on the slider GameObject in both settings prefabs had
   `_useMaxValueFromVariable=0` and `_maxValue.LocalValue=100`. At runtime
   `BindSlider.Awake()` (`Assets/Imported/Tools/Obvious/Soap/Core/Runtime/Bindings/BindSlider.cs:38`)
   calls `OnMaxValueChanged(_maxValue.Value)`, which **overrides** the
   slider's Inspector-configured `m_MaxValue` and forces it to `100`. So the
   slider that the prefab author had set to 0..2 actually ran 0..100 in
   builds. The slider's `m_MinValue` of `0` was not overridden — it stayed
   at `0`, which freezes look entirely on the left end because the value is
   used directly as a multiplier in
   `CharacterControllerAdapter_Player.cs:406-407` and
   `RotateCameraTarget.cs:45-46`.
2. The `FloatVariable_Input_MouseSensitivity.asset` had `_isClamped: 1` but
   the only clamp field present in the YAML was `_minMax: {x: 0.1, y: 1}` —
   a **legacy field** from an older version of SOAP. The current
   `FloatVariable` class (`Assets/Imported/Tools/Obvious/Soap/Core/Runtime/ScriptableVariables/FloatVariable.cs`)
   clamps using `_min` and `_max` `FloatReference` fields, not `_minMax`.
   Because `_min`/`_max` were absent in the YAML, Unity deserialized them
   with `FloatReference` constructor defaults: `_min.LocalValue=0`,
   `_max.LocalValue=float.MaxValue`. Net effect at runtime: clamping was
   effectively `[0, ∞]`, so the unbounded slider writes (up to 100) sailed
   through unclamped.

The combined result was a slider whose useful range was `0..100`, used
directly as a look multiplier with no centering and no upper safety. Hence:
left end freezes, right end runs away.

---

## Fix

Make the slider a centered multiplier with range **0.5 ↔ 2** (full-left =
half speed, midpoint = baseline, full-right = double):

* Update `FloatVariable_Input_MouseSensitivity.asset` to use the modern
  `_min`/`_max` FloatReference fields, with `LocalValue=0.5` and
  `LocalValue=2` respectively. Set `_value=1` and `_defaultValue=1` so the
  variable's natural state is the baseline midpoint.
* On both settings prefabs (`Canvas_Gameplay_Settings.prefab` and
  `Canvas_MainMenu_Settings.prefab`), update the `Slider` component on the
  `MouseSensitivity` GameObject: `m_MinValue=0.5`, `m_MaxValue=2`, `m_Value=1`.
* On the same prefabs, update the `BindSlider` component:
  `_useMaxValueFromVariable=1` so the slider's max tracks the
  FloatVariable's `MaxReference` (single source of truth);
  `_maxValue.LocalValue=2` as a fallback in case the variable reference is
  ever cleared. Note `BindSlider` does not bind `minValue`, so the slider's
  `m_MinValue=0.5` is set directly on the Slider component.

No consumer code changes are needed. The multiply
(`inputLook * deltaTimeMultiplier * _mouseSensitivity`) already does what we
want — it just needs the input to be a sane centered multiplier instead of a
raw 0..100 scaler.

---

## Exact changes

### File 1 — `Assets/Data/Scriptables/Input/FloatVariable_Input_MouseSensitivity.asset`

Before:

```yaml
  _value: 1
  _debugLogEnabled: 0
  _saved: 0
  _defaultValue: 0
  _resetOn: 1
  _isClamped: 1
  _minMax: {x: 0.1, y: 1}
```

After:

```yaml
  _value: 1
  _debugLogEnabled: 0
  _saved: 0
  _defaultValue: 1
  _resetOn: 1
  _isClamped: 1
  _min:
    UseLocal: 1
    LocalValue: 0.5
    Variable: {fileID: 0}
  _max:
    UseLocal: 1
    LocalValue: 2
    Variable: {fileID: 0}
```

Field-level summary:

* `_defaultValue`: `0` → `1`  (midpoint of new range; the SOAP default a
  Reset-to-Default would write back).
* `_minMax: {0.1, 1}` (legacy, ignored at runtime) → removed.
* `_min` added with `LocalValue: 0.5, UseLocal: 1`.
* `_max` added with `LocalValue: 2, UseLocal: 1`.

### File 2 — `Assets/Prefabs/UI/Canvas_Gameplay_Settings.prefab`

Locate the `Slider` MonoBehaviour on the `MouseSensitivity` GameObject
(`fileID 4275124971077909525`). Before:

```yaml
  m_MinValue: 0
  m_MaxValue: 2
  m_WholeNumbers: 0
  m_Value: 1
```

After:

```yaml
  m_MinValue: 0.5
  m_MaxValue: 2
  m_WholeNumbers: 0
  m_Value: 1
```

Locate the `BindSlider` MonoBehaviour on the same GameObject
(`fileID 494940109793693822`). Before:

```yaml
  _floatVariable: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
  _useMaxValueFromVariable: 0
  _maxValue:
    UseLocal: 1
    LocalValue: 100
    Variable: {fileID: 0}
```

After:

```yaml
  _floatVariable: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
  _useMaxValueFromVariable: 1
  _maxValue:
    UseLocal: 1
    LocalValue: 2
    Variable: {fileID: 0}
```

### File 3 — `Assets/Prefabs/UI/Canvas_MainMenu_Settings.prefab`

Same edits as File 2, on the `MouseSensitivity` GameObject in this prefab.

Locate the `Slider` MonoBehaviour (`fileID 7855423527577185693`). Before:

```yaml
  m_MinValue: 0
  m_MaxValue: 2
  m_WholeNumbers: 0
  m_Value: 1
```

After:

```yaml
  m_MinValue: 0.5
  m_MaxValue: 2
  m_WholeNumbers: 0
  m_Value: 1
```

Locate the `BindSlider` MonoBehaviour (`fileID 3346131348119256133`). Before:

```yaml
  _floatVariable: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
  _useMaxValueFromVariable: 0
  _maxValue:
    UseLocal: 1
    LocalValue: 100
    Variable: {fileID: 0}
```

After:

```yaml
  _floatVariable: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
  _useMaxValueFromVariable: 1
  _maxValue:
    UseLocal: 1
    LocalValue: 2
    Variable: {fileID: 0}
```

---

## Doing this in the Unity Editor (rather than YAML)

If the maintainer prefers to apply this change via Inspector instead of YAML
text edits:

1. Open `FloatVariable_Input_MouseSensitivity.asset`.
   * Default Value: `1`.
   * Is Clamped: checked.
   * Min: `0.5`.
   * Max: `2`.
   * Value: `1`.
2. Open `Canvas_Gameplay_Settings.prefab` → `MouseSensitivity` GameObject:
   * On the `Slider` component, set Min Value `0.5`, Max Value `2`, Value
     `1`.
   * On the `BindSlider` component, tick "Use Max Value From Variable" so
     the variable is the single source of truth for the upper bound. If
     left unchecked, set the Max Value field to `2`.
3. Repeat step 2 on `Canvas_MainMenu_Settings.prefab`.

---

## Side-effects to be aware of

* `EventFloat_Input_MouseSensitivityChanged` is still wired to the slider's
  `onValueChanged` in both prefabs. It will now fire with values in
  `[0.5, 2]` instead of `[0, 100]`. **No script currently listens to this
  event** (verified by GUID search across the project) — the only references
  are the two prefabs that fire it. Safe to leave as-is; cleaning it up is a
  candidate for a future pass.
* Existing player saves (via `SettingsSaver`) that persisted a previous
  out-of-range value will be clamped into `[0.5, 2]` on load by SOAP's
  `FloatVariable.Value` setter. Players who had pushed the slider far right
  will land at `2`, which is much saner than their previous state.
* The slider's `m_Value=1` is the prefab default. If a user has no saved
  settings, they will see the slider centered with baseline feel — which is
  the intended behavior.

---

## Verification

In the Editor:

1. Open the `MainMenu` scene; open the Settings panel.
2. Slider should appear centered, labeled with its current text (still
   `Mouse Sensitiviy` — fixed in Pass 2).
3. Drag the slider to the far left. Move the mouse. Look should be slower
   but still respond — not frozen.
4. Drag to far right. Look should be faster but not erratic.
5. Drag to the middle. Look should feel like baseline.
6. Repeat in-game via the pause/settings panel (`Canvas_Gameplay_Settings`).

WebGL: build via the Adroit/MHS Bridge template; repeat on desktop browser.
Trackpad-scroll and iPad two-finger swipe behavior is unchanged in this pass
(both still drive the same multiplier as mouse delta — split happens in
Pass 3).

---

## What this pass does **not** fix

* Scroll wheel / trackpad still shares the mouse-sensitivity slider →
  Pass 3.
* No user-facing invert option → Pass 4.
* Misspellings on the label and field → Pass 2.
* No drone slider, no reset button → Pass 5.
* Apply/Revert semantics on closing the panel → Pass 6.

---

## Follow-up recommendation — audit other clamped FloatVariable assets

The root cause of the slider's broken clamp was that
`FloatVariable_Input_MouseSensitivity.asset` was written in an older SOAP
format using `_minMax: Vector2`, while the current `FloatVariable` class
clamps using `_min`/`_max` `FloatReference` fields. The asset therefore had
`_isClamped: 1` set but no working clamp at runtime — `_min`/`_max`
deserialized as `[0, float.MaxValue]`.

Other `FloatVariable` assets in the project that predate the SOAP upgrade
may have the same issue: an `_isClamped: 1` line, a `_minMax: {x: …, y: …}`
line, and no `_min`/`_max` blocks. Any consumer that relies on the clamp to
keep values in a safe range would be reading unclamped values without
anything obviously broken in the Inspector.

### Suggested audit

From the project root, search for the legacy field on any clamped variable:

```bash
grep -rln "_isClamped: 1" "Assets/Data/Scriptables" \
  | xargs grep -L "^  _min:" 2>/dev/null
```

Any file that shows up has `_isClamped: 1` but no `_min:` block — i.e. it is
in the legacy format and its clamp is silently inactive. Open each one in
the Inspector, confirm the intended Min/Max, and re-save (Unity will
serialize in the new format) or edit the YAML to add the `_min`/`_max`
blocks shown in this document.

### Findings in this snapshot

Running the audit above turned up five legacy-format clamped variables in
`Assets/Data/Scriptables/` (other than the mouse sensitivity asset fixed by
this pass):

| Asset | `_isClamped` intent (`_minMax`) | Actual runtime clamp | `_value` |
|---|---|---|---|
| `Camera/FloatVariable_Camera_DampingWhenOccluded.asset` | `[0, 10]` | `[0, float.MaxValue]` | `0.5` |
| `Camera/FloatVariable_Camera_Radius.asset` | `[0, 10]` | `[0, float.MaxValue]` | `0.1` |
| `Camera/FloatVariable_Camera_Damping.asset` | `[0, 10]` | `[0, float.MaxValue]` | `0.3` |
| `Camera/FloatVariable_Camera_SmoothingTime.asset` | `[0, 2]` | `[0, float.MaxValue]` | `0.2` |
| `Variables/IntVariable_FurthestUnitReached.asset` | `[1, 6]` | `[0, int.MaxValue]` | `2` |

The `IntVariable` (`IntVariable_FurthestUnitReached`) has the same issue —
`IntVariable.cs` uses `_min`/`_max` `IntReference` for clamping, identical to
`FloatVariable`. The legacy `_minMax: Vector2` field is just as inert there.

For the four camera variables, the current `_value` is in-bounds of the
intended `_minMax`, so the missing clamp has caused no visible bug — yet. If
anything writes a value out of `[0, 10]` (or `[0, 2]` for `SmoothingTime`)
at runtime, it will pass through unclamped.

For `IntVariable_FurthestUnitReached`, intent `[1, 6]` versus actual
`[0, int.MaxValue]` means a player who somehow reaches Unit `7` or has
their progress accidentally written to a value outside the unit count would
not be auto-clamped. Worth checking with the team whether the clamp was
intended as a hard safety or as a hint.

### Suggested fix

For each affected asset, either:

1. **In the Editor:** open the asset, click in the Inspector to dirty it,
   then save. Unity will re-serialize using the modern `_min`/`_max` fields
   and drop the legacy `_minMax` line. Verify the Min/Max values in the
   Inspector match the values that were previously in `_minMax`.
2. **By YAML edit:** replace the `_minMax: {x: a, y: b}` line with the
   equivalent `_min`/`_max` blocks (see this document's File 1 section for
   the FloatVariable form; substitute `IntReference` shape for the int
   variant).

This is not a Pass-1 scope item — these assets aren't broken in a way the
user can see — but the same hidden-clamp class of bug should be fixed for
hygiene and to prevent surprises later.
