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

`SendQueuedLogs` reads the head of the queue like this:

```csharp
Dictionary<string, object> logData = new();
lock (queueLock) { if (logQueue.Count > 0) logData = logQueue.Peek(); }
if (logData.Count == 0) break;   // "queue is empty"
```

An **empty dictionary at the head is indistinguishable from an empty queue**:
the loop exits, `isSendingLogs` goes back to false, and every later
`SendToServer` starts a coroutine that hits the same `{}` and exits at once.
Nothing is ever sent again. The entries pile up behind the `{}`, the cache is
written on every enqueue with the `{}` still first, and every later launch
reloads it that way. That is the incident exactly: zero requests per
session, every session, on every build, until the store is cleared.

How the head becomes `{}`: the queue holds **the caller's dictionary object**
(`SendToServer` enqueues the reference it is given). The session's first
event is the debug-menu state event, fired before identity is set (which is
also why it goes out with `user_id: null` and is normally rejected with 400).
It was serialized and POSTed intact at 06:43:26.556, and 150 ms later, when
the next event's enqueue wrote the cache, the same object was empty. Something
on the caller's side cleared or reused that dictionary after handing it in.
(The exact line is in the game project, not in this bundle: look at whoever
logs `DEBUGMenu` / `DebugMenuStateChanged`, and at any code that reuses a
dictionary across log calls.)

Why it does not bite every session: normally the first POST is answered
within a fraction of a second, either 201 or the 400 for the missing
`user_id`, and both paths **dequeue** the entry before the caller clears it.
The clear then empties an object nobody holds. It bites only when the first
POST fails at the network level, so the entry is still at the head, waiting
for the ten-second retry, when it is emptied. One blip at launch, and that
browser profile never logs again.

## What this changes in the fix

The drop-in `GameLogger.cs` in this folder predates this finding. It already
never exits on an empty head (its loop stops only when the queue is empty,
and an empty entry would go out, be refused with 400, and be dropped). Two
additions are still required, and are not yet in the drop-in:

1. **Copy on enqueue.** `SendToServer` must store its own copy:
   `logQueue.Enqueue(new Dictionary<string, object>(logData))`, so a caller
   reusing or clearing its dictionary cannot alter a queued entry. This is the
   root fix.
2. **Skip empty or unreadable entries when loading the cache**, so a store
   written by the current build (with a `{}` in front) drains on the first
   launch of the fixed build instead of sending a `{}` first.

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
