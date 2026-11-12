# Save/resume fix: what to do in the game project

**What this bundle changes:**

- **The checkpoint replay runs first on every resume.** Pixel Crushers' save system applies its savers in no fixed
  order, and the checkpoint replay switches objects that other savers own. Depending on the order, a resumed Unit 3
  student lost the first supply crate, and a Unit 4 puzzle collider came back on although the save had it off. The
  replay now runs first, then every other saver applies what it recorded. Pixel Crushers' own code is not changed.
- **Five Unit 2 quests are listed under Unit 2.** The quest table (`QuestAndUnitSO`) had *Claim of Command*, *A Word
  from Our Sponsors*, *Secrets of the Lost Society*, *The Measure of a Mutt* and *Creating a Catalogue* under Unit 1.
  On every Unit 2 resume the game set them back to unassigned, so a student who resumed lost the final quest
  (*Claim of Command*, checkpoint 14) and any side quest they had started.
- **The fog-of-war flag is set, not toggled.** The flag is not reset between scene loads, and a resume calls the fog
  switch a different number of times than live play does, so after some resumes the flag said the opposite of what the
  map showed.
- **The Unit 2 map hints come back right.** After a resume at checkpoint 4 or 5 the hint text was missing; after a
  resume at 6 or later the hint panel was open where live play has it closed.
- **Five new save points in Unit 2**, at checkpoints 2, 3, 5, 9 and 11. They already have their checkpoint numbers
  and replay events; only saving was switched off. They split the longest stretches between saves (medians of 7 to
  12.5 minutes in production), so a student who has to stop loses less.

Applying it is: replace four scripts, change one asset and one prefab (in the Inspector, or by replacing the files),
tick five boxes in the Unit 2 scene, build every unit, upload. Nothing changes in the save format, the save service
or StrataHub.

## Part 1: what to do

### Step 1. Replace four scripts

`Game-Code/` in this bundle mirrors `Assets/` in the project. Each file goes over the file of the same path.

**Diff first.** The files were written against your project as of 2026-10-29 (changelist 12527, Unity 6000.0.74f1),
which already holds the saving fix (`gamesave-fix-102426` 1.3). Before overwriting a file, diff ours against yours;
if your copy has changed since then, carry your change into ours (or tell us) rather than losing it. Line endings are
the same as yours (LF).

| File (same path under `Game-Code/` and `Assets/`) | What changed |
|---|---|
| `Scripts/Systems/Save Load System/CustomSavers/CheckpointSaver.cs` | New `ApplySaveCheckpointFirst(SavedGameData)`; `ApplyData` skips its turn when that method has already applied it. |
| `Scripts/Systems/Save Load System/CustomSavers/DatabaseSaver.cs` | Its two `SaveSystem.ApplySavedGameData(saveData)` calls now call `CheckpointSaver.ApplySaveCheckpointFirst(saveData)`. Nothing else. |
| `Scripts/Map/FOWMapSwitcher.cs` | `_fowIsActive` is set to false (out of range) or true (a fog image is shown) instead of toggled. |
| `Scripts/Map/ModularMap/Scripts/MapDataProcessor.cs` | `_fowIsActive` is set to true instead of toggled when the first fog image is put up. |

Everything else in those files is as in your copy, and nothing that calls them needs to change.

**From now on**, anything that applies a save must call `CheckpointSaver.ApplySaveCheckpointFirst(saveData)`, not
`SaveSystem.ApplySavedGameData(saveData)`. Today `DatabaseSaver` is the only place that applies saves.

### Step 2. The quest table: five quests to Unit 2

In the Project window select `Assets/Data/Dialogue System/QuestAndUnitSO.asset`. In the Inspector, set **Unit Number**
to **Unit 2** for these five entries of **Quests By Unit**:

- A Word from Our Sponsors
- Claim of Command
- Creating a Catalogue
- Secrets of the Lost Society
- The Measure of a Mutt

Leave every other entry as it is. Or replace the asset with `Game-Code/Data/Dialogue System/QuestAndUnitSO.asset`,
diffing first: the only differences are those five `UnitNumber: 1` lines, which become `UnitNumber: 2`.

Don't press the table's **Get Quests** button: it appends every quest in the dialogue database again, all as Unit 1.
When you add a quest, add its row by hand with its real unit.

### Step 3. The Unit 2 replay events: map hints

Open the prefab `Assets/Prefabs/_Core/SaveSystem/AutoSaveTracker - Unit 2.prefab` and find the **Events** list of its
Checkpoint Saver. The two variables are in `Assets/Data/Scriptables/Map/Map Hints/`.

| Event | Change |
|---|---|
| **Element 4** | Add a call at the end: `BoolVariable_MapHint_HintTextOn`, `BoolVariable.Value`, ticked (true). |
| **Element 5** | Add a call at the end: `BoolVariable_MapHint_HintTextOn`, `BoolVariable.Value`, ticked (true). |
| **Element 6** | Its call on `BoolVariable_MapHint_HintTextOn` becomes ticked (true); its call on `BoolVariable_MapHint_PanelOn` becomes unticked (false). |

After the change, events 4 and 5 have three calls each and event 6 still has three. Or replace the prefab with
`Game-Code/Prefabs/_Core/SaveSystem/AutoSaveTracker - Unit 2.prefab`, diffing first.

**Only append to events 4 and 5.** The Unit 2 scene holds overrides for calls that the prefab no longer has, on events
0, 1, 7, 8, 10 and 11. Unity ignores them today, but adding a call to one of those events in the prefab brings the
scene's old call at that position back to life. Part 3 lists them.

### Step 4. The Unit 2 scene: five save points

Open `Assets/Scenes/Gameplay/Unit 2 Prod/Unit 2 Prod (Refactor).unity`. The three objects are under
`===== Quest Specific Objects =====`. On each one's Quest Event Value Listener, open **Value Responses → Element 0 →
Quest Entries**, and tick **Save On Activation** on these entries:

| Object | Quest Entries element | Entry | Auto Checkpoint On Activation (already set; check it) |
|---|---|---|---|
| `Quest Listener - Foraged Forging` | Element 1 | Foraged Forging, entry 2 | 2 |
| `Quest Listener - Getting The Band Back Together` | Element 0 | Getting the Band Back Together, entry 1 | 3 |
| `Quest Listener - Getting The Band Back Together` | Element 2 | Getting the Band Back Together, entry 3 | 5 |
| `Quest Listener - Which Watershed` | Element 0 | Which Watershed?, entry 1 | 9 |
| `Quest Listener - Which Watershed` | Element 4 | Which Watershed?, entry 5 | 11 |

Save the scene. `Game-Code/Scenes/Gameplay/Unit 2 Prod/Unit 2 Prod (Refactor).unity.diff` shows the result: five
`SaveOnActivation: 0` lines become `1`, and nothing else changes. The scene is not shipped whole because yours may
have moved on since 2026-10-29.

### Step 5. Run the Editor tests

Put the tests in your project and run them, as Part 2 describes. All 19 must pass. They check the four scripts, the
quest table, the replay events and the five save points in your project, so they catch a step that was missed.
Remove them again before the release build.

### Step 6. Build and hand over

1. Build **every unit** (release profiles) with a distinct version string. The scripts are shared by all units, so
   every unit changes, not only Unit 2.
2. Upload the zip through MHS Builds on the dev site; that creates a collection.
3. Tell us the collection name. We run the checks listed in Part 3 on it.
4. Only after that is the collection made active for students.

That is all there is to do. Part 2 covers the Editor tests; Part 3 is reference material.

---

# Part 2: running the Editor tests

`tools/SaveResumeTests.cs` holds 19 tests, and `tools/Fixtures/` holds the 15 game saves they resume. Each scene test
resumes a real save the way the game does: the unit's scene, the save applied through
`CheckpointSaver.ApplySaveCheckpointFirst`, the resume finished. It then checks one guarantee of the fix. Details are
in `tools/README.md`. In short:

1. Copy `tools/SaveResumeTests.cs` to `Assets/Tests/SaveResume/Editor/SaveResumeTests.cs`, and `tools/Fixtures/*.json`
   to `Assets/Tests/SaveResume/Fixtures/`. The `Editor` folder matters: these are **Edit Mode** tests, because each one
   needs a play session of its own.
2. Open **Window → General → Test Runner**, **EditMode** tab, and run **MHS.SaveResumeTests**. Each test enters and
   leaves play mode, so the Editor goes in and out of play mode 18 times; a full run takes about three minutes.
3. If the file does not compile and the Console shows *The type or namespace name 'UnityTest' could not be found*,
   your `Assets/csc.rsp` is missing or points at another machine. Your 2026-10-29 copy has one with a
   `/home/...` path, so it is set up as in the saving fix: one line, `-r:"<your project>/Library/ScriptAssemblies/UnityEngine.TestRunner.dll"`.
   The full explanation is in Part 2 of `../gamesave-fix-102426/03-for-the-game-dev.md`.

**Why each test enters play mode itself.** The game decides which unit it is from the build's scene list
(`SceneLoadingUtilities.GetUnitNumberForThisBuild`), and play mode reads that list when it starts. The Editor's own list
holds every unit, so in a normal play session the game thinks it is another unit and resets the scene's quests on
resume. Each test therefore puts only its own unit's scene in the list, enters play mode, and puts your list back when
it is done. Your `EditorBuildSettings` are the same after the run as before. If a run is stopped half-way, check
**File → Build Profiles → Scene List** once.

**The game's own errors.** A unit scene started on its own (no StrataHub page, no main menu, no services) logs errors
and exceptions while it starts, such as `DisableForReleaseBuild` and `DoorController`. They are not caused by the fix
and happen without it too. The tests let them through and check the state.

**Before a release build**, delete `Assets/Tests/SaveResume/` (and `csc.rsp` if you only added it for this).

---

# Part 3: supporting information (nothing to do here)

## What each change fixes

**Checkpoint replay first (`CheckpointSaver.cs`, `DatabaseSaver.cs`).** On a resume every saver applies what it
recorded, and the checkpoint saver replays the unit's checkpoint events 0..k (teleport, show/hide objects, timers,
atmosphere). Pixel Crushers' `SaveSystem` keeps its savers in a `HashSet<Saver>`, and `ApplySavedGameData` walks a copy
of it from the end, so the order follows hash codes, not the scene. Where a saver and a replay event set the same
object, the last one wins:

- **Unit 3:** the saves record Supply Crate 1 shown, and replay event 1 hides the Supply Run objects. In the Editor the
  checkpoint saver comes after 20 of the 24 savers, so the crate came back hidden. 10 of 25 sampled saves were affected.
- **Unit 4:** `GlyphGame_InfiltrationMultiEnabledSaver` records the Infiltration glyph's collider off, and replay event
  2 turns it on. 13 of 20 sampled saves depend on the order.

`ApplySaveCheckpointFirst` applies the active checkpoint saver(s) first, then calls `SaveSystem.ApplySavedGameData`. The
checkpoint saver skips its turn in that pass, so the replay runs exactly once per apply. An exception in the replay is
logged and the apply goes on, as in `SaveSystem`'s own loop. With the replay first, whatever a saver recorded is what
the player gets back. That is the intended result: the savers record the state at the moment of the save, and the
replay only rebuilds what no saver holds.

We kept the change in MHS code because the alternative, changing `SaveSystem.cs` in Pixel Crushers, would be undone
by the next asset update without anyone noticing.

**Quest table (`QuestAndUnitSO.asset`).** On every resume `CheckpointSaver.ApplyWaypoints` calls
`QuestResetUtility.ResetUnfinishedQuests`, which sets every active quest of another unit back to unassigned (so a
quest left over from Unit 1 doesn't stay in the Unit 2 log). The table's **Get Quests** button fills it with every
quest as Unit 1, and these five were never corrected. Production logs show *Claim of Command* only in Unit 2, and real
Unit 2 saves show the four side quests active. The two other Unit 1 rows that might look wrong ("The Most Important
Meal of the Day" and "?") are never active in any save, so they are left alone.

In production, of 85 students with a checkpoint 14 save, 15 reloaded Unit 2 afterwards; the reset runs on every Unit 2
load, so each of those resumes set *Claim of Command* back to unassigned. 11 of them still reached the end of the unit,
3 were moved on by their teacher, and 1 reloaded about 20 times without the quest starting again.

**Fog of war (`FOWMapSwitcher.cs`, `MapDataProcessor.cs`).** `BoolVariable_WorldSpaceMap_FOWIsActive` is not reset on
scene load, and both scripts flipped it rather than setting it. Live play and a resume call the fog switch a different
number of times (the replay passes the Band quest's map index), so after resumes at 6, 7, 10 and 12 the flag said the
opposite of the fog image.

**Map hints (replay events 4, 5, 6).** At checkpoints 3 to 5 the Band quest sets the map-hint text on (step 1) and
names the place to show. Finishing step 3 closes the hint panel, and it stays closed for the rest of Unit 2. On a resume
the skip list `{3, 4, 5, 9}` runs events 3, 4 and 5 only when they are the current checkpoint, so:

- a resume at 4 or 5 skipped event 3, the only one that set the text on (now events 4 and 5 set it too);
- event 6 set the panel **on** and the text **off**, the reverse of live play, and every resume at 6 or later runs
  event 6 (now panel off, text on).

**Save points (Unit 2 scene).** The game sets the checkpoint number only inside its save
(`QuestEventValueListener.DoAutoSave`), so a step with a checkpoint number but no save never moves the number. These
five steps have their numbers and replay events (the replay list has 15 events for checkpoints 0 to 14). Only Save On
Activation was off. In production, Unit 2's longest stretches between saves contain them: 1→4 (median 7.0 minutes),
4→6 (7.8), 8→10 (11.7) and 10→12 (12.5).

A Unit 2 playthrough now saves 14 times instead of 9. Saves made before the change load as before: the format is the
same, and an old save resumes at its own checkpoint number.

## How it was checked

All of this was run in a copy of your 2026-10-29 project with the changes applied (Unity 6000.0.74f1, batch mode),
using a test harness that is not part of this bundle:

- **Saver order, every unit.** The first save of every checkpoint in Units 1 to 5 (real student saves) was resumed
  twice with the replay forced first and twice with it forced last. Every save resumes the same either way; the only
  difference is a few hundredths of a unit in position. Re-saving right after the resume reproduces the input save for
  every save. Before the change, Unit 3 had 12 and Unit 4 had 14 order-dependent saves.
- **Live vs resumed, Unit 2.** For each pair of consecutive save points (0→1, 1→4, 4→6, 6→7, 7→8, 8→10, 10→12, 12→14;
  two students each), a student's save was resumed and the quest steps up to the next save were played through the
  quest log. The world at the moment the game saved ("live") was then compared with that save resumed in a fresh
  Editor ("resumed"). The fixes above remove every difference that came from the save system: the map hints, the fog
  flag, the five quest resets and the missing quest waypoint at 14. What is left is listed below.
- **The new save points.** For each of 2, 3, 5, 9 and 11 (two students each), the game saved at the right number with
  no errors. Resuming that save and playing on to the next save point gives the same world as playing the whole
  stretch without a resume. The only exceptions are a cutscene that was playing at the moment of the save (3) and an
  inactive timer (5).
- **No new errors.** The exceptions the Editor logs in Units 3 to 5 are the same ones as before the change
  (a DOTween material tween, the camera invert setting, the chat log display), and none come from the changed scripts.
- **The Editor tests** of Part 2: 19 of 19 pass in our copy. Run against your original files (the fix taken out),
  13 fail, each on the problem it is about. The six that pass without the fix are T03, which guards the new apply path
  (the replay runs once, not twice), and T15 to T19, which show that a save made at a new save point resumes there.
  The existing replay events already support those numbers.
- **Test build on the dev site.** Units 2, 3 and 4 built from that copy (release profiles) are on the dev site as the
  collection `20261111-saveresume-SaveResumeFixTest` (Unit 2 v3.0.8, Unit 3 v3.0.4, Unit 4 v3.0.3; Units 1 and 5 are
  the 2026-11-02 builds). It is not active for students. With the test member, Unit 2 loads, renders and starts with no
  game errors, and the unit-change reset logs the five quests as Unit 2 ("… is part of Unit2. Skipping"). Anyone with a
  dev account can be put on it to play and comment.

## Found and not changed

These came up in the live-vs-resumed comparison. They are left out of this bundle: none of them changes saved data
or quest progress, and each needs a decision about what the scene is meant to show. They're listed so you can decide.

1. **Jasper at the campsite after a resume at 1 to 7.** *Escape the Ruin*'s success hides him; *Getting the Band Back
   Together* entry 5 (checkpoint 8) shows him again. No replay event hides him, so a student who resumes in between
   sees him at the camp while the story has him missing. Talking to him starts nothing: every branch of `U2/Jasper`
   needs a *Which Watershed?* or *Checking In* state that comes later.
2. **Anderson's model at checkpoint 1.** Replay event 1 shows `Anderson-Unit2/Visuals`; in live play it is hidden at
   that point (we could not find what hides it; the opening timeline is the likely source).
3. **Toppo at checkpoint 14.** Replay event 14 turns Toppo's main Usable off, which in live play happens one step
   later (*Claim of Command* entry 2). A resumed student can't talk to Toppo before checking on Jasper.
4. **The map highlight under the fog at 6 to 7.** Replay event 6 passes 3 to the three-image fog switcher, which is out
   of range. The fog is then off in both live play and a resume, but the highlight underneath shows region 0 instead of
   2 until checkpoint 8 sets it.
5. **Dead calls in the Unit 2 scene.** The scene overrides calls that `AutoSaveTracker - Unit 2.prefab` no longer has,
   without a size override, so Unity drops them (confirmed by reading the events at run time):

   | Event | Dropped calls | Fully defined ones (method and argument set) |
   |---|---|---|
   | 0 | 1 to 5 | 1: `SetActive(false)` on Jasper Campsite's Post-Argumentation |
   | 1 | 4 | 4: `SetActive(true)` on Anderson's Visuals (the same as call 3) |
   | 7 | 2 | 2: `SetActive(false)` on Player Touches Glyph Puzzle |
   | 8 | 3 | 3: `ClassifierManager.CompletedInitialExplanation` |
   | 10 | 1 to 9 | none (targets only) |
   | 11 | 2 to 9 | none (targets only) |

   Adding a call to one of these events in the prefab brings the scene's call at that position back, which is why
   Step 3 only appends to events 4 and 5. Removing the dead overrides (Inspector: right-click the event list on the
   scene instance → *Revert*) is safe and would make the list read as it runs.

## The checks we run on your build

1. The Editor tests on your project as delivered (Part 2).
2. On the dev site, with the test member: play Unit 2 to each new save point (2, 3, 5, 9, 11), reload, and check that
   the game resumes there with the step active and the map hint as it was.
3. Resume at 14 and check that *Claim of Command* is in the quest log.
4. Resume a Unit 3 save at checkpoint 5 or later and check that the first supply crate is shown.

## Version history

- **1.0 (2026-11-11).** First version.
