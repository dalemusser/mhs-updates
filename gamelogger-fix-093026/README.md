# gamelogger-fix-093026 — the game's logging fixed (version 1.2, 2026-10-01)

**Start with `03-for-the-game-dev.md`**: which three files to replace, how to build and hand the build back, one CI fix, and the background on what each file fixes.

| File | Purpose |
|---|---|
| `03-for-the-game-dev.md` | The short page for the developer applying the fix. |
| `Game-Code/` | The three changed files, mirroring `Assets/Scripts/`: `Systems/Logging/GameLogger.cs`, `Systems/Logging/LoggingData.cs`, `Systems/Save Load System/SettingsSaveManager.cs`. |
| `01-changes.md` | Each defect, its change, the version 1.1 review fixes, the version 1.2 fields for the log service, and the verification results on the dev site. |
| `00-plan.md` | The brief the work followed (the defects in the source, how to reproduce each, the seven checks a build must pass). |
| `02-logging-redesign-if-starting-over.md` | A design note for a possible version 2 of the logging, including the coordinated client and server changes; nothing implemented. |
| `tools/` | The send-loop simulation, the browser recipe helpers, and the read-only server-side queries used for the checks. |

**Reference snapshot.** The files were written against the game team's project copy of 2026-09-28 (changelist 12449, Unity 6000.0.74f1), which the team holds; there is no separate snapshot zip for this bundle. Test builds of each version were uploaded to the dev site as unit1 v2.8.3 through v2.8.7 (collections named `20260930-logfix…`); none is active for students.
