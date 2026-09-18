# Reproduction, specimen, and the mechanism

Added 2026-09-18, after the failure was reproduced on purpose in a browser
with the log host blocked. This supersedes the "leading hypothesis" wording
in `01-gamelogger-cache-overflow.md` §2: the mechanism is now observed, and
a captured store is in `specimen/`.

## Reproduce it in two minutes

1. In the browser, block requests to the log service host (Chrome DevTools →
   Network → Network request blocking → add the host → enable; or any
   equivalent). Leave everything else reachable.
2. Launch a unit and press through to gameplay. Move around for a minute.
3. Unblock the host. Keep playing.

Expected from the current `GameLogger.cs`: the game makes **two** POSTs to
the log host at startup (the first event and one retry ten seconds later),
both fail, and it never makes another request for the rest of the session,
blocked or not. Every event from then on is written to the PlayerPrefs
cache. Reload the page: still nothing is sent, because the cache is reloaded
with the poison entry (below) still in front. The only way out is clearing
the site's data.

Observed 2026-09-18 on a device-test run (run id beginning `ffffffff06ea`,
unit 2 v2.8.1, headless Chromium, the host blocked for the first six minutes
of the session):

| Time (UTC) | What the browser saw |
|---|---|
| 06:43:26.556 | POST `/api/log/submit` — refused (blocked). Body: the session's first event, `DEBUGMenu` / `DebugMenuStateChanged`, scene `MainMenu`, **`user_id: null`**, with the `device` block attached. |
| 06:43:36 | The same POST again (the ten-second retry) — refused. |
| 06:43:36 → 06:55 | **No further requests to the log host, ever.** Not while blocked, not after unblocking at 06:48. |
| 06:43 → 06:55 | The PlayerPrefs store grew from 1.8 KB to 21 KB (80 cached entries). |
| 06:55 | Page reload: the game reloads the cache and makes **no** request. |

The full request list is `specimen/browser-requests-2026-09-18.txt`.

## The specimen

`specimen/PlayerPrefs-2026-09-18-blocked-host.bin` is the game's PlayerPrefs
file as Unity stored it in IndexedDB (`/idbfs/<hash>/PlayerPrefs`, Unity's
`UnityPrf` binary format), exported from the browser at 06:53 UTC, before the
reload. `specimen/game_logs_cache-2026-09-18.json` is the value of the
`game_logs_cache.json` key from it, decoded into a JSON array.

The cache holds 80 entries. **Entry 0 is `{}`**, an empty object. Entries 1
to 79 are real events, in order, starting with `gameWindowFocusEvent` and
`gameStartEvent` at 06:43:26.702, then `DEBUGMenu`, `PlayerPositionEvent`,
`DialogueEvent`, `InputEvent` from gameplay. None of the 79 carries a
`device` block. The `DEBUGMenu` event that was actually sent at 06:43:26.556
is not in the cache; its slot at the head is the `{}`.

## The mechanism

Two further runs on 2026-09-18 sampled the store every few seconds through the
menu and into the gameplay scene (run ids beginning `ffffffff3b3b` and
`ffffffff92d6`). Together with run `ffffffff06ea` they give this sequence,
the same every time:

1. An event is queued and sent. With the host blocked the POST fails at the
   network level.
2. Ten seconds later the **same entry** is sent again (the bodies of the two
   requests are identical). It fails again.
3. **There is never a third attempt.** Not ten seconds later, not after the
   host is unblocked, not after a reload.
4. The next time the cache is written (the next event queued, which in these
   runs happened to be the gameplay scene loading), the entry at the head of
   the queue is `{}`: every key gone, the device block gone. Everything queued
   after it is intact behind it.

Run `ffffffff92d6` rules out the identity angle: the host was reachable at
first, so the session's null-id first event was rejected with 400 and
dropped and two more were accepted; the host was then blocked and a
window-unfocus event, with a proper user id, became the head. It went
through exactly the same three steps and became the `{}`.

What the loop does with an empty head:

```csharp
Dictionary<string, object> logData = new();
lock (queueLock) { if (logQueue.Count > 0) logData = logQueue.Peek(); }
if (logData.Count == 0) break;   // "queue is empty"
```

An empty dictionary at the head is indistinguishable from an empty queue:
the loop exits, `isSendingLogs` goes back to false, and every later
`SendToServer` starts a coroutine that meets the same `{}` and exits at
once. Nothing is ever sent again. The cache is rewritten on every enqueue
with the `{}` still first, and every later launch reloads it that way. That
is the incident exactly: zero requests per session, every session, on every
build, until the store is cleared.

**What empties the entry.** The emptying happens inside the logger, between
the second failed attempt and the next loop iteration. The `GameLogger.cs`
in the MHS 2.0 project copy we have (and the May drop's) has no code that
removes keys from a queued entry and would keep retrying every ten seconds
forever, so the shipped build (unit builds of 2026-09-14) carries a newer
send loop with a limit on attempts. The evidence says that limit is
implemented by **clearing the entry's dictionary instead of dequeuing it**
(`logData.Clear()` or equivalent where `logQueue.Dequeue()` was meant). Look
at the code that runs after a non-400 failure once a retry has been made.

**Being fully offline is the same case.** A run with the browser reporting
offline (`navigator.onLine === false`, run id beginning `ffffffff6952`):
the game still made the attempt and the ten-second retry, both failed, and
after the connection returned it never sent again; the failed entry was
`{}` at the next cache write. The `IsNetworkAvailable()` check
(`Application.internetReachability`) does not stop the loop on WebGL. So any
interruption of the log path lasting more than about ten seconds during
play, from any cause, ends logging on that browser profile until its site
data is cleared.

Why it does not bite every session: the first attempt normally succeeds or
is rejected with 400 within a fraction of a second, and both paths dequeue
the entry. It takes a network-level failure of the same entry twice, ten
seconds apart, at any point in a session. One brief outage, and that browser
profile never logs again.

**A second defect in the same specimen.** Every entry queued from a
`LoggingData` asset carries the asset's one reusable `logDictionary` as its
`data` (`LoggingData.cs`, `ExecuteLogData` clears and refills it on every
call, and `GameLogger.LogEvent` stores the reference). While sending works
the body is serialized at once, so it is correct; but any entry that waits
in the queue has its `data` overwritten by the next event from the same
asset. In the specimen, entry 5 has the key of dialogue 16 node 3 and the
data of dialogue 18 node 1, and all seven cached input events say key "w"
although a, s and d were pressed too. A backlog sent after an outage
carries wrong payloads. Copying on enqueue fixes this as well.

**A structural risk worth removing.** The `GameLogger` component lives on
the `ConsoleLogManager` object in both `CoreSystems_MainMenu.prefab` and
`CoreSystems_Gameplay.prefab`, so a second instance awakes at the gameplay
scene, `Awake` never assigns `_instance` (only the getter does), and the
duplicate check calls `Destroy(gameObject)` on an object it shares with
another singleton. With `isSendingLogs`, `queueLock` and the user id all
static and the queue per instance, any path that leaves two loggers alive,
or destroys the one holding the running coroutine, strands the flag. None of
the runs above needed this to fail, but it should not be there.

## What this changes in the fix

The drop-in `GameLogger.cs` in this folder predates this finding. It already
never exits on an empty head (its loop stops only when the queue is empty,
and an empty entry would go out, be refused with 400, and be dropped), and it
never gives up on an entry except on 400 or 413, so the shipped build's
"clear after the retry" step has no counterpart in it. Three additions are
still required, and are not yet in the drop-in:

1. **Copy on enqueue.** `SendToServer` must store its own copy:
   `logQueue.Enqueue(new Dictionary<string, object>(logData))`, so the
   reusable `LoggingData` dictionaries cannot alter a queued entry's data.
2. **Skip empty or unreadable entries when loading the cache**, so a store
   written by the current build (with a `{}` in front) drains on the first
   launch of the fixed build instead of sending a `{}` first.
3. **Diff the shipped `GameLogger.cs` against this drop-in before applying
   it**, so whatever was added to the send loop after the May drop (the
   attempt limit) is not carried forward by mistake.

The bounded cache, the drop-only-on-400/413 rule, and the per-iteration
guards in the drop-in remain necessary: they stop any *other* way of wedging
the sender from growing the store to Unity's 1 MB cap.

## Verifying the fix

Repeat the reproduction above with the fixed logger. Expected: while the
host is blocked, the game keeps retrying (a request every ten seconds, with
backoff) and the store grows; after unblocking, the backlog drains within a
minute and the store shrinks back; a reload drains it too. StrataHub's
Devices tab should show the device's Logs cell going from "Not recorded" to
a check mark within ten minutes of the unblock.
