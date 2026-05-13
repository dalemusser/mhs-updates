# Build Automation Update — Handoff Package

This folder is an **isolated handoff** of two build-related fixes for
Mission HydroSci 2.0 that came up while attempting to produce a clean
WebGL build during development. Snapshot taken **2026-05-12**.

The two fixes are independent of each other and independent of the
slider/save work in `docs/slider-and-save-changes/`. They are
bundled here because both target the build pipeline and the dev team
should land them together.

---

## Two fixes in this package

### Fix 1 — `OnPostprocessBuild` callback robustness

**Problem:** Builds were failing with errors like:

```
Could not find a part of the path
  ".../Builds/WebGLBuild/BuildReport_1600 -31-12-16-07-00_SourceAssets.csv"
DirectoryNotFoundException: Could not find a part of the path …
Error building Player: 2 errors
Build completed with a result of 'Failed' in 7 seconds
```

…regardless of how the build was launched, on a fresh checkout.

**Cause:** The `IPostprocessBuildWithReport.OnPostprocessBuild` callback
in `BuildFromBuildProfiles.cs` depended on a static `_buildPath` field
that's only populated when the build is started via
`BuildFromProfile()`. When the build is started any other way
(`File → Build`, build-profile UI), `_buildPath` is empty, falls back
to a path that doesn't exist on disk, and `File.Copy` /
`SaveAssetsToCsv` throw `DirectoryNotFoundException`. The
exceptions are then counted as build errors and the overall build is
flagged Failed.

The `1600-12-31` timestamp in the filenames is a separate, related
symptom: `File.GetLastWriteTime` on a missing file returns the
Windows FILETIME-zero sentinel, which on macOS converts to local
time using the historical LMT offset for the system's timezone —
producing `1600-12-31 16:07:xx` for SF.

**Fix:** Rewrote `OnPostprocessBuild` and `ExportBuildReportCSV` to:

* Source `_buildPath` from `report.summary.outputPath` first
  (canonical, always populated by the build pipeline regardless of
  launch method);
* Source the timestamp from `report.summary.buildEndedAt` first,
  falling back to `File.GetLastWriteTime` only if the file exists;
* `Directory.CreateDirectory` before any write;
* Wrap the body in `try/catch` and use `Debug.LogWarning` (not
  `Debug.LogError`) for non-fatal reporting failures, so a reporting
  glitch can never flip an otherwise-successful build to Failed.

Full analysis and exact code:
[`01-build-automation-postprocess-robustness.md`](01-build-automation-postprocess-robustness.md)

### Fix 2 — `MHS Bridge` WebGL template Build-and-Run

**Problem:** Using `Build and Run` from a Build Profile with the
`MHS Bridge` template produced this in the browser:

> **Could not determine build name from URL.**
> Set `UNITY_BUILD_NAME` at the top of the `<script>` section in
> index.html.

…even though the same template works correctly in production
multi-build hosting.

**Cause:** The `MHS Bridge` template's `detectAndLaunch()` routine
deliberately avoids Unity's `{{{ LOADER_FILENAME }}}` build-time
replacement and instead tries to derive the build name from the URL
path segment. That works in production where each unit is hosted at
`/<unitN>/index.html`. Locally, Build-and-Run serves at root (`/`),
the path-segment detection has no folder name to extract, and the
template gives up.

**Fix:** Added a `tryUnityLoaderFilename()` helper to both
`MHS Bridge` templates as a **last-resort fallback**, after the
existing detection chain. The helper uses Unity's
`{{{ LOADER_FILENAME }}}` replacement (which Unity fills in at build
time), guarded against the literal-placeholder case so it's safe for
shared parent pages where the replacement never happens. Existing
production behavior is unchanged — the new branch only fires when
the existing strategies all fail.

Full analysis and exact code (including a Pitfalls section about
Unity's WebGL preprocessor that bit us during this fix):
[`02-webgl-template-build-and-run-fix.md`](02-webgl-template-build-and-run-fix.md)

---

## Files in this package

| File | Purpose |
|---|---|
| `README.md` | This file. Overview of both fixes and how to apply. |
| `01-build-automation-postprocess-robustness.md` | Full doc for Fix 1. Issue, root cause, fix, exact before/after code, before/after behavior table, what was deliberately not changed, six general patterns to apply to similar code, verification steps. |
| `02-webgl-template-build-and-run-fix.md` | Full doc for Fix 2. Same structure as Fix 1, plus a Pitfalls section about the triple-brace placeholder gotcha in Unity's WebGL preprocessor. |
| `BuildFromBuildProfiles.cs` | The full updated file. Drop-in replacement for `Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs` in the active Perforce branch — **but** only if the active branch has not also modified this file. If it has, apply the diffs from doc #01 selectively. |
| `MHS-Bridge-index.html` | The full updated file. Source-of-truth for `Assets/WebGLTemplates/MHS Bridge/index.html`. |
| `MHS-Bridge-iPad-Test-index.html` | The full updated file. Source-of-truth for `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html`. The fix is identical to the regular MHS Bridge variant. |

---

## Files touched (complete list)

| Project path | Changed by |
|---|---|
| `Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs` | Fix 1 |
| `Assets/WebGLTemplates/MHS Bridge/index.html` | Fix 2 |
| `Assets/WebGLTemplates/MHS Bridge - iPad Test/index.html` | Fix 2 |

The corresponding `.meta` files were **not** modified — no GUID
changes, no Unity import-settings changes.

---

## How to apply

The two fixes are independent. Land in either order.

### Option 1 — Drop-in file replacement (fastest if your branch hasn't
modified these files)

1. Compare your active-branch versions to the copies in this folder.
   If your branch matches the **pre-fix** state described in each
   doc, you can copy these three files into place.
2. Open Unity. Watch the console for compile errors. There shouldn't
   be any — the changes don't add new dependencies.
3. Run the Verification steps from each doc.

### Option 2 — Apply diffs manually (recommended if your branch has
also modified these files)

1. For Fix 1: read `01-build-automation-postprocess-robustness.md`
   sections "The fix applied here" → "1. OnPostprocessBuild(…)" and
   "2. ExportBuildReportCSV(…)". Each section has the full method
   replacement code. Apply each method replacement to your active
   `BuildFromBuildProfiles.cs`.
2. For Fix 2: read `02-webgl-template-build-and-run-fix.md` section
   "What was changed". One new helper function
   (`tryUnityLoaderFilename`) added above `detectAndLaunch()`, and
   three `if (tryUnityLoaderFilename()) return;` calls inserted
   inside `detectAndLaunch()` — one per existing error branch.
   Apply the same three insertions in both `MHS Bridge` and
   `MHS Bridge - iPad Test` `index.html` files. Pay attention to the
   **Pitfalls** section: the file must contain exactly one
   `{{{ LOADER_FILENAME }}}` placeholder pattern, and detection of
   the unsubstituted case is done with `indexOf('{')`, not by
   looking for the triple-brace sequence (which would create a
   second placeholder and trip Unity's preprocessor).

### Combined verification (after both fixes applied)

1. From a clean state (no `Builds/` directory present), start a
   build through any path:
   * `Build → Build Unit X from Profile` (project menu).
   * `File → Build` (Unity standard UI).
   * A Build Profile's Inspector "Build" button.
2. **Expected for Fix 1:** the build completes (or fails) based only
   on the build pipeline's actual outcome. The post-process callback
   completes without `DirectoryNotFoundException`. Build reports are
   written to `Assets/BuildReports/` with sensible timestamps (no
   `1600 -31-12-…` filenames). If a build-folder copy needs to happen
   but the folder doesn't exist, you'll see a `LogWarning` instead
   of a `LogError`, and the build status is unaffected.
3. **Expected for Fix 2:** the resulting WebGL build, served by
   Build-and-Run at `http://localhost:<port>/`, loads the Unity
   instance without showing the "Could not determine build name from
   URL" error overlay.

If either of these doesn't hold, the per-doc Verification sections
have narrower targeted checks.

---

## What this package does **not** include

* **Slider / mouse-sensitivity work** — separate handoff in
  `docs/slider-and-save-changes/`. Independent of the build fixes
  here; can be applied in either order.
* **Pass 3 onward of the sensitivity series** — future work, not yet
  done.
* **Anything in `Assets/Sandbox/`** — sandbox/test areas were not
  touched by these fixes.

---

## Notes / things worth knowing

* The `BuildFromBuildProfiles.cs` `OnPostprocessBuild` rewrite
  deliberately leaves the existing date-format string
  `"yyyy -dd-MM-HH-mm-ss"` unchanged. It has a leading space and
  orders day before month — cosmetic, not behavioral. If the team
  wants to clean it up to `"yyyy-MM-dd-HH-mm-ss"`, that's a one-line
  change unrelated to the bug fixed here.
* The `MHS Bridge` template's existing detection chain (meta tag →
  `UNITY_BUILD_NAME` → URL folder name) is **untouched** by Fix 2.
  The new `tryUnityLoaderFilename` branch is strictly a fallback
  added before the final error path. Production behavior is
  unchanged in the cases where the existing chain already resolves.
* The Pitfalls section in doc #02 is worth reading before making
  any future edits to either MHS Bridge `index.html`. Unity's WebGL
  preprocessor parses the triple-brace placeholder pattern globally
  across the file (including JS comments and string literals); the
  rule is "one such pattern per file, ever." The first version of
  Fix 2 violated this and produced a `SyntaxError: Invalid or
  unexpected token` build failure that took some debugging to
  recognize.
