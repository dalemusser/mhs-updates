# Pass 2 — Fix `Sensitiviy` / `MousSensitivity` Misspellings

Pass #2 of the sensitivity/invert series. See
`01-sensitivity-current-state-and-plan.md` for context.

---

## Issue

Three misspellings existed around the mouse-sensitivity feature:

* `Assets/Scripts/Settings/GameplaySettings.cs` — private field
  `_mouseSensitiviy` (missing a `t`).
* Both settings prefabs — the slider's label `GameObject` was named
  `MousSensitivity Label` (missing an `e`) and its TextMeshPro `m_text` was
  `Mouse Sensitiviy` (missing a `t`).

These are visible to players (in the label) and to anyone reading the code
(in the field name).

---

## Fix

1. Rename the C# field `_mouseSensitiviy` → `_mouseSensitivity`, with
   `[FormerlySerializedAs("_mouseSensitiviy")]` so existing prefab/scene
   references migrate without losing the assignment when the Editor reloads
   them.
2. Update the prefab YAML to use the new field key as well — belt and
   suspenders. The attribute makes the migration automatic on Editor
   reload, but updating the YAML means the prefab works correctly even
   before that migration runs (e.g. on a clean checkout or a non-Editor
   import).
3. Rename the label GameObject in both prefabs: `MousSensitivity Label` →
   `Mouse Sensitivity Label`.
4. Update the label text in both prefabs: `Mouse Sensitiviy` →
   `Mouse Sensitivity`.

---

## Exact changes

### File 1 — `Assets/Scripts/Settings/GameplaySettings.cs`

Add the `UnityEngine.Serialization` import for `FormerlySerializedAs`,
rename the field and its three usages.

Before:

```csharp
using Obvious.Soap;
using UnityEngine;

namespace MHS
{
    public class GameplaySettings : MonoBehaviour
    {
        [SerializeField] private ScriptableVariable<float> _mouseSensitiviy;
        private float _oldMouseSensitivity;

        void Start()
        {
            ApplyGameplayChanges();
        }

        private void OnDisable()
        {
            RevertGameplayChanges();
        }

        public void RevertGameplayChanges()
        {
            _mouseSensitiviy.Value = _oldMouseSensitivity;
        }

        public void ApplyGameplayChanges()
        {
            _oldMouseSensitivity = _mouseSensitiviy.Value;
        }

        #region ApplicationQuit
        private void OnApplicationQuit()
        {
            RevertGameplayChanges();
        }
        #endregion
    }
}
```

After:

```csharp
using Obvious.Soap;
using UnityEngine;
using UnityEngine.Serialization;

namespace MHS
{
    public class GameplaySettings : MonoBehaviour
    {
        [FormerlySerializedAs("_mouseSensitiviy")]
        [SerializeField] private ScriptableVariable<float> _mouseSensitivity;
        private float _oldMouseSensitivity;

        void Start()
        {
            ApplyGameplayChanges();
        }

        private void OnDisable()
        {
            RevertGameplayChanges();
        }

        public void RevertGameplayChanges()
        {
            _mouseSensitivity.Value = _oldMouseSensitivity;
        }

        public void ApplyGameplayChanges()
        {
            _oldMouseSensitivity = _mouseSensitivity.Value;
        }

        #region ApplicationQuit
        private void OnApplicationQuit()
        {
            RevertGameplayChanges();
        }
        #endregion
    }
}
```

### File 2 — `Assets/Prefabs/UI/Canvas_Gameplay_Settings.prefab`

Locate the `GameplaySettings` MonoBehaviour reference (search for the
script GUID `a08cd50c76a8dbb4f999be57afddf4f3`, around line 1908). Update
the serialized field key:

Before:

```yaml
  _mouseSensitiviy: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
```

After:

```yaml
  _mouseSensitivity: {fileID: 11400000, guid: c9a08ff2d0355fd42bf43a0a4232eb3c, type: 2}
```

Locate the label `GameObject` (search for `m_Name: MousSensitivity Label`,
around line 634). Update its name:

Before:

```yaml
  m_Name: MousSensitivity Label
```

After:

```yaml
  m_Name: Mouse Sensitivity Label
```

Locate the TextMeshPro component on that GameObject (search for
`m_text: Mouse Sensitiviy`, around line 687). Update the text:

Before:

```yaml
  m_text: Mouse Sensitiviy
```

After:

```yaml
  m_text: Mouse Sensitivity
```

### File 3 — `Assets/Prefabs/UI/Canvas_MainMenu_Settings.prefab`

This prefab does **not** carry a `GameplaySettings` MonoBehaviour (it's
only on the in-game settings prefab), so there is no `_mouseSensitiviy`
serialized reference to update here. Only the label needs two fixes.

Locate the label `GameObject` (search for `m_Name: MousSensitivity Label`,
around line 3634). Update its name:

Before:

```yaml
  m_Name: MousSensitivity Label
```

After:

```yaml
  m_Name: Mouse Sensitivity Label
```

Locate the TextMeshPro component on that GameObject (search for
`m_text: Mouse Sensitiviy`, around line 3687). Update the text:

Before:

```yaml
  m_text: Mouse Sensitiviy
```

After:

```yaml
  m_text: Mouse Sensitivity
```

---

## Doing this in the Unity Editor (rather than YAML / text edit)

If the maintainer prefers Inspector/Hierarchy edits:

1. Open `Assets/Scripts/Settings/GameplaySettings.cs` in an editor, apply
   the C# changes shown above. Wait for Unity to recompile.
2. Open `Canvas_Gameplay_Settings.prefab`. With the `GameplaySettings`
   MonoBehaviour selected on the prefab root, the field will appear as
   "Mouse Sensitivity" with the same value assigned (thanks to
   `[FormerlySerializedAs]`). Save the prefab — Unity rewrites the field
   key to `_mouseSensitivity`.
3. In the same prefab, rename the `MousSensitivity Label` GameObject in
   the Hierarchy to `Mouse Sensitivity Label`. Update its TextMeshPro
   "Text" property from `Mouse Sensitiviy` to `Mouse Sensitivity`. Save.
4. Open `Canvas_MainMenu_Settings.prefab`. Repeat step 3 for the same
   label GameObject in this prefab. Save.

---

## Why keep `[FormerlySerializedAs]` if we're updating the YAML too?

* Defense against any other prefab, scene, or variant we haven't found
  that still references the field by its old name.
* Future migrations: if anyone branches from a pre-rename revision and
  merges back, their prefab references won't silently lose their
  assignment.
* The attribute is one line and zero runtime cost.

It can be removed in a later cleanup pass once the team is confident every
reference has been migrated.

---

## Verification

In the Editor:

1. Open `Canvas_Gameplay_Settings.prefab`. Confirm:
   * The `GameplaySettings` component on the prefab root shows the
     "Mouse Sensitivity" field with `FloatVariable_Input_MouseSensitivity`
     still assigned.
   * The Hierarchy under the settings panel contains
     `Mouse Sensitivity Label` (not `MousSensitivity Label`).
   * The label reads `Mouse Sensitivity` in Scene/Game view.
2. Open `Canvas_MainMenu_Settings.prefab`. Confirm the same Hierarchy /
   label-text changes.
3. Enter Play Mode in the `MainMenu` scene. Open the Settings panel.
   * The label reads `Mouse Sensitivity`.
   * The slider still drives the look multiplier exactly as it did after
     Pass 1 (centered 0.5 ↔ 2, midpoint baseline).
4. Enter the gameplay scene. Open the in-game Settings panel.
   * Same label text. Same slider behavior. The `GameplaySettings` Apply
     / Revert flow still works (revert-on-close behavior, until Pass 6
     tidies it).

---

## What this pass does **not** fix

* Scroll wheel / trackpad still shares the mouse-sensitivity slider →
  Pass 3.
* No user-facing invert option → Pass 4.
* No drone slider, no reset button → Pass 5.
* Apply/Revert semantics on closing the panel → Pass 6.