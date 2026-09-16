# mhs-updates — Context for Claude Code

## What This Repo Does

Repository of vetted, self-contained handoff bundles for Mission HydroSci 2.0 (a Unity WebGL educational game). Each folder packages a coherent set of fixes or changes that have been developed, tested, and verified against a working snapshot of the main project — ready for the development team to apply to the active Perforce mainline.

The repository serves as a formal handoff mechanism: instead of ad-hoc patch files or verbal descriptions, each update is documented with step-by-step change instructions, code snippets, and reference project snapshots so developers can apply fixes with confidence and verify them independently. Updates are scoped to one coherent problem set per folder and dated for sequencing.

## Technology Stack

- **Language(s):** C# (Unity game code), HTML/JavaScript (WebGL templates), YAML (ScriptableObject asset files, prefab files)
- **Framework(s):** Unity 2022+ (WebGL build target), SOAP UI framework (custom), PixelCrushers save system
- **Backend:** MongoDB (for player settings, game state, logs), Stratalog (logging service), Stratahub (companion web dashboard)
- **Deployment:** Unity WebGL builds hosted on `save.adroit.games`, hosted within MHS Bridge iframe pages

## Folder Structure

```
mhs-updates/
├── README.md                                    # Repo-level overview
├── LICENSE
├── .gitignore                                   # Standard Unity ignore rules
├── build-automation-update-051226/              # Dated update package 1
│   ├── README.md                                # Overview of this update
│   ├── 01-build-automation-postprocess-robustness.md
│   ├── 02-webgl-template-build-and-run-fix.md
│   ├── BuildFromBuildProfiles.cs                # Updated source file (drop-in)
│   ├── MHS-Bridge-index.html
│   └── MHS-Bridge-iPad-Test-index.html.obsolete
├── slider-and-save-changes-051226/              # Dated update package 2
│   ├── README.md
│   ├── 01-pass1-centered-mouse-sensitivity.md
│   ├── 02-pass2-misspellings.md
│   ├── 03-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md
│   ├── background-settings-state-coupling.md   # Root-cause analysis
│   ├── player-settings-data.png
│   ├── player-state-data-with-settings.png
│   └── [no source files — YAML/prefab edits described in docs]
├── mhsbridge-userid-cleanup-052626/             # Dated update package 3
│   ├── README.md
│   ├── DIRECTIONS.md                            # Step-by-step install guide
│   ├── WHATS-CHANGED.md                         # Line-level change summary
│   ├── DEAD-CODE.md
│   ├── RECOMMENDATIONS.md                       # Follow-up suggestions
│   ├── Bridge/
│   │   ├── MHSBridge.cs
│   │   └── MHSBridge.jslib
│   ├── Game-Code/
│   │   └── Systems/
│   │       ├── Logging/LoggingManager.cs
│   │       ├── Cloud/CloudManager.cs
│   │       └── Characterization/CharacterCustomization_DataController.cs
│   └── WebGL-Template/
│       └── MHS-Bridge-index.html
├── gamelogger-cache-overflow-091626/            # Dated update package 4
│   ├── README.md
│   ├── 01-gamelogger-cache-overflow.md          # Symptom → mechanism → required changes → verification
│   └── Game-Code/
│       └── Systems/
│           └── Logging/GameLogger.cs            # Drop-in (bounded cache, no wedging, batch sends)
└── dale-working-docs/                           # (Internal) working notes during development
    └── build-automation-slider-settings-save-051226/
```

**Organizing principle:** Each dated folder (`-MMDDYY`) is a complete, self-contained update package. The same area can be revisited in a later package without overwriting prior handoffs. Documentation is layered: repo README → update folder README → per-change numbered docs → optional background analysis.

## Code Patterns & Conventions

- **Naming:** 
  - Markdown docs use kebab-case for filenames (`01-pass1-centered-mouse-sensitivity.md`)
  - C# fields use camelCase with leading underscore (`_mouseSensitivity`, `_playerId`)
  - JSON/API payloads use snake_case (`user_id`, `player_settings`, `saved_games`)
  - URLs use kebab-case (`/api/settings/load`, `/api/state/save`)
  - Update folders use kebab-case with MMDDYY date suffix

- **Documentation structure:**
  - `README.md` in each folder: Overview, problem summary, file list, how-to-apply, verification steps
  - Numbered per-change docs: Issue/Symptom → Root Cause → Fix → Exact Edits (before/after) → Verification
  - Reference snapshots linked in each folder's README (Google Drive zipped project state)
  - Background analysis docs for complex coupling issues (optional, for context only)

- **Change format:** For code files, exact edits include line numbers and diff-style before/after snippets. For YAML (`.asset` and `.prefab` files), both raw YAML diffs and "Editor-equivalent" GUI paths are provided so non-YAML-literate developers can apply changes via the Inspector.

- **Error handling:** Guards for null/empty values before API calls (e.g., empty `playerId` guard in `SettingsSaveManager.LoadSettings`). Failures in non-blocking operations wrapped in try/catch with `LogWarning` so they don't fail otherwise-good builds.

- **Auth/Identity:** Transitioning from `login_id` → `user_id` (24-char lowercase hex MongoDB ObjectID). API contracts and logging payloads must use canonical `user_id`. Legacy field names (`playerId`, `player_id`, `login_id`, `email`) are rejected by Stratalog/Stratahub post-2026-05-26.

## Key Dependencies & Gotchas

1. **Reference snapshots are essential, not optional.** Each update folder's README links to a zipped Google Drive snapshot of the full MHS 2.0 project at the exact state the fixes were developed and verified against. Developers should use these as the source of truth for surrounding code context, asset wiring, and prefab structure — the numbered per-change docs describe edits precisely but don't reproduce entire file contents. Snapshots are also proof of fix; if you want to confirm a change works before landing it, open the snapshot, build it locally, and run it.

2. **YAML field serialization quirks.** `FloatVariable` and `IntVariable` ScriptableObject assets have a legacy serialization format where clamp settings (`_minMax: Vector2`) are configured but inert at runtime. The fix is to use the modern `FloatReference` format with explicit `_min`/`_max` blocks. When manually editing YAML, the field names and structure must be exact, or Unity silently ignores the malformed blocks.

3. **Prefab serialization across name changes.** When renaming C# fields that are serialized into prefabs (e.g., `_mouseSensitiviy` → `_mouseSensitivity`), use `[FormerlySerializedAs("old_name")]` attribute so existing prefab references migrate without losing their assignments. Without it, the renamed field comes up blank/default on load.

4. **API identity contract breaking changes (2026-05-26).** Stratalog and Stratahub now reject any logging or settings payload that uses deprecated field names (`playerId`, `player_id`, `login_id`, `email`) or non-hex values for identity. The canonical name is `user_id` (24-char hex ObjectID). Calls to `MHSBridge.GetPlayerID()` were renamed to `GetUserID()` to make the contract self-documenting at call sites.

5. **Settings vs. State save coupling bug.** Before the `SettingsSaver` decoupling fix, settings data was silently saved into both the dedicated `/api/settings/*` path **and** the game-state `/api/state/*` path (because `SettingsSaver` inherited from `PixelCrushers.Saver`). The two collections would drift, and on load the stale state-save copy would overwrite the fresh settings-load value. The fix is to override `OnEnable`/`OnDisable` in `SettingsSaver` to skip the base class registration, so it stops registering with `PixelCrushers.SaveSystem`.

6. **WebGL build-and-run vs. production hosting.** WebGL templates need two different build-name detection chains: one for production multi-build hosting (folder-name URL pattern), and a fallback using Unity's `{{{ LOADER_FILENAME }}}` build-time replacement for local Build-and-Run. The literal placeholder must be guarded against so it's safe for shared parent pages.

## How to Run Locally

This is a documentation and handoff repository; there is no build/run step. Instead:

```bash
# Clone the repo
git clone <repo-url> mhs-updates
cd mhs-updates

# Choose an update package
cd slider-and-save-changes-051226

# Read the overview
cat README.md

# Read each numbered change in order
cat 01-pass1-centered-mouse-sensitivity.md
cat 02-pass2-misspellings.md
cat 03-issue-a-empty-playerid-and-issue-b-pixelcrushers-decoupling.md

# Download the reference snapshot from the "Reference snapshot" link in README.md
# Unzip it, open in Unity, and verify the fix is working before applying to your branch
```

For developers applying an update to the active Perforce mainline:

1. Read the update folder's `README.md` (or `DIRECTIONS.md` if present).
2. For each numbered doc, read the Exact Edits section and apply changes to your active branch.
3. Run the Verification steps at the bottom of each doc.
4. After all changes in the package are applied, run the Combined Verification (listed in the folder README).

## Related Repos

- **stratahub** — Go web app dashboard for MHS data (MongoDB backend, accompanies the Unity game)
- **mhscurriculum** — Source curriculum docs that inform MHS gameplay and assessment logic
- **Mission HydroSci 2.0 (Perforce mainline)** — The canonical active-development branch of the Unity project; this repo's updates are handoffs ready to apply there

## Notes for Claude

1. **Handoff nature:** This repo is a formal handoff mechanism, not an active development branch. Each folder is a snapshot in time (dated, immutable). New updates get new folders; old updates don't get retrofitted. The repo documents both the "why" (root cause analysis) and the "how" (exact edits) so developers downstream can apply changes with confidence and without tribal knowledge.

2. **Reference snapshots are the source of truth.** Numbered per-change docs are self-contained and describe edits precisely, but they don't reproduce entire file contents. If a developer needs to see what the rest of a file looked like, how a prefab is wired, or what assets are referenced, the reference snapshot is the most complete source. Snapshots are also proof that the fix works — they're the exact project state the changes were verified against.

3. **Breaking changes are explicit.** The `mhsbridge-userid-cleanup` package documents a breaking API contract change (`user_id` becomes canonical; legacy field names are rejected). Future updates should flag similar breaking changes clearly in their READMEs and include migration guidance.

4. **Verify independently.** Each folder's README includes a "Combined verification" section that walks through the expected behavior after all changes are applied. Developers should follow these steps to confirm a fix lands cleanly and doesn't regress into the original issue. Reference snapshots can be opened, built, and run as an additional sanity check.

5. **Keep documentation layered.** Repo README explains the structure. Each folder's README explains that update. Numbered docs explain each change. Background analysis docs (like `background-settings-state-coupling.md`) explain the root cause but are optional reading. This structure means a dev in a hurry can apply a fix quickly, or a dev who wants to understand the coupling can dig deeper.

6. **Scope one coherent problem per folder.** Updates are grouped by problem domain (build automation, UI slider fixes, identity contract cleanup) so the dev team can apply them independently. If you find yourself documenting two unrelated problem areas in one folder, split them into separate updates with their own dates.
