# Build Automation — Post-Process Robustness Fix

Standalone document. Not part of the sensitivity/invert pass series — kept
here for distribution alongside the rest of the handoff docs.

Audience: the developer who owns `BuildFromBuildProfiles.cs` and the wider
build-automation tooling.

---

## What went wrong (specific symptoms)

When attempting a WebGL build from this snapshot — without launching it
through one of the project's `Build → …` menu items — the Editor reported
the build as Failed with two errors and a trailing exception, all sourced
in `OnPostprocessBuild`:

```
An error occurred while writing to CSV:
Builds/WebGLBuild/BuildReport_1600 -31-12-16-07-00_SourceAssets.csv
Could not find a part of the path
  "/Users/.../MHS 2.0/Builds/WebGLBuild/BuildReport_1600 -31-12-16-07-00_SourceAssets.csv".
…
DirectoryNotFoundException: Could not find a part of the path
  "/Users/.../MHS 2.0/Builds/WebGLBuild/BuildReport_1600 -31-12-16-07-00.txt".
…
Error building Player: 2 errors
Build completed with a result of 'Failed' in 7 seconds
```

The errors came from
`Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs:407` and
`:425` — the post-process callback writing the CSV asset report and copying
the text-format report next to the build output.

Three things were going wrong simultaneously:

1. **Static `_buildPath` was empty.** `_buildPath` is only assigned inside
   `BuildFromProfile()` (line 243). Any build launched through the
   standard Unity UI (`File → Build`, `File → Build Settings → Build`, a
   Build Profile's Build button) bypasses that method, so the callback
   runs with `_buildPath = null/empty` from the start of the session — or
   stale from a previous build pointing somewhere that no longer applies.
   The fallback on line 474 set `_buildPath = BuildPathBaseDirectory`
   (`"Builds/WebGLBuild"`), a path the build pipeline never actually wrote
   to (the build pipeline's output went somewhere else, or the build
   produced no output).
2. **Target directory did not exist.** Whether `_buildPath` was the
   pipeline's real output path or the fallback, the post-process code did
   not check that the directory existed before calling `SaveAssetsToCsv`
   or `File.Copy`. On a clean checkout, or for a build profile pointed at
   a fresh path, the directory hadn't been created yet.
3. **Filename derived from a missing file's last-write time.** The
   timestamp portion of the report filename came from
   `File.GetLastWriteTime("Library/LastBuild.buildreport")`. On a fresh
   checkout that file does not exist, so `GetLastWriteTime` returns
   Windows' FILETIME-zero sentinel (`1601-01-01 UTC`). On macOS that
   converts to local time using the *historical* Local Mean Time offset
   for the system's timezone — producing `1600-12-31 16:07:00` for SF,
   which serialized via the format string `"yyyy -dd-MM-HH-mm-ss"`
   becomes the cosmetic-but-noisy filename
   `BuildReport_<changelist>1600 -31-12-16-07-00.{buildreport|txt}`.

The combination meant: post-process callback throws a
`DirectoryNotFoundException`, Unity treats that as a build error,
overall build status flips to Failed even when the build pipeline itself
produced (or could have produced) usable output.

The `External Code Editor application path does not exist
(/Applications/Visual Studio.app)!` message that often appears alongside
this is **unrelated** — it's a separate editor-preferences problem (Visual
Studio for Mac is discontinued; set External Script Editor to Rider, VS
Code, or any installed editor).

---

## Root design issues

Three design choices made the callback fragile:

1. **Implicit shared state.** A static `_buildPath` field, set only by one
   code path (`BuildFromProfile`), read by another (the post-process
   callback). The post-process callback fires for *every* build regardless
   of how it was launched — anything that depends on session-scoped static
   state will eventually be invoked when that state hasn't been
   populated.
2. **Side effects without precondition checks.** `File.Copy` and
   `SaveAssetsToCsv` both throw if the destination directory is missing.
   The original code didn't validate.
3. **`IPostprocessBuildWithReport` exceptions become build errors.**
   Unity counts any exception thrown by a `IPostprocessBuildWithReport`
   implementation as a build error and flips the overall result to
   Failed. A reporting failure that has nothing to do with whether the
   produced binary is correct should not propagate to that level.

---

## The fix applied here

Two targeted edits in
`Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs`. Both
preserve the existing public API (`OnPostprocessBuild` and the static
`ExportBuildReportCSV`) and the existing return-string contract used by
callers.

### 1. `OnPostprocessBuild(BuildReport report)`

Changes, in order:

* **Source `_buildPath` from `report.summary.outputPath` first.** This is
  the canonical output path the build pipeline actually used, populated by
  Unity regardless of how the build was launched. Fall back to the
  existing static `_buildPath` only if the report's `outputPath` is
  empty, and fall back further to `BuildPathBaseDirectory` only if both
  are empty.
* **Source the timestamp from `report.summary.buildEndedAt` first.** Fall
  back to `File.GetLastWriteTime(k_LastBuildReportFileName)` only if the
  file actually exists, and to `DateTime.Now` otherwise. This eliminates
  the FILETIME-zero / `1600-12-31` filenames.
* **Skip the build-folder copy if the build output directory is
  missing.** Log a warning and return, instead of letting `File.Copy`
  throw.
* **Wrap the body in `try { … } catch (Exception ex) { LogWarning(...); }`.**
  Anything unexpected in post-processing becomes a warning, not a build
  error. Use `LogWarning`, not `LogError` — `LogError` can flip the build
  result; `LogWarning` does not.

The reformatted method is included below. The diff is structural rather
than mechanical (added try/catch wrapping, new conditional blocks), so the
full replacement is the cleanest way to review.

```csharp
public void OnPostprocessBuild(BuildReport report)
{
    try
    {
        // Prefer the actual output path the build pipeline used. This is
        // populated regardless of how the build was launched (project menu,
        // File > Build, build-profile UI). The static _buildPath is only
        // set when launching via BuildFromProfile().
        if (report != null && !string.IsNullOrEmpty(report.summary.outputPath))
        {
            _buildPath = report.summary.outputPath;
        }
        else if (string.IsNullOrEmpty(_buildPath))
        {
            _buildPath = BuildPathBaseDirectory;
        }

        //Create Build Report Directory if it doesn't exist
        if (!Directory.Exists(k_BuildReportDirectory))
        {
            Directory.CreateDirectory(k_BuildReportDirectory);
        }

        // Use the report's own timestamp when available. Falling back to
        // File.GetLastWriteTime on a file that may not exist yields a
        // sentinel (FILETIME 0 -> 1600-12-31 local time on macOS) and
        // produces filenames like "BuildReport_1600 -31-12-16-07-00".
        var date = DateTime.Now;
        if (report != null && report.summary.buildEndedAt != default)
        {
            date = report.summary.buildEndedAt;
        }
        else if (File.Exists(k_LastBuildReportFileName))
        {
            date = File.GetLastWriteTime(k_LastBuildReportFileName);
        }

        var reportFileName = "BuildReport_" + GetChangeListNumber() + date.ToString("yyyy -dd-MM-HH-mm-ss") + ".buildreport";
        var textFileName = "BuildReport_" + GetChangeListNumber() + date.ToString("yyyy -dd-MM-HH-mm-ss") + ".txt";

        //Export the assets in this buld report as CSV
        string reportCSV = ExportBuildReportCSV(report, reportFileName);

        if (string.IsNullOrEmpty(reportCSV))
        {
            Debug.Log($"Export complete at {k_BuildReportDirectory}/{reportFileName}");
        }
        else
        {
            Debug.LogError(reportCSV);
        }

        //Write relevent parts of Build Report to a text file.  Place in both the Build Report Directory
        //and the folder the build files are in.

        string buildReportTextFile = WriteBuildReportToTextFile(report, k_BuildReportDirectory);

        if (string.IsNullOrEmpty(buildReportTextFile))
        {
            return;
        }

        // Make sure the build output directory exists before copying. If
        // the build didn't produce output (e.g. early failure) or the
        // path is unexpected, skip rather than throw.
        if (!Directory.Exists(_buildPath))
        {
            Debug.LogWarning($"Build output directory '{_buildPath}' does not exist; skipping copy of build report text file to the build folder.");
            return;
        }

        var buildFolderDestination = Path.Combine(_buildPath, textFileName);

        if (!File.Exists(buildFolderDestination))
        {
            File.Copy(buildReportTextFile, buildFolderDestination, true);
        }
    }
    catch (Exception ex)
    {
        // LogWarning, not LogError: post-process reporting failures must
        // never flip an otherwise-successful build to "Failed".
        Debug.LogWarning($"OnPostprocessBuild error (non-fatal): {ex}");
    }
}
```

### 2. `ExportBuildReportCSV(BuildReport buildReport, string reportFileName)`

* **Null-guard the `BuildReport` argument** and return a non-empty error
  string (matches the function's existing contract: empty string =
  success, non-empty = error message).
* **Improved `_buildPath` fallback** — prefer `buildReport.summary.outputPath`
  over the hardcoded `BuildPathBaseDirectory`.
* **Create the destination directory** before calling
  `SaveAssetsToCsv` so the write doesn't throw.
* **Wrap in `try/catch`** that returns the exception message as the error
  string. Caller already handles non-empty returns with `Debug.LogError`,
  so behavior is preserved.

```csharp
public static string ExportBuildReportCSV(BuildReport buildReport, string reportFileName)
{
    try
    {
        if (buildReport == null)
            return "ExportBuildReportCSV: buildReport is null; skipping.";

        //Create Content Analysis
        ContentAnalysis contentAnalysis = new ContentAnalysis(buildReport, 10000, true);
        Debug.Log($"Build Path is {_buildPath}");
        if (string.IsNullOrEmpty(_buildPath))
        {
            _buildPath = !string.IsNullOrEmpty(buildReport.summary.outputPath)
                ? buildReport.summary.outputPath
                : BuildPathBaseDirectory;
            Debug.Log($"Build path adjusted to {_buildPath}");
        }

        // Ensure destination directory exists before SaveAssetsToCsv writes.
        if (!Directory.Exists(_buildPath))
        {
            Directory.CreateDirectory(_buildPath);
        }

        string exportPath = $"{_buildPath}/{reportFileName}";
        exportPath = Path.ChangeExtension(exportPath, null) + "_SourceAssets.csv";
        return contentAnalysis.SaveAssetsToCsv(exportPath);
    }
    catch (Exception ex)
    {
        return $"ExportBuildReportCSV failed: {ex.Message}";
    }
}
```

### Behavior after the fix

| Scenario | Before | After |
|---|---|---|
| Build launched via `Build → Build Unit X from Profile` menu | Worked (when target dir already existed) | Works |
| Build launched via `File → Build` or Build Profile UI | Failed: `_buildPath` empty, fallback dir missing | Works: uses `report.summary.outputPath` |
| Fresh checkout, no `Library/LastBuild.buildreport` yet | Filenames like `1600 -31-12-16-07-00` | Filenames use `report.summary.buildEndedAt` or `DateTime.Now` |
| Build pipeline failure before producing output | Post-process throws, build flagged Failed | Post-process logs warning, build result reflects pipeline outcome only |
| Any other unexpected reporting failure | Build flagged Failed | Build proceeds, warning logged |

---

## What was not changed (deliberately)

* **The date format string `"yyyy -dd-MM-HH-mm-ss"`.** It includes a
  leading space and orders day before month. Both are cosmetic
  preferences; behavior-changing edits are out of scope here. If the team
  wants `yyyy-MM-dd-HH-mm-ss` instead, it's a one-line change.
* **`SaveLastBuildReportAsCSV` menu item** (`BuildFromBuildProfiles.cs:438`).
  It also relies on `File.GetLastWriteTime(k_LastBuildReportFileName)` and
  has a similar fragility, but it's a manual menu item, not a hot path —
  flagged for follow-up rather than fixed here.
* **`WriteBuildReportToTextFile`** (`BuildFromBuildProfiles.cs:484`).
  Currently uses `DateTime.Now` in the filename and writes only to
  `k_BuildReportDirectory`, which `OnPostprocessBuild` creates upfront, so
  no missing-directory issue at the call site we touched. Could still
  benefit from a null-guard and try/catch for safety.
* **`BuildFromProfile`'s `_buildPath` assignment.** Left untouched — the
  pre-existing happy-path behavior is unchanged. The fix is purely
  defensive layering for the cases where that path isn't taken.

---

## General patterns to apply in other build-automation code

These came up specifically in this script, but apply across any
post-process pipeline a build-automation maintainer is likely to write.

1. **Treat `IPostprocessBuildWithReport.OnPostprocessBuild` as
   non-critical.** Wrap it in `try/catch`. Use `LogWarning` for
   non-actionable failures so the build result reflects only the build
   pipeline's actual outcome. The user cares whether the binary is good;
   they do not care whether the report CSV got written.

2. **Read paths from the `BuildReport` you were handed, not from static
   shared state.** `report.summary.outputPath` is always populated;
   `report.summary.buildEndedAt` is always populated. Static fields set
   elsewhere may or may not be — depending on whether that "elsewhere"
   ran in this session.

3. **`File.GetLastWriteTime` on a missing file returns a sentinel, not an
   exception.** On Windows that's `1601-01-01 UTC`; on macOS it converts
   to local time via the historical LMT offset, often surfacing as
   `1600-12-31 16:07:something` for US west-coast timezones. Always
   `File.Exists` first, or use the actual event time the report provides.

4. **`File.Copy` and `StreamWriter` throw `DirectoryNotFoundException` if
   the parent directory is missing.** `Directory.CreateDirectory` is
   idempotent; call it before any write where the parent might not exist.

5. **Avoid coupling the build script to one launch method.** If state
   matters to the post-process step, derive it from the `BuildReport` or
   `BuildPlayerOptions` the pipeline gives you, not from a static field
   only one menu item happens to populate. The post-process callback
   fires for every build, including ones launched by automation, IDE
   integration, or Unity's stock UI.

6. **`Debug.LogError` from a post-process callback can cause the overall
   build to be marked Failed.** Reserve it for genuine errors the user
   needs to act on; use `LogWarning` for diagnostic noise so the
   user-visible build status reflects the pipeline's actual result.

---

## Verification

In the Editor (this snapshot, after the fix):

1. With no `Builds/WebGLBuild/` directory and no `Library/LastBuild.buildreport`,
   start a build via any launch method:
   * `Build → Build Unit 2 from Profile` (project menu — routes through
     `BuildFromProfile`).
   * `File → Build` (Unity standard UI — bypasses `BuildFromProfile`).
   * A Build Profile's Inspector "Build" button.
2. Expected: the build either completes successfully or fails based on the
   build pipeline's own outcome. The post-process step:
   * Locates the actual output directory via `report.summary.outputPath`.
   * Creates `Assets/BuildReports/` if missing.
   * Writes the CSV and TXT report files with timestamps derived from
     `report.summary.buildEndedAt` (no `1600-…` filenames).
   * Copies the TXT next to the build output if the output directory
     exists; warns and skips if it doesn't.
3. If anything goes wrong inside the post-process step, a warning is
   logged and the build result is *not* flipped to Failed.

---

## Distribution / Perforce notes

* The change touches a single file:
  `Assets/Scripts/Editor/Build Automation/BuildFromBuildProfiles.cs`.
* Two methods replaced in their entirety: `OnPostprocessBuild` and
  `ExportBuildReportCSV`. No new types, no new dependencies; uses
  `System`, `System.IO`, `UnityEditor.Build.Reporting` which are already
  imported.
* The function signatures and string-return contract of
  `ExportBuildReportCSV` are unchanged, so any other callers (currently
  just `OnPostprocessBuild` and the menu item `SaveLastBuildReportAsCSV`)
  keep working as before.

When applying the same change in the active Perforce project, the diff is
a clean replacement of those two methods — no surrounding code needs to
change. If your active branch has further changes inside these methods
(e.g. extra report types being written), apply the same five patterns
listed above to those code paths too:

* Source paths from the `BuildReport`, not statics.
* Guard against null `BuildReport`.
* `Directory.CreateDirectory` before any write.
* `File.Exists` before `File.GetLastWriteTime`.
* Wrap in `try/catch` with `LogWarning` so reporting failures don't fail
  the build.
