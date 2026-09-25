# End of Unit 5 and the End-of-Game Ceremony — 2026-09-24

StrataHub now owns the end of the game: when Unit 5 completes, the player is
taken to an award ceremony (a web experience outside the Unity build) that
recaps their journey with the five characters and shows their per-unit
stars. This drop asks for one change in the game so that hand-off happens
cleanly, and removes the need for the in-game end screen.

## TL;DR

- In build 2.8.1 the end of Unit 5 calls **only** `MHSBridge.EndGame()` (from
  the Congratulations / Continue screen). It never calls
  `MHSBridge.CompleteUnit("unit5", …)`, so StrataHub never learned that
  Unit 5 was finished: the student came back to the launcher with Unit 5
  still "Ready to play" and no ceremony. (StrataHub has since been made
  tolerant of this, but the contract is still wrong in the build.)
- Requested change: **remove the Congratulations / Continue screen** and,
  at the point where Unit 5 ends (the base portal), call
  `CompleteUnit("unit5", null)` exactly as the other units do, then
  `EndGame()` immediately after it. In StrataHub's PWA mode the page leaves
  the game on `CompleteUnit` and the ceremony starts; `EndGame` only matters
  when the build is hosted outside StrataHub.
- Nothing about logging changes. The grading trigger for the last progress
  point (`questFinishEvent:45`) and the `EndOfUnit` event must keep firing
  exactly as today.

## What's in this folder

| Path | Purpose |
|------|---------|
| `01-end-of-unit-5-and-the-ceremony.md` | The full write-up: what StrataHub does at the end now, what the build does today, the required change, the exact call sequence, edge cases, and verification on Dev MHS. **Start here.** |

No source files are included: the change is a call sequence at the end of
Unit 5 and the removal of one screen, both in code we do not have in this
repo. The bridge itself (`MHSBridge.cs` / `MHSBridge.jslib` from the
2026-05-26 drop) is unchanged.

## Where to start

1. Read `01-end-of-unit-5-and-the-ceremony.md`.
2. Make the two edits under "Required change".
3. Run the verification on Dev MHS: finishing Unit 5 should go directly into
   the ceremony, and the launcher afterwards should show "Mission Complete"
   with a "Watch your ceremony" button.
