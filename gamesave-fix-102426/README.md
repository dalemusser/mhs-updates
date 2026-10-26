# The game's saving — review and fix

**Date opened:** 2026-10-24. **Fix written, verified in the editor and on the dev site:** 2026-10-24 (version 1.0; 1.1 of 2026-10-25 made the `GameLogger.cs` change a required file of the bundle; 1.2 of 2026-10-25 applies the findings of a second review, `04-review-102526.md`, and takes the developer key out of the game's source; 1.3 of 2026-10-26 applies the findings of a review of 1.2, §7 of the same document). **Test builds on the dev site:** collections `20261024-savefix1-SaveFixTest` (unit1 v2.9.5), `20261024-savefix2-SaveFixTest` (unit1 v2.9.6, version 1.0), `20261025-savefix3-SaveFixTest` (unit1 v2.9.7, version 1.2) and `20261026-savefix4-SaveFixTest` (unit1 v2.9.8, version 1.3, the delivered files), not active for students.
**Start with `03-for-the-game-dev.md`**: Part 1 is what to do (the files, the developer config file, build and
hand-over); Part 2 is the supporting information (checks, what students see, what each change does). `01-findings.md`
holds the production evidence and each defect's verdict; `00-plan.md` is the brief the work
followed, with Dale's decisions in §9.

## Start here (new session)

Working directory for the session: the workspace that holds `mhs-updates`, `stratahub`,
`stratasave` and `mhs2` (so the shared instructions and memory apply). Then:

1. Read this file, then `00-plan.md`. Background worth a skim: `../gamelogger-fix-093026/03-for-the-game-dev.md`
   (what the logger fix did; the save fix will reuse its patterns) and
   `../slider-and-save-changes-051226/background-settings-state-coupling.md` (how settings and
   state saves were once coupled).
2. The Unity project copy to work in:
   `mhs2/20260928_CH12449_MHS 2.0 modified - logging fix v1.3/MHS 2.0` (Unity 6000.0.74f1,
   installed in Unity Hub, warm Library). It carries the logging fix v1.3 and the garden box fix of
   2026-10-23; the save code is unchanged from the game team's 2026-09-28 copy (`mhs2/20260928_CH12449_MHS 2.0.zip`
   is the pristine copy for diffs). The game team is building the garden box fix on 2026-10-26;
   diff the save files against their next copy before delivering.
3. Code under review: `Assets/Scripts/Systems/Save Load System/` (`GameSaveManager.cs`,
   `CustomSavers/DatabaseSaver.cs`, `SettingsSaveManager.cs`, the other `CustomSavers/`), the prefab
   `Assets/Prefabs/_Core/SaveSystem/SaveSystem.prefab`, and the save service `stratasave`
   (`internal/app/features/saveapi/`, `settingsapi/`, `ledger/`). The map is in `00-plan.md` §2.
4. Building and testing a WebGL unit on the dev site: `../gamelogger-fix-093026/00-plan.md`
   (test build script `Assets/Editor/LogFixTestBuild.cs`, hosting through StrataHub's MHS Builds
   page, the dev test member, the browser recipe). Editor PlayMode tests against the game code:
   `../unit4-gardenbox-crash-102326/tools/README.md` (the two temporary project settings and the
   Unity gotchas). Nothing reaches students from this work directly.

## What is in this folder

| File | Purpose |
|---|---|
| `03-for-the-game-dev.md` | The page for the developer applying the fix. Part 1 is what to do (replace eight files, diffing first; delete two dead ones; the developer config file; build and hand over). Part 2 is the supporting information: compatibility notes, the checks we run, what students see, what each file fixes, the verification record. |
| `Game-Code/` | The changed files, mirroring `Assets/`: `Scripts/Systems/Save Load System/{GameSaveManager.cs, CustomSavers/DatabaseSaver.cs, SettingsSaveManager.cs}`, `Scripts/Systems/Logging/GameLogger.cs` (supersedes the copy in `../gamelogger-fix-093026/`), `Scripts/Helpers/UI/UICanvasGroupFadeInOut.cs`, `Sandbox/Brian/MHS Bridge Testing/MHSBridge.cs`, and the two `WebGLTemplates/.../index.html`. |
| `mhs-bridge-dev-config.example.json` | The developer config file (identity, service URLs, key) the Editor and a localhost page read from 1.2 on; placeholders only. |
| `04-review-102526.md` | The review of the delivered files before the hand-off: fourteen findings with their resolution in 1.2, caveats for the documentation, what was checked. |
| `01-findings.md` | The evidence (volumes, the service's ledger, what the saves look like, duplicate saves, progress going backwards, failed saves, the old key) and the twelve defects of the brief confirmed, cleared or documented; proposals for the service; notes for the game team. |
| `00-plan.md` | The brief: what was known, the code map, the defects to check, evidence to gather, reproduction, the rules, delivery, and the decisions (§9). |
| `tools/` | The PlayMode tests with the fake save service, how to run them, and the browser recipe for the checks on a build (`tools/README.md`). |
| `queries/` | The read-only mongosh scripts and the journal summariser that produced `data/`. |
| `data/` | Their outputs, ids shortened to their last six characters, hosts and addresses replaced. |

## Summary

The game saved through one HTTP request per checkpoint with no timeout and no retry: a failed save
was a toast and the progress since the previous checkpoint was lost (the service's ledger shows
three such saves in four weeks, each cut off by a page reload; the ones that never reached it are
invisible). A failed *load* at launch was treated as "no save" and started the unit over, and the
next checkpoint wrote the fresh start over the real progress on the server (seen in production:
a student's Unit 4 checkpoint 3 became checkpoint 0 after an evening relaunch at home). The unit
transition waited on the save flag with no cap, the bearer value went to the browser console on
every launch, the response parsing was unguarded, and the old API key was a built-in fallback.

The fix replaces the transport (`GameSaveManager.cs`) and the policy (`DatabaseSaver.cs`),
removes the key fallback from `SettingsSaveManager.cs`, and corrects the unit-transition gate in
the logging fix's `GameLogger.cs` (its v1.3 rule never fired against the loader's two-frame poll,
so with the log host unreachable the transition waited for ever; the saver's own cap cannot help
there, which is why that file is part of this fix): saves and loads are retried for ever with
backoff and a status line (reason, countdown, attempt) in the existing toast; the newest save is
kept on the device and sent at the next launch when it could not go out; a failed load never
starts a unit over (only the service's explicit "no saves" does), and while the service is
unreachable the game plays on from the device copy and switches to the service's save if that
arrives newer; identity and configuration are waited for; requests time out after 30 s; the unit
transition waits at most five seconds; the same checkpoint raised twice sends one request; nothing
secret is printed. The save service is unchanged (proposals in `01-findings.md` §7). A second
review before the hand-off (`04-review-102526.md`) closed three more rollback paths in the outage
handling (a newer save lost while an older one was in flight, a stale answer resumed from a
long-open tab, progress rewound after a device-copy resume), made a page without save settings
play unsaved instead of waiting for ever, and took the developer key out of the Editor defaults
and the page templates (a developer config file replaces it). Forty-eight PlayMode tests against
a fake save service cover the rules; Unit 1 builds of the files were run on the dev site (see the
verification record in `03-for-the-game-dev.md`).

**Reference snapshot.** The files were written against the game team's project copy of
2026-09-28 (changelist 12449, Unity 6000.0.74f1) with the logging fix v1.3 and the garden box fix
applied; the team holds that copy. There is no separate snapshot zip for this bundle.
