# End of Unit 5, end of game, and the ceremony — 2026-09-25

StrataHub now shows an **end-of-game ceremony** after Mission HydroSci: an
award show, outside the Unity build, in which the five characters recap the
student's journey and show their per-unit stars. This drop describes the two
bridge events StrataHub listens for at the end of the game and asks for one
addition in the build.

## TL;DR

- Two events, both required, in this order: **`CompleteUnit("unit5", null)`**
  when Unit 5 is finished (as every other unit already does at its end), then
  **`EndGame()`** when the game is over. Back to back from the same place, or
  from separate code (a screen between them), both work.
- Build 2.8.1 sends only `EndGame()` (from its Congratulations / Continue
  screen) and never `CompleteUnit("unit5", …)`. StrataHub copes with that
  today, so the current build reaches the ceremony; the request is to add
  the missing call so the contract is complete.
- The Congratulations screen is **your call**. If it stays, please change
  its copy: it no longer returns the student to Strata, it leads into the
  ceremony (recommended wording inside). If it goes, call both events at
  the portal and the ceremony begins directly.
- The ceremony **starts by itself** when the student arrives; there is no
  Begin button in the normal flow.
- Nothing about logging changes: `questFinishEvent:45` and the `EndOfUnit`
  event keep firing exactly as today.

## What's in this folder

| Path | Purpose |
|------|---------|
| `01-end-of-unit-5-and-the-ceremony.md` | The full write-up: what StrataHub does at the end, what each event means, what the build does today, the required addition, the screen and its copy, what happens on each host, edge cases, and verification on Dev MHS. **Start here.** |

No source files are included: the change is a call at the end of Unit 5
(and optionally the copy of one screen), in code we do not have in this
repo. The bridge itself (`MHSBridge.cs` / `MHSBridge.jslib` from the
2026-05-26 drop) is unchanged.

## Where to start

1. Read `01-end-of-unit-5-and-the-ceremony.md`.
2. Add the `CompleteUnit("unit5", null)` call; decide about the screen and
   its copy.
3. Run the verification on Dev MHS: finishing Unit 5 should end in the
   ceremony playing on its own, and the launcher afterwards should show
   "Mission Complete" with a "Watch your ceremony" button.
