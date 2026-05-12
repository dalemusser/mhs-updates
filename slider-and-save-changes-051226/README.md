# Slider & Save Changes — Handoff Package

This folder is an **isolated handoff** of one coherent set of fixes
for Mission HydroSci 2.0, ready to be applied to the active Perforce
mainline by the dev team. Snapshot taken **2026-05-12**.

The fixes in this package solve three observed problems:

1. **The user-facing Mouse Sensitivity slider in the Settings panel
   does not work as a proper sensitivity control.** The left end
   freezes look entirely; the right end produces erratic high-speed
   look. The label has spelling errors ("Mouse Sensitiviy",
   GameObject "MousSensitivity Label").
2. **The dedicated `/api/settings/load` returns HTTP 400 on the very
   first request of every session.** Browser console shows
   `Failed to load resource: 400 (Bad Request)` against
   `save.adroit.games/api/settings/load`, with response body
   `{"error":"Missing required fields"}`.
3. **Even after settings load successfully, the slider always shows
   the maximum position on every game launch.** A correct value
   (e.g. `MouseSensitivity: 1.2033`) comes back from
   `/api/settings/load`, but by the time the user opens the Settings
   panel, the variable has been overwritten and the slider sits at
   max.

All three are fixed by the changes in this handoff. The package
contains four documents and touches **four files** total in the
project tree (two C# files, one ScriptableObject `.asset`, two
`.prefab` files).

The handoff snapshot has been **verified working** in a local WebGL
build: sensitivity persists correctly across launches; the 400 no
longer fires; the slider initializes at the user's saved value.

---

## Files in this package

| Document | What it covers |
|---|---|
| `README.md` | **This file.** Overview, application order, file-level summary, verification. Start here. |
| `01-pass1-centered-mouse-sensitivity.md` | Fixes #1 (the slider math). Replaces the broken 0..(implicit ~100) raw-scaler behavior with a centered multiplier `0.5 ↔ 2`. Asset and prefab edits only — no code change. Also identifies and audits five other clamped `FloatVariable`/`IntVariable` assets that have the same latent legacy-format issue (clamp configured but not effective at runtime). |
| `02-pass2-misspellings.md` | Fixes the spelling errors discovered alongside #1: `_mouseSensitiviy` → `_mouseSensitivity` (C# field, with `[FormerlySerializedAs]` for prefab compatibility), `Mouse Sensitiviy` → `Mouse Sensitivity` (label text), `MousSensitivity Label` → `Mouse Sensitivity Label` (GameObject name). One C# file plus two `.prefab` files. |
| `03-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md` | Fixes #2 and #3 as a paired change. Adds empty-`playerId` guards to `SettingsSaveManager.LoadSettings` and `SaveSettings` (fixes the 400). Decouples `SettingsSaver` from PixelCrushers `SaveSystem` registration so settings stop being silently saved/restored through the `/api/state/*` path (fixes the overwrite). Two C# files. |
| `background-settings-state-coupling.md` | Background analysis of the coupling problem fixed by #3 above. Not required reading to apply the fix, but recommended for anyone who wants to understand why PixelCrushers `Saver` inheritance caused settings to leak into the state save path. Also lists three fix options (Option A was chosen, Option C is noted as a possible future cleanup). |

---

## How to apply

The four changes are **independent** — they can be applied in any
order, each compiles and runs on its own. Recommended order matches
the numbered docs above (1 → 2 → 3) because that's how they were
landed and verified locally; nothing breaks if you take them in a
different order.

For each numbered doc:

1. Read the **Issue / Symptom** section to confirm the bug exists in
   your branch.
2. Read the **Fix** section to understand the approach.
3. Apply the **Exact edits** section. Each one provides explicit
   before/after snippets for every file touched. Where a YAML edit
   is awkward, an "Editor-equivalent" path is also given.
4. Run the **Verification** at the bottom of the doc to confirm the
   fix landed cleanly.

After all four are applied, run the **Combined verification** below
to confirm the package as a whole works.

---

## Files touched (complete list across all four changes)

| File | Touched by | Nature of change |
|---|---|---|
| `Assets/Data/Scriptables/Input/FloatVariable_Input_MouseSensitivity.asset` | 01 (Pass 1) | YAML field changes: `_min`/`_max` `FloatReference` blocks added (replacing legacy `_minMax: Vector2` which was inert at runtime); `_defaultValue: 1`. |
| `Assets/Prefabs/UI/Canvas_Gameplay_Settings.prefab` | 01 (Pass 1) + 02 (Pass 2) | Slider min/max set to 0.5/2 (component on the `MouseSensitivity` GameObject). BindSlider component flipped to `_useMaxValueFromVariable: 1` and `_maxValue.LocalValue: 2`. Label GameObject renamed to `Mouse Sensitivity Label`. Label TextMeshPro text fixed to `Mouse Sensitivity`. `GameplaySettings` MonoBehaviour's serialized field key renamed from `_mouseSensitiviy` to `_mouseSensitivity`. |
| `Assets/Prefabs/UI/Canvas_MainMenu_Settings.prefab` | 01 (Pass 1) + 02 (Pass 2) | Same Pass 1 changes as the gameplay prefab. Label GameObject renamed and text fixed. (This prefab does **not** carry a `GameplaySettings` MonoBehaviour, so no field-key rename needed here.) |
| `Assets/Scripts/Settings/GameplaySettings.cs` | 02 (Pass 2) | Field renamed `_mouseSensitiviy` → `_mouseSensitivity` with `[FormerlySerializedAs("_mouseSensitiviy")]`. `using UnityEngine.Serialization;` added. Three usages in body updated. |
| `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs` | 03 (Issue A) | Empty-`playerId` guard added at the top of `LoadSettings(Action<bool>)` and `SaveSettings(Action<bool>)`. Returns early with a `Debug.LogWarning` when `playerId.Value` is null/empty. |
| `Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs` | 03 (Issue B) | `OnEnable` and `OnDisable` overrides modified to **skip** the `base.OnEnable()` and `base.OnDisable()` calls, so the saver does not register with `PixelCrushers.SaveSystem`. Method bodies kept (still hook the `sceneLoaded` event). |

No new files created. No file deletions. No third-party packages
modified. Mongo collection schemas unchanged.

---

## Combined verification (after all four changes applied)

In a freshly-built WebGL build:

1. **Open the build.** Browser console should show, on the very first
   settings load attempt:
   ```
   [SettingsSaveManager] LoadSettings skipped: playerId is empty.
   AuthManager will retry on its next identity-fetch tick…
   ```
   And **no** `Failed to load resource: 400` for `/api/settings/load`
   anywhere in the Network tab.

2. **A moment later** (after `AuthManager` populates `playerId`), the
   real `/api/settings/load` POST fires and returns 200 with the
   user's settings JSON.

3. **Open the Settings panel.** The Mouse Sensitivity slider should
   show the value that was returned by `/api/settings/load` — not
   pinned to the right edge (the slider max).

4. **The label** should read `Mouse Sensitivity`, not
   `Mouse Sensitiviy`.

5. **Drag the slider** to a different value, click **Apply**, close
   the panel.

6. **Refresh the page.** After reload and auth, the slider should
   show the value you just applied — it persists across browser
   refresh.

7. **Walk around in-game** until something triggers an `OnSaveGame`
   raise (scrap pickup, quest milestone, scene transition). In
   DevTools → Network → the resulting POST to `/api/state/save`,
   inspect the request body. The `save_data` field should **no
   longer contain** a `SettingsSaver` entry — confirming that game
   state no longer carries settings.

8. **Quit and relaunch.** Slider should still come up at your saved
   value, not at the max.

If any of these fail, the per-doc Verification sections have
narrower targeted checks.

---

## What this package does **not** include

This handoff is scoped to the slider+save fixes only. Other changes
that have been made in the working snapshot but are **not** part of
this package:

* **Build automation post-process robustness** —
  `Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs`
  was hardened against missing build directories and the FILETIME-zero
  timestamp issue. Unrelated to settings/slider. Will be distributed
  separately if/when needed.
* **`MHS Bridge` WebGL template Build-and-Run fix** — both
  `Assets/WebGLTemplates/MHS Bridge/index.html` and
  `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html` got a
  `{{{ LOADER_FILENAME }}}` fallback so they work with Unity's local
  Build-and-Run. Unrelated to settings/slider. Will be distributed
  separately if/when needed.
* **Pass 3 onward of the sensitivity series** — separate
  Mouse vs. Trackpad/Scroll sliders, per-axis Invert, Drone
  sensitivity, Reset-to-defaults. Future work, not in this package.

If you want any of those distributed alongside this set, ask Dale —
they're already documented in the project's main `docs/` folder.

---

## Open follow-up (not blocking handoff)

`SettingsSaver` still inherits from `PixelCrushers.Saver` (we kept the
inheritance and just skipped the base `OnEnable`/`OnDisable`). The
class therefore still has a `key` field, a `saveAcrossSceneChanges`
flag, etc. on its prefab, which are now inert. A clean follow-up
would be to drop the `Saver` inheritance entirely and make
`SettingsSaver` a plain `MonoBehaviour` with the same
`RecordData()` / `ApplyData(string)` methods. Documented as "Option C"
in `background-settings-state-coupling.md`. Not required for the
fixes in this package to work correctly; consider for a future
hygiene pass.

One question worth confirming with the original implementer when
convenient: character customization data
(`CharacterCustomization_DataController`) currently travels inside
`SettingsValues` and therefore now flows exclusively through
`/api/settings/*` after the decoupling. That treats it as a persistent
user preference (correct for most cases). If customization was
intended to be per-playthrough state, that would need a separate
ownership change. The current implementation treats it as a setting,
which is what this package preserves.
