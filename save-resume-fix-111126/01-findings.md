# Findings: what a resume gives back

All counts come from production saves and logs, read only, or from runs in a copy of the game team's 2026-10-29
project (Unity 6000.0.74f1). No student or school is named here.

## 1. How a resume works

When a unit scene loads, `DatabaseSaver` applies the newest save at the end of the first frame. In that apply:

1. **Every saver applies what it recorded.** That includes the Dialogue System's saver (quest states, variables), the
   checkpoint saver (the checkpoint number), door, puzzle and waypoint savers, the player and drone position savers, and
   Pixel Crushers' `ActiveSaver`, `MultiActiveSaver` and `MultiEnabledSaver` on many quest objects. These were read
   from 1,289 real Unit 2 saves.
2. **The checkpoint saver replays the unit's checkpoint events 0..k**, k being the saved checkpoint. A skip-listed event
   runs only when it is the current checkpoint. Unit 2 has 15 events (checkpoints 0 to 14) and skips 3, 4, 5 and 9.
   These events rebuild what no saver holds: they teleport the player and switch objects, timers, audio and atmosphere.
3. **At the end of that frame**, the activation responses of every active quest entry of the unit run again
   (`ApplyWaypoints`), after quests of other units are set back to unassigned (`QuestResetUtility`).

The checkpoint number moves only when the game saves (`QuestEventValueListener.DoAutoSave` sets it just before raising
the save event). A step that has a checkpoint number but does not save never changes it.

## 2. The savers apply in an undefined order

`PixelCrushers.SaveSystem` keeps its savers in a `static HashSet<Saver>`. `ApplySavedGameData` copies the set and
applies from the end of the copy. A `HashSet`'s order follows hash codes, not the scene, so the checkpoint replay runs
before some savers and after others. Where a saver and a replay event set the same object, the last one wins.

The first save of every checkpoint in every unit was resumed with the replay forced first and forced last (2 runs
each), and the resulting worlds were compared:

| Unit | Saves | Order-dependent | What differs |
|---|---|---|---|
| 1 | 18 | 0 | – |
| 2 | 48 | 0 | – |
| 3 | 25 | 12 | Supply Crate 1 mesh and boundary (10 saves), Open Crate (1), the dungeon opening-cutscene trigger (1) |
| 4 | 20 | 14 | `GlyphGame_Infiltration` collider (13), `Door_DrillRoom` collider (1) |
| 5 and its dungeon | 11 | 0 | – |

- **Unit 3:** the saves record Supply Crate 1 shown; replay event 1 hides the Supply Run objects. With the replay
  last the crate is hidden, and with it first the crate is shown, as saved. In the Editor's own order the checkpoint
  saver applies after 20 of 24 savers, so students resuming at 5 or later lost the crate.
- **Unit 4:** the glyph's `MultiEnabledSaver` records its collider off; replay event 2 turns it on.

With the replay first, every save in every unit resumes the same in either order (positions differ by hundredths), and
re-saving right after the resume reproduces the input save.

## 3. Live vs resumed, Unit 2

The order test shows that a resume is stable. It can't show that a resume is wrong in the same way every time. So for
each pair of consecutive Unit 2 save points (two students per pair), a student's save was resumed and the quest steps
up to the next save were played through the quest log, taken from the next save. The world at the moment the game
saved was then compared with that save resumed in a fresh Editor. The differences found, before the fix:

| Found | Where | Cause |
|---|---|---|
| Map hint panel open (live: closed) | every resume at 6 or later | replay event 6 sets PanelOn true and HintTextOn false; live play has the reverse from Band entry 3's success on |
| Map hint text off (live: on) | resumes at 4 and 5 | only event 3 sets it, and 3 is skip-listed |
| Fog-of-war flag the opposite of the fog image | resumes at 6, 7, 10, 12 | the flag is toggled, not set, and not reset on scene load |
| *The Measure of a Mutt*, *A Word from Our Sponsors*, *Secrets of the Lost Society*, *Creating a Catalogue*, *Claim of Command* unassigned (live: active) | resumes at 1, 6, 8, 12, 14 | listed as Unit 1 in `QuestAndUnitSO`, so the unit-change reset clears them |
| No quest waypoint and Jasper talkable at 14 | resume at 14 | follows from *Claim of Command* being reset |

Production confirms the quest table finding. In the logs *Claim of Command* only ever starts in Unit 2, and real
Unit 2 saves hold the four side quests active. Of 85 students with a checkpoint 14 save, 15 reloaded Unit 2 afterwards
and resumed with *Claim of Command* unassigned. 11 still reached the end of the unit, 3 were moved on by their teacher,
and 1 reloaded about 20 times without the quest ever starting again.

After the fix, all 16 pairs were run again: none of these differences remain. What does remain is listed in Part 3 of
`03-for-the-game-dev.md` ("Found and not changed"): cosmetic scene states, and play the harness does not do (walking
into trigger rooms, flying the drone to collect data).

## 4. Long stretches without a save, and the five new save points

Unit 2 saves at 0, 1, 4, 6, 7, 8, 10, 12 and 14. In production (the last 30 days before 2026-11-10, 415 students) the
median time between consecutive saves is 7.0 minutes for 1→4, 7.8 for 4→6, 11.7 for 8→10 and 12.5 for 10→12. A
student who closes the game loses everything since the last save.

Five steps in those stretches already set a checkpoint number when they start, with a replay event for that number,
but do not save: Foraged Forging entry 2 (checkpoint 2), Getting the Band Back Together entries 1 (3) and 3 (5), and
Which Watershed? entries 1 (9) and 5 (11). Turning saving on there adds save points without renumbering anything.
(The build of late August 2026 inserted a save at 1 and moved every later number up by one; Unit 2 saves from before
then use the old numbers.)

Each was checked the same way as the existing save points, two students each. Live to the step: the game saved at the
right number with no errors, and the save resumes like the existing ones. Then the candidate save was resumed and
played on to the next existing save, and compared with playing the whole stretch without a resume. The worlds match,
apart from a cutscene that plays at the moment of the save at 3 (it does not replay on resume) and an inactive timer
at 5.

## 5. Things measured on the way

- **The game decides its unit from the build's scene list** (`SceneLoadingUtilities.GetUnitNumberForThisBuild`: the
  first scene with "unit" in its name). In the Editor that list holds every unit, so a Unit 2 scene played in the Editor
  thinks it is another unit and resets its own quests on resume. A build has one unit, so this affects Editor testing
  only. The tests in `tools/` put the unit's scene in the list before entering play mode.
- **The opening cutscene of Unit 2 plays on every load, resumes included.** Comparisons need about 20 seconds after
  load before the world is settled.
- **Unity's batch mode never signals the end of a frame**, so the checkpoint saver's last step (`ApplyWaypoints`, after
  `WaitForEndOfFrame`) does not run there by itself. Tests and harness run it themselves in batch mode only.
