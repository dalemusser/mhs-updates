# End of Unit 5, end of game, and the end-of-game ceremony

*2026-09-25. Applies to the Mission HydroSci 2.0 WebGL build (2.8.1 is the
one we tested). Bridge API: the `MHSBridge.cs` / `MHSBridge.jslib` from the
2026-05-26 drop, unchanged.*

## 1. What StrataHub does at the end of the game

When the game ends, StrataHub shows an **end-of-game ceremony**: a short
award show, rendered in the browser outside the Unity build, in which Toppo,
Jasper, Tera, Anderson and Aryn recap the student's journey unit by unit.
Eight of the lines have two versions chosen from the student's
embedded-assessment scores, and the ending shows a star board (one to three
stars per unit) and the Planetary Water Steward award. It streams from the
CDN (about 37 MB), starts on its own as soon as it has loaded, and can be
replayed from the launcher afterwards.

Two things happen at the end, and StrataHub treats them as two separate
facts:

| Event | Bridge call | What StrataHub records |
|---|---|---|
| **Unit 5 is finished** | `MHSBridge.Instance.CompleteUnit("unit5", null)` | Unit 5 complete; the student's progress becomes "Mission Complete". Same call as every other unit makes at its end. |
| **The game is over** | `MHSBridge.Instance.EndGame()` | The end of the game. This is what opens the ceremony. |

The ceremony hangs off the second fact only. Unit completion never opens it,
and a teacher can end the game for a student from the MHS Dashboard without
any unit being complete; the game's `EndGame` is the normal way it happens.

## 2. What the build does today (2.8.1)

Observed on Dev MHS on 2026-09-22 with a test student, and confirmed in the
data:

1. The student goes through the base portal at the end of Unit 5.
2. The game shows its **Congratulations — You have completed Mission
   HydroSci! Click continue to return to Strata** screen with a Continue
   button.
3. Continue calls `MHSBridge.Instance.EndGame()`.
4. **`MHSBridge.Instance.CompleteUnit("unit5", …)` is never called.** The
   game's log shows the `EndOfUnit` event and every Unit 5 progress point was
   graded, but nothing told StrataHub that Unit 5 was finished. Units 1–4
   do call it, which is how their transitions work, and the bridge contract
   (`MHSBridge.cs`, comment on `CompleteUnit`) says the call is expected for
   every unit including the last.

StrataHub now covers this: when `EndGame` arrives while the page is running
the last unit, it records that unit as complete as well, so the current build
does reach the ceremony. The contract is still incomplete in the build, and
the screen's copy is now wrong for the player (it says "return to Strata";
what follows is the ceremony).

## 3. Required addition

At the point where Unit 5 is finished (where the portal currently leads to
the Congratulations screen), call:

```csharp
// Unit 5 is finished: same call as every other unit makes at its end.
MHSBridge.Instance.CompleteUnit("unit5", null);
```

`EndGame()` stays where it is (or moves to the same spot, see §4). Both calls
must happen with `CompleteUnit` first; back to back from one place, or with
a screen between them, are both fine. StrataHub ignores a repeated call of
either.

Everything else stays as it is:

- Keep the `EndOfUnit` log event and the quest events at the end of Unit 5
  exactly as they fire today. In particular `questFinishEvent:45` is the
  grading trigger for the last progress point (Water Problems Require Water
  Solutions); if it stops firing, the last point is never graded and the
  ceremony has nothing to say about it.
- Keep the game's existing behaviour in the Editor (both calls are
  `#if UNITY_WEBGL && !UNITY_EDITOR`, so they log and do nothing there).
- No change to `MHSBridge.cs`, `MHSBridge.jslib`, the WebGL template, or
  the identity/service configuration.

## 4. The Congratulations screen: your choice

**Keep it.** Then call `CompleteUnit("unit5", null)` when Unit 5 is finished
(before the screen appears) and keep `EndGame()` on the button. The copy
should say what happens next, because the button now leads into the
ceremony, not back to Strata. Our recommendation:

> **Congratulations, you have completed Mission HydroSci!**
>
> Your crew is waiting to celebrate with you.
>
> [ **Continue to the celebration** ]

**Remove it.** Then call both at the portal, in order:

```csharp
MHSBridge.Instance.CompleteUnit("unit5", null);
MHSBridge.Instance.EndGame();
```

and the browser leaves the game for the ceremony at once. A brief in-game
moment at the portal (a fade, a sound) is fine either way, as long as
`CompleteUnit("unit5", null)` is called when Unit 5 is finished and
`EndGame()` when the game is over.

## 5. What happens after each call, on each host

| Host | On `CompleteUnit("unit5", null)` | On `EndGame()` |
|---|---|---|
| StrataHub (PWA mode: the host page called `OnPWAReady`) | The page records Unit 5 as complete and hides its "loading next unit" overlay. The game keeps running; nothing else happens. | The page records the end of the game (and Unit 5 as complete if that call never came), tears down the Unity instance and opens the ceremony, which starts playing on its own. |
| Outside StrataHub (URL mode) | No-op (null next URL). | The bridge's existing fallback: tries to close the tab, else shows "Game Complete — you can close this tab". |

## 6. Edge cases we checked

- **A student who replays Unit 5** (it stays playable after completion):
  both calls are idempotent on the StrataHub side; the ceremony opens again
  and can be replayed as often as they like.
- **The request fails** (offline, timeout): StrataHub queues it and flushes
  it from the launcher later; the student lands on the launcher and the
  ceremony becomes available once the queue drains.
- **Double firing.** The grading team noted that `questActiveEvent:36` and
  `questFinishEvent:45` sometimes fire twice. It does not affect the
  hand-off (a repeated completion or end is ignored), but if you are in
  that code anyway, a single firing would be cleaner for grading.
- **A teacher ends the game for a student** (from the dashboard, for a
  student who could not finish): the student sees the ceremony from the
  launcher; nothing in the game changes.

## 7. Verification on Dev MHS

1. Deploy the build to a collection on the Dev MHS workspace (MHS Builds →
   Upload, then activate) with the ceremony selected in the collection (it
   is already set on the current Dev MHS collection).
2. As a member whose current unit is Unit 5, play to the base portal and go
   through it.
3. Expected: the Congratulations screen with the new copy (if kept), then
   the browser leaves the game and the ceremony starts playing on its own
   after a few seconds of loading (longer on a school network); it plays
   through to the star board. In the browser console on the game tab the
   bridge logs `MHS: mhsUnitComplete called with unit5` and then
   `MHS: mhsEndGame called`.
4. Back on the Mission HydroSci launcher: "Mission Complete" with a "Watch
   your ceremony" button, and the End-of-game ceremony row reads "Watch".
5. Staff check: the student's row on the MHS Dashboard shows a trophy mark
   beside their name once the ceremony has started for them, and the Device
   Tests viewer has a `ceremony-ok` row under unit `end`.

If step 4 shows Unit 5 still "Ready to play" (with the ceremony available),
the `CompleteUnit` call did not reach StrataHub: check the console for the
`mhsUnitComplete` line and for a failed `POST /missionhydrosci/api/progress/complete`.
If the ceremony did not open at all, the `EndGame` call did not reach it:
check for the `mhsEndGame` line and `POST /missionhydrosci/api/progress/end-game`.
