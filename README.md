# mhs-updates

A repository of update packages for the Mission HydroSci 2.0 Unity
project. Each folder is a self-contained handoff bundle — a set of
fixes or changes that have been made and verified against a working
snapshot of the project, ready for the dev team to apply to the
active Perforce mainline.

Each update folder contains:

* A `README.md` with an overview of the changes and how to apply
  them.
* Numbered per-change documents (`01-…`, `02-…`) describing the
  issue, the root cause, the exact code/asset/prefab edits, and
  verification steps. Each doc is written to be self-contained so
  the dev applying the change does not have to re-derive the
  reasoning.
* Where useful, drop-in copies of the actual updated source files
  alongside the docs — so the applying developer can compare to
  their active branch, or copy directly into place if their branch
  has not also touched the file.

Folder names include a date suffix (`-MMDDYY`) so the sequence of
updates is clear and so the same area can be revisited later
without overwriting prior handoffs.

---

## Updates in this repository

### `build-automation-update-051226/` — 2026-05-12

Two independent fixes to the WebGL build pipeline, bundled because
both came up while attempting to produce a clean WebGL build on a
fresh checkout. **Fix 1** hardens
`Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs`
against a class of `OnPostprocessBuild` callback failures — the
post-process step was crashing with `DirectoryNotFoundException`
because it relied on a static path field that was only populated when
the build was launched a specific way, and was writing report
filenames with bogus `1600-12-31` timestamps caused by
`File.GetLastWriteTime` returning the Windows FILETIME-zero sentinel
on a missing file. These exceptions were being counted as build
errors, flipping otherwise-successful builds to Failed. The fix
sources the build path and timestamp from the `BuildReport` itself
(always populated regardless of launch method), creates the
destination directory if missing, and wraps reporting failures in
`try/catch` with `LogWarning` so they cannot fail an otherwise-good
build. **Fix 2** updates the `MHS Bridge` and `MHS Bridge - iPad Test`
WebGL templates (`index.html` in both) so they work with Unity's
local `Build and Run` in addition to the production multi-build
hosting they were originally designed for. The templates were
showing "Could not determine build name from URL" on local builds
because their detection chain relied on a folder-name URL pattern
that doesn't exist at the local `/` root path. A
`tryUnityLoaderFilename()` fallback was added that uses Unity's
`{{{ LOADER_FILENAME }}}` build-time replacement as a last resort,
guarded against the literal-placeholder case so it's safe for shared
parent pages in production. Required because without these two fixes
the dev team cannot reliably build the project from a fresh
checkout, and the build-automation reporting that the team uses for
asset-size tracking is non-functional.

### `slider-and-save-changes-051226/` — 2026-05-12

A coherent set of four fixes around the Mouse Sensitivity slider on
the Settings panel and the settings save/load path that backs it.
**Fix 1** (Pass 1) corrects the slider's underlying math: the slider
was operating as a raw 0..100 scaler (because the SOAP `BindSlider`
component was overriding the prefab's Inspector-set max at runtime,
and the `FloatVariable_Input_MouseSensitivity.asset` used a legacy
serialization format whose clamp was silently ineffective). The
result was that the left end of the slider produced 0 (look freezes
entirely) and the right end produced erratic high-speed look. The
fix makes the slider a true centered multiplier with range 0.5–2 —
midpoint is baseline, full left is half-speed, full right is
double-speed. Editing both the `FloatVariable` asset (`_min`/`_max`
in the modern `FloatReference` format) and the slider/BindSlider
configuration in both `Canvas_Gameplay_Settings.prefab` and
`Canvas_MainMenu_Settings.prefab`. **Fix 2** (Pass 2) corrects
multiple spelling errors that were visible to the user and present
in code — the C# field was named `_mouseSensitiviy` (missing a `t`),
the slider's label GameObject was named `MousSensitivity Label`
(missing an `e`), and the label text read `Mouse Sensitiviy`.
`[FormerlySerializedAs]` is used on the renamed field so existing
prefab references migrate without losing their assignment. **Fix 3
(Issue A)** adds a defensive empty-`playerId` guard to
`SettingsSaveManager.LoadSettings` and `SaveSettings`. Without it,
the very first `/api/settings/load` of every session returns HTTP
400 because `CharacterCustomization_DataController.OnSceneLoaded`
raises `loadSettings.Raise()` before `AuthManager` has populated
`playerId.Value`, and the server (correctly) rejects a request
with `user_id=""`. The guard stops the guaranteed-to-fail request;
`AuthManager` retries successfully a moment later once auth has
completed. **Fix 4 (Issue B)** decouples `SettingsSaver` from the
PixelCrushers `SaveSystem` registration by overriding `OnEnable` and
`OnDisable` to skip the base class's `RegisterSaver` /
`UnregisterSaver` calls. Before this change, `SettingsSaver`'s
inheritance from `PixelCrushers.Saver` caused settings data
(mouse sensitivity, audio bus levels, character customization) to
be silently saved into and restored from the `/api/state/*`
endpoints alongside game state, in addition to the dedicated
`/api/settings/*` path. The two paths could drift apart, and on
load the state-load path's stale settings copy would overwrite the
fresh settings-load value — causing the slider to display at max on
every launch regardless of what the user had set. Required because
without these four fixes the user-facing slider does not work as a
sensitivity control, contains visible spelling errors, generates a
spurious 400 error on every launch, and never reflects the user's
saved sensitivity across game restarts.

---

## How to consume an update

Open the update folder, start with its `README.md`. The README
describes which problems the update solves, lists every file
touched, and points at the numbered per-change docs for the exact
edits. Apply each numbered change in order (or selectively if your
active branch only needs a subset). Each per-change doc has a
Verification section at the bottom; the update folder's README has
a Combined verification that exercises the whole set.

Updates in this repo are scoped to one coherent set of changes per
folder. Different concerns (e.g. build-pipeline fixes vs.
user-facing UI fixes) ship as separate folders so the dev team can
distribute and apply them independently.
