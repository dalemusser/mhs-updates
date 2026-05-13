# Session State — Where We Left Off

Snapshot taken end of session **2026-05-12**. Pick up here at the next
session.

Working snapshot of Mission HydroSci 2.0 (Unity 6 / `6000.0.44f1`, URP
16.0.4). Changes will be folded back into the Perforce mainline later.

---

## 1. Pass status (sensitivity / invert series)

| Pass | Status | Notes |
|---|---|---|
| Docs setup | ✅ done | `docs/01-…` plan written, expanded to capture all design decisions |
| **Pass 1** — centered-multiplier mouse sensitivity slider | ✅ **done** | `_min=0.5, _max=2, _value=1, _defaultValue=1`. BindSlider `_useMaxValueFromVariable=1`. Slider `m_MinValue=0.5, m_MaxValue=2, m_Value=1`. Also discovered + documented legacy `_minMax: Vector2` issue in 5 other SOAP assets (audit in Pass 1 doc). |
| **Pass 2** — Sensitiviy/MousSensitivity misspellings | ✅ **done** | Field `_mouseSensitivity` renamed with `[FormerlySerializedAs]`. Both prefabs updated (label GameObject name + text). |
| Pass 3 — Split mouse delta from scroll; add Trackpad/Scroll slider | ⏳ pending | Adds 2nd action `LookScroll`, 2nd `FloatVariable_Input_ScrollLookSensitivity`, 2nd slider on settings panel. Character only — drone in Pass 5. |
| Pass 4 — Character Invert X + Invert Y | ⏳ pending | Remove `InvertVector2` processors from `<Mouse>/delta` and `<Mouse>/scroll`, add bool variables + toggles, ±1 multiplier in consumer code. |
| Pass 5 — Drone sliders + invert + Reset-to-defaults | ⏳ pending | 2 sliders + 2 inverts for drone; collapse 4 axis-invert bools into 2 user-facing bools; Reset buttons on both Character and Drone sections; refactor `CharacterControllerAdapter_Drone.cs` so sensitivity values multiply a tuned base instead of overwriting Cinemachine `m_MaxSpeed`. |
| Pass 6 — Apply/Revert tidy in `GameplaySettings.cs` | ⏳ pending | Snapshot on `OnEnable`; real `Apply()` updates snapshot; `OnDisable` reverts only when not committed. |

**Agreed design decisions** (locked in during this session):

* Slider range: **0.5 ↔ 2** (full-left = half speed, midpoint = baseline, full-right = double).
* Slider count: **two per controllable** — Mouse + Scroll for Character, Mouse + Scroll for Drone. Driven by the iPad vs. MacBook trackpad-scroll divergence the user observed in person; a single calibration constant cannot satisfy both.
* Invert: **per-axis (X, Y) per controllable**, applied uniformly to both input devices on that controllable. Collapse the four existing drone FP/TP axis-invert bools into two user-facing bools (FP+TP driven from same toggle in pairs).
* Defaults: match current observed direction so existing users don't see a sudden flip on first launch after deploy.
* **Reset-to-defaults**: one button per section (Character, Drone). User noted this is important — once a user moves the slider, they currently can't find the default again.
* Invert ownership moves out of Input action processors into consumer code, unifying with how the drone already handles invert.

---

## 2. Docs inventory (everything in `docs/`)

| File | What it is |
|---|---|
| `00-session-state.md` | **This file.** Session-bookmark for cross-day pickup. |
| `01-sensitivity-current-state-and-plan.md` | The master plan. Current implementation, issues, agreed design, Pass-by-Pass plan, verification plan. Updated through this session with all decisions. |
| `02-pass1-centered-mouse-sensitivity.md` | Pass 1 change record. Issue, fix, exact YAML diffs, Editor-equivalent steps, side-effects, verification. Includes follow-up "audit other clamped `FloatVariable` assets" section with five legacy-format assets found (camera FloatVariables + one IntVariable). |
| `03-pass2-misspellings.md` | Pass 2 change record. Issue, fix, exact YAML diffs, why we kept `[FormerlySerializedAs]` even after updating the prefab YAML key. |
| `build-automation-postprocess-robustness.md` | Standalone — for the build-automation maintainer. Fixes the `OnPostprocessBuild` callback in `BuildFromBuildProfiles.cs` so post-process reporting failures don't flip the build to Failed, no matter how it was launched. Also covers the `File.GetLastWriteTime` FILETIME-zero sentinel that produced `1600 -31-12-16-07-00` filenames. |
| `webgl-template-build-and-run-fix.md` | Standalone — for whoever owns the WebGL templates. Adds a `{{{ LOADER_FILENAME }}}` fallback to `MHS Bridge` + `MHS Bridge - iPad Test` index.html so `Build and Run` works locally without breaking production multi-build hosting. Includes a Pitfalls section about Unity's preprocessor parsing triple-brace patterns globally (caught us once). |
| `settings-state-coupling-via-pixelcrushers.md` | Standalone — for the original dev who implemented `SettingsSaver` / `SettingsSaveManager` / `DatabaseSaver`. Captures the structural issue we discovered: `SettingsSaver` inherits from `PixelCrushers.Saver` which causes user settings to be saved/loaded through `/api/state/*` in addition to the dedicated `/api/settings/*`. Three fix options laid out, Option A recommended (decouple in `OnEnable`/`OnDisable`). Pending the user's conversation with the original dev before we apply. |
| `settings-400-error-and-coupling-issue.txt` | User-archived copy of an earlier analysis in chat. Now superseded by `00-session-state.md` (this file) and `settings-state-coupling-via-pixelcrushers.md`. Safe to leave or delete. |

---

## 3. Open issues — exact status

### Issue A — `/api/settings/load` returns 400 on first call

**Fixed 2026-05-12 — see `docs/04-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md`.**

Empty-`playerId` guard added to both `SettingsSaveManager.LoadSettings`
and `SaveSettings`. Pending in-build verification (rebuild → check
browser console for the new `[SettingsSaveManager] LoadSettings skipped`
warning on the first call, no 400 in Network tab).

*(Original analysis retained below for reference.)*

`CharacterCustomization_DataController.OnSceneLoaded`
(`Assets/Scripts/Character Customization/CharacterCustomization_DataController.cs:81`)
raises `loadSettings.Raise()` on every non-customization scene load. This
fires when the first gameplay scene loads — **before** `AuthManager` has
run its identity check and populated `playerId.Value`. The
`SettingsSaveManager.LoadSettings` coroutine then runs with `playerId=""`,
sends `{"user_id":"","game":"mhs"}` to the server, which returns 400
"Missing required fields" from the `LoadHandler`
(`stratasave/internal/app/features/settingsapi/handler.go:167-170`).

After `AuthManager.OnUserIdentityFetched` runs moments later, it sets
`playerId.Value` and raises `loadSettings.Raise()` again. The second call
succeeds with 200 and returns the persisted settings correctly.

**Confirmed by diagnostics in last test:**

```
[DiagSettingsLoad] About to build request. … playerId='' (len=0), game='mhs'
[DiagSettingsLoad] Request body JSON: {"user_id":"","game":"mhs"}
[DiagSettingsLoad] Response: result=ProtocolError, responseCode=400,
                   body='{"error":"Missing required fields"}'

[DiagSettingsLoad] About to build request. … playerId='mhs_developer' (len=13), game='mhs'
[DiagSettingsLoad] Request body JSON: {"user_id":"mhs_developer","game":"mhs"}
[DiagSettingsLoad] Response: result=Success, responseCode=200,
                   body='{…,"MouseSensitivity":1.2033472061157227,…}'
```

**Proposed fix** (small, safe, ready to apply):

Add an empty-playerId guard at the top of
`SettingsSaveManager.LoadSettings` (and symmetrically `SaveSettings`). If
`playerId == null || string.IsNullOrEmpty(playerId.Value)`, log a warning
and return without POSTing. This is defensive — no matter who raises
`OnLoadSettings` or `OnSaveSettings` prematurely, we never send an
invalid request.

Alternative more-architectural fix: change
`CharacterCustomization_DataController.OnSceneLoaded` to not raise
`loadSettings` until auth has completed (or only raise from `AuthManager`).
But the defensive guard is simpler and safer.

### Issue B — Settings overwritten by state-load (structural coupling)

**Fixed 2026-05-12 — see `docs/04-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md`.**

`SettingsSaver.OnEnable` and `OnDisable` overridden to skip the
PixelCrushers `base.OnEnable()` / `base.OnDisable()` calls — the
saver is no longer registered with `SaveSystem`, so settings data
no longer leaks into the `/api/state/*` path. Pending in-build
verification (rebuild → confirm `[DiagPass1] GameplaySettings.Awake`
shows the value from `/api/settings/load`, not the clamped legacy
value; confirm `/api/state/save` POST body no longer contains a
`SettingsSaver` sub-entry).

*(Original analysis retained below for reference.)*

See `settings-state-coupling-via-pixelcrushers.md` for the full
write-up.

**Confirmed by diagnostics in last test:**

- `/api/settings/load` correctly returned `MouseSensitivity: 1.2033`.
- `[DiagPass1] GameplaySettings.Awake — mouseSensitivity.Value = 2` —
  variable was overwritten between the settings-load and the panel open.
- This is the PixelCrushers `SaveSystem.ApplySavedGameData` call writing
  back the stale settings value via `SettingsSaver.ApplyData` (because
  `SettingsSaver` inherits from `PixelCrushers.Saver` and registers with
  `SaveSystem` in its base `OnEnable`).

**Proposed fix** (in the standalone doc): Option A — override
`SettingsSaver.OnEnable` and `OnDisable` to skip
`base.OnEnable()`/`base.OnDisable()` so PixelCrushers doesn't pick it up.
Settings then flow exclusively through `SettingsSaveManager` ⇄
`/api/settings/*`.

User wants to discuss with the original implementer before applying.
Hold for now.

### Issue C — Build automation post-process reporting

**Fixed and documented.** See `build-automation-postprocess-robustness.md`.

Edits already applied in
`Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs`.

### Issue D — `MHS Bridge` WebGL template doesn't work with Build-and-Run

**Fixed and documented.** See `webgl-template-build-and-run-fix.md`.

Edits applied to both templates:

- `Assets/WebGLTemplates/MHS Bridge/index.html`
- `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html`

---

## 4. Temporary diagnostic code currently in the project

These are intentionally still in place. Remove during cleanup before
final hand-off to Perforce — they're labeled with `TEMP DIAGNOSTIC` and
prefixed in their logs.

| File | What's there | Why it's still there |
|---|---|---|
| `Assets/Scripts/Settings/GameplaySettings.cs` | `[DiagPass1]` `Debug.Log` in `Awake`, `OnEnable`, `Start`, `OnDisable`, `RevertGameplayChanges`, `ApplyGameplayChanges` | Captures the mouse-sensitivity variable's actual runtime value at every panel-lifecycle stage. Confirmed in last test that the variable is at 2 before the panel even opens — evidence of the state-coupling overwrite. Keep until Issue B is fixed and verified clean. |
| `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs` | `[DiagSettingsLoad]` `Debug.Log` before each request and after each response in `LoadSettingsCoroutine` | Captures URL, playerId, game, request body, response status, response body. Caught the 400 root cause this session. Keep until Issue A is fixed and verified clean. |

---

## 5. What to do next session (in order)

1. **Verify Issue A + Issue B fixes** with a build cycle. Per the
   verification section of
   `docs/04-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md`:
   * First `LoadSettings` should be skipped with the new warning,
     not produce a 400.
   * `[DiagPass1] GameplaySettings.Awake` should show the value from
     `/api/settings/load`, not the clamped legacy `2`.
   * `/api/state/save` POST body should no longer contain a
     `SettingsSaver` sub-entry.
2. **Clean up diagnostic logs** once both verifications pass. Remove
   all `[DiagPass1]` calls in `GameplaySettings.cs` (revert to its
   post-Pass-2 state) and `[DiagSettingsLoad]` calls in
   `SettingsSaveManager.LoadSettingsCoroutine`. Keep the new
   `[SettingsSaveManager]` guard warnings — they're production-useful.
3. **Begin Pass 3** — split mouse delta from scrollwheel, add the
   Trackpad/Scroll sensitivity slider for the Character. Pattern set by
   Pass 1; we have a known-good template to clone.
4. Continue through Passes 4, 5, 6 in order. Each pass gets a per-pass
   doc; rebuild + verify between passes.

---

## 6. Important context for whoever picks this up

- **The user is not the original implementer.** They are facilitating a
  set of changes that will be folded back into the active Perforce
  mainline by another dev. All docs in `docs/` are written for that
  other dev's benefit — they describe what was found, what was changed,
  why, and how to apply equivalent changes upstream.
- **The user builds and tests after each pass** to verify before moving
  on. Don't batch-apply multiple passes without giving them a chance to
  test in between.
- **`mhs_developer` is the localhost dev identity** (set by the MHS
  Bridge `loadLocalDefaults` in `Assets/WebGLTemplates/MHS Bridge/index.html`).
  Its server-side state save and settings save are populated and stale
  — useful for repro of the coupling issue, not useful for verifying
  default new-user experience. New-user verification should be done via
  the production deployment with a fresh account.
- **The agreed design (4 sliders, per-axis invert, reset buttons, 0.5–2
  range)** was settled after the user observed that iPad trackpad
  two-finger swipe and MacBook trackpad two-finger swipe diverge enough
  that a single `ScaleVector2` calibration constant can't satisfy both.
  That observation is what justified the four-slider design over the
  simpler one-slider-per-controllable approach. Don't second-guess that
  decision without revisiting the iPad/MacBook calibration question.
- **Don't touch the `/api/state/*` path on the Unity side.** The user
  was explicit: state is for game logging / progress and should not
  involve settings. The fact that it currently does is what Issue B and
  `settings-state-coupling-via-pixelcrushers.md` are about.
