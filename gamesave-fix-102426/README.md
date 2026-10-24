# The game's saving — review and fix

**Date opened:** 2026-10-24
**Status:** INVESTIGATION PACKAGE, nothing changed in the game yet. The brief (`00-plan.md`) holds
what is already known about the save code from the logging work and the bridge retrospective, the
code map, the defects to look for (the logger's defects, one by one, applied to saving), how to
gather the production evidence, how to reproduce on the dev site, the rules the fix must follow,
and the hand-off format. The next session does the analysis, the fix, the proof, and the hand-off.

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
| `00-plan.md` | The brief: what is known, the code map, the defects to check, evidence to gather, reproduction on the dev site, fix rules, testing, delivery, open questions |

The next session adds `01-findings.md` (the evidence and each defect confirmed or cleared),
`Game-Code/` (drop-ins mirroring `Assets/Scripts/`), `03-for-the-game-dev.md` (the page for the
developer applying the fix), `tools/` (queries, tests, browser recipes), and, if the service side
changes, a note on the `stratasave` change.

## Summary (to be confirmed by the next session)

The game saves through one HTTP request per checkpoint with no timeout and no retry: a failed save
is a toast and the progress since the previous checkpoint is lost on the next reload. A failed
*load* at launch is treated the same as "no save" and starts the unit over, and the unit transition
waits on the save flag with no cap. The logger had exactly this family of defects before its
2026-09-30 fix (per-request state, no retry, no identity wait, no timeout, an uncapped transition
gate, unguarded JSON parsing), which is why saving is next.
