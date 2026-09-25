# End of Unit 5 and the end-of-game ceremony

*2026-09-24. Applies to the Mission HydroSci 2.0 WebGL build (2.8.1 is the
one we tested). Bridge API: the `MHSBridge.cs` / `MHSBridge.jslib` from the
2026-05-26 drop, unchanged.*

## 1. What StrataHub does at the end of the game now

When a student finishes Unit 5, StrataHub shows an **end-of-game ceremony**:
a short award show, rendered in the browser outside the Unity build, in which
Toppo, Jasper, Tera, Anderson and Aryn recap the student's journey unit by
unit. Eight of the lines have two versions chosen from the student's
embedded-assessment scores, and the ending shows a star board (one to three
stars per unit) and the Planetary Water Steward award. It streams from the
CDN (about 37 MB), starts on a Begin button, and can be replayed from the
launcher afterwards.

The hand-off from the game to the ceremony is StrataHub's job. The host page
that runs the Unity build listens for the bridge's unit-complete signal; for
the last unit it now leaves the game **immediately** and opens the ceremony.
There is nothing left for the game to show after Unit 5.

## 2. What the build does today (2.8.1)

Observed on Dev MHS on 2026-09-22 with a test student, and confirmed in the
data:

1. The student goes through the base portal at the end of Unit 5.
2. The game shows its own **Congratulations — You have completed Mission
   HydroSci! Click continue to return to Strata** screen with a Continue
   button.
3. Continue calls `MHSBridge.Instance.EndGame()`.
4. **`MHSBridge.Instance.CompleteUnit("unit5", …)` is never called.** The
   game's log shows the `EndOfUnit` event and every Unit 5 progress point was
   graded, but StrataHub's progress record for the student never moved past
   `unit5`, because that record only advances on `CompleteUnit`.

The result was the launcher with Unit 5 still "Ready to play" and no
ceremony. The bridge contract (`MHSBridge.cs`, comment on `CompleteUnit`)
already says the call is expected for every unit **including the last**;
Units 1–4 do call it, which is how their transitions work.

StrataHub was made tolerant of this on 2026-09-24 (it now records the
completion itself when `EndGame` arrives), so the current build does reach
the ceremony. But the flow is still wrong for the player: they see the
game's own congratulations screen, press Continue, and only then get the
real ending. We want the game to end at the portal and the ceremony to
begin.

## 3. Required change

Two edits, both in the Unit 5 end-of-unit code:

**a. Remove the Congratulations / Continue screen.** The ceremony is the
end-of-game screen now. Nothing should be shown by the game after the
player leaves Unit 5.

**b. At the point where Unit 5 ends** (where the portal currently leads to
that screen), call the bridge in this order:

```csharp
// Unit 5 is finished: same call as every other unit makes at its end.
MHSBridge.Instance.CompleteUnit("unit5", null);

// Then the game is done. In StrataHub's PWA mode the host page has already
// left the game on CompleteUnit and this call never runs; it is the exit
// path for a build hosted outside StrataHub (URL mode), where CompleteUnit
// with a null next URL is a no-op.
MHSBridge.Instance.EndGame();
```

`CompleteUnit` must be called **before** `EndGame`, and both must happen
without waiting for a click. Everything else stays as it is:

- Keep the `EndOfUnit` log event and the quest events at the end of Unit 5
  exactly as they fire today. In particular `questFinishEvent:45` is the
  grading trigger for the last progress point (Water Problems Require Water
  Solutions); if it stops firing, the last point is never graded and the
  ceremony has nothing to say about it.
- Keep the game's existing behaviour in the Editor (both calls are
  `#if UNITY_WEBGL && !UNITY_EDITOR`, so they log and do nothing there).
- No change to `MHSBridge.cs`, `MHSBridge.jslib`, the WebGL template, or
  the identity/service configuration.

If you want to keep a brief in-game moment at the portal (a fade, a sound),
that is fine as long as `CompleteUnit("unit5", null)` is called when it ends
and no screen waits for input.

## 4. What happens after the call, on each host

| Host | On `CompleteUnit("unit5", null)` | On `EndGame()` |
|---|---|---|
| StrataHub (PWA mode: the host page called `OnPWAReady`) | The page records the completion, tears down the Unity instance and opens the ceremony. The game is gone at this point. | Never runs (the page has left). If a build calls it first for any reason, StrataHub still records the completion and opens the ceremony. |
| Outside StrataHub (URL mode) | No-op (null next URL). | The bridge's existing fallback: tries to close the tab, else shows "Game Complete — you can close this tab". |

## 5. Edge cases we checked

- **A student who replays Unit 5** (it stays playable after completion):
  `CompleteUnit("unit5", null)` is idempotent on the StrataHub side; the
  ceremony opens again and can be replayed as often as they like.
- **The completion request fails** (offline, timeout): StrataHub queues it
  and flushes it from the launcher later; the student lands on the launcher
  and the ceremony becomes available once the queue drains.
- **Double firing.** The grading team noted that `questActiveEvent:36` and
  `questFinishEvent:45` sometimes fire twice. It does not affect the
  hand-off (StrataHub ignores a repeated completion), but if you are in that
  code anyway, a single firing would be cleaner for grading.
- **Older StrataHub pages** (before 2026-09-24) would leave the game running
  after `CompleteUnit` on the last unit and wait for `EndGame`; with
  `EndGame` called right after `CompleteUnit` as above, those pages also
  transition correctly. No StrataHub version depends on the removed screen.

## 6. Verification on Dev MHS

1. Deploy the build to a collection on the Dev MHS workspace (MHS Builds →
   Upload, then activate) with the ceremony selected in the collection (it
   is already set on the current Dev MHS collection).
2. As a member whose current unit is Unit 5, play to the base portal and go
   through it.
3. Expected: no in-game congratulations screen; the browser leaves the game
   and the ceremony's Begin button appears (loading takes a few seconds on a
   school network); the ceremony plays through to the star board.
4. Back on the Mission HydroSci launcher: "Mission Complete" with a "Watch
   your ceremony" button, and the End-of-game ceremony row reads "Watch".
5. Staff check: the student's row on the MHS Dashboard shows a trophy mark
   beside their name, and the Device Tests viewer has a `ceremony-ok` row
   under unit `end` for them.

If step 3 instead shows the launcher with Unit 5 "Ready to play", the
`CompleteUnit` call did not reach StrataHub: check the browser console on
the game tab for `MHS: mhsUnitComplete called with unit5` (the bridge logs
it) and for a failed `POST /missionhydrosci/api/progress/complete`.
