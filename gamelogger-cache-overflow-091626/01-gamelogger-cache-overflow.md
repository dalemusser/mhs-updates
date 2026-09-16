# GameLogger — unsent-log cache overflows the WebGL PlayerPrefs limit

Audience: the developer who owns `Assets/Scripts/Systems/Logging/GameLogger.cs`.

Status: root cause confirmed from the game source, the stratalog server
source, and Unity's documentation. The drop-in `GameLogger.cs` in this
folder implements every change below. It compiles cleanly with Roslyn
(C# 9) against stubs of the Unity APIs it uses, for both the `UNITY_WEBGL`
and standalone defines, but it **has not been built or run inside the
Unity project**. Treat the "Verification" section as required, not
optional.

---

## Symptom

On one Windows classroom machine, the browser console fills with this while
Mission HydroSci is running:

```
Failed to save cached logs (WebGL): Could not store preference value
```

It repeats on every game event (dozens of times per minute once the player
is moving and talking to characters). The same build logs normally on other
machines. On the affected machine the game otherwise plays, and the state
load from `save.adroit.games` succeeds in the same console.

`Could not store preference value` is the message of Unity's
`PlayerPrefsException`, thrown by `PlayerPrefs.SetString` when the store
refuses the write.

---

## What is happening

### 1. How the logger caches events

`GameLogger` keeps every event in an in-memory queue until the server
accepts it. So that events survive a reload, the **entire queue** is
serialized into **one PlayerPrefs string** (key `game_logs_cache.json`):

```csharp
// GameLogger.cs (2026-05-26 drop), SaveCachedLogsWebGL()
List<string> logList = new List<string>();
foreach (var log in logQueue)
    logList.Add(JsonConvert.SerializeObject(log));
string json = JsonUtility.ToJson(new LogListWrapper(logList));
PlayerPrefs.SetString(logCacheFile, json);
PlayerPrefs.Save();
```

This runs on every enqueue (`SendToServer`) and after every successful
send.

On WebGL, Unity keeps PlayerPrefs in the browser's IndexedDB and caps the
whole store at **1 MB** (Unity's PlayerPrefs reference: "WebGL: Unity
stores up to 1MB of PlayerPrefs data using the browser's IndexedDB API").
Once the serialized queue is larger than that, every `SetString` throws.
The `catch` logs the error and moves on, so:

* the in-memory queue keeps growing for the rest of the session;
* nothing new is persisted, so anything queued after the last successful
  write is lost on reload;
* the console error repeats on every event.

### 2. Why the queue got that big

The send loop only ever discards an entry when the server answers **400**.
Every other outcome retries the *same head entry* every ten seconds,
forever:

```csharp
// GameLogger.cs (2026-05-26 drop), SendQueuedLogs()
else
{
    ConsoleLogManager.Instance.PrintToConsole($"Log Post Failed: ...");
    if (request.responseCode == 400)
    {
        logQueue.Dequeue();
        continue;
    }
    yield return new WaitForSeconds(10); // Retry after a delay
}
```

| Outcome of the POST | What the loop does |
|---|---|
| 2xx | dequeue, save cache, next entry |
| 400 (invalid entry) | dequeue, next entry |
| connection failed / blocked / DNS / timeout | retry same entry every 10 s |
| 401, 403, 413, 5xx | retry same entry every 10 s |

So one POST that can never succeed wedges everything behind it. The queue
only grows, and the cache with it, until the 1 MB cap. At roughly 500 to
900 bytes per cached entry (see §4), that is about 1,200 to 2,000 events,
which is well within one or two class periods.

### 3. Why one machine and not the others

Three things are specific to a machine, and any of them explains it:

* **The network path to `log.adroit.games`.** School filters and proxies
  commonly allow-list by host. The console on the affected machine shows
  `save.adroit.games` working; nothing in it shows whether the log host is
  reachable, because the "Log Post Failed" line goes to the in-game debug
  console, not the browser console.
* **PlayerPrefs are per browser profile per origin, not per student.**
  Every account that plays in that Windows browser profile appends to the
  same cache. A shared lab machine fills it much faster than a personal
  one, and the student whose session hits the cap is just whoever was
  there when it happened.
* **A wedged head entry.** One entry that the server answers with anything
  other than 2xx or 400 (a 413, an auth failure while the key was
  misconfigured, a 500 during a server incident) stays at the head until
  the page is reloaded with a working path. Entries behind it pile up.

The API key is static (it comes from the stratahub host page config), so
token expiry is not a factor.

### 4. Secondary costs of the current design

These are not what the user saw, but they are why the fix restructures the
cache rather than only catching the exception:

* **Each cached entry carries the full device block.** The send loop
  mutates the queued dictionary (`logData["device"] = cachedDeviceInfo`),
  so after one send attempt every cached copy includes the GPU name, OS
  string, resolution, etc.
* **The cache is double-encoded.** Each entry is serialized to a JSON
  string, then that string is embedded in another JSON document, so every
  quote is escaped. That inflates the cache by roughly 20%.
* **Every event re-serializes the whole queue and calls
  `PlayerPrefs.Save()`** (an IndexedDB sync). With a large backlog this is
  a per-event hitch on low-end Chromebooks, exactly when the machine is
  already struggling with a broken log path.

---

## How to confirm on an affected machine

1. **Network tab.** DevTools → Network → filter `log.adroit.games`. Watch
   the POST to `/api/log/submit`. Anything other than `201` is the real
   cause: `(blocked)`, `(failed)`, `401`, `413`, `500`. The response body
   is JSON with a `code` field.
2. **Cache size.** DevTools → Application → IndexedDB → `/idbfs` →
   `FILE_DATA`. Unity's PlayerPrefs live in a file whose path ends in
   `/PlayerPrefs`. The `contents` byte length is the store size; if it is
   near 1,048,576 the cache has hit the cap.
3. **Server side.** In stratalog's Log Browser, filter on the student's
   `user_id` (shown in the console as `User ID is <24 hex>`). No entries
   from that device on that day confirms nothing is getting through.

---

## Immediate remedy for the affected machine (no code change)

Clear site data for the game's origin in that browser profile (padlock in
the address bar → Site settings → Delete data; or DevTools → Application →
Storage → Clear site data). That resets PlayerPrefs.

* Lost: the unsent cached logs on that machine. They were not going
  anywhere.
* Not lost: game progress and settings, which live server-side in
  stratasave. The game's assets will re-download once.
* If the cause is a blocked log host, the cache refills. The network
  problem has to be fixed as well.

---

## Required changes

All five are in the drop-in `Game-Code/Systems/Logging/GameLogger.cs`. They
are described separately so they can be reviewed separately, but they are
meant to land together.

### Change 1 — bound the cache (the actual fix)

Keep the serialized queue under a budget well below Unity's cap, dropping
the **oldest** entries first, and never let a write failure spam the
console.

```csharp
private const int   maxCacheBytes     = 512 * 1024; // Unity WebGL PlayerPrefs cap is 1 MB, all keys combined
private const int   maxCacheEntries   = 1500;       // bounds the per-save serialization cost
private const float dropWarningInterval = 60f;
```

`SerializeQueueTrimmed()` serializes the queue, and while the result is
over `maxCacheBytes` it dequeues a proportional slice of the oldest entries
and re-serializes (converges in one or two passes). Dropped counts are
accumulated.

When entries have to be dropped, that is reported **to the server** once
the connection is back, as a `LogCacheOverflow` event
(`data.dropped`, `data.droppedTotal`), so a gap in a student's data is
visible in stratalog rather than silent.

`SaveCachedLogsWebGL` still catches exceptions (a private window or a
browser with site data blocked can refuse IndexedDB writes), but it logs a
**warning once per session** and carries on in memory. `Debug.LogError` is
gone from this path: the logger is best-effort and must never look like a
crash.

Why 512 KB and not "just under 1 MB": the cap covers every PlayerPrefs key
the game writes, not only this one, and Unity does not document whether the
limit is measured in bytes or UTF-16 characters. Halving it removes both
questions.

### Change 2 — stop wedging on one entry

Only outcomes that can never succeed on retry drop the entry:

| Outcome | New behaviour |
|---|---|
| 2xx | remove the sent entries, reset backoff |
| 400 on a single entry | drop it (log a warning with the server's reason) |
| 400 on a batch | resend that batch one entry at a time so only the bad one is dropped (stratalog rejects a whole batch if any entry is invalid) |
| 413 on a batch | halve the batch size and retry |
| 413 on a single entry | drop it (it is over the server's 1 MB body limit) |
| anything else (no network, 401/403, 5xx, timeout) | keep, retry with backoff 10 s → 20 s → 40 s → 60 s cap |

Retrying forever on network and server failures is deliberate and matches
the rest of the platform. It is safe now only because Change 1 bounds what
can accumulate meanwhile.

### Change 3 — keep cached entries small

* `device` is attached to a **shallow copy** at send time (`WithDevice`),
  so the queued entry never grows. Every entry the server receives still
  has the device block, which the stratahub Devices tab depends on.
* The cache is one JSON array of entries, serialized once with
  Json.NET, instead of a list of pre-serialized strings inside a
  `JsonUtility` wrapper.
* `ParseCache` reads **both** formats. A cache left behind by the current
  build is sent after the upgrade, not lost. The `LogListWrapper` class
  stays for that reason only.
* An unreadable cache is discarded on load (with a warning). Previously it
  would fail the same way on every launch forever.

### Change 4 — coalesce cache writes

`MarkCacheDirty()` writes immediately if the last write was more than
`cacheSaveInterval` (2 s) ago; otherwise `Update()` writes once when the
interval elapses. `OnApplicationPause(true)`, `OnApplicationFocus(false)`
and `OnApplicationQuit` flush immediately. A burst of events becomes one
serialization and one IndexedDB sync instead of one per event. The worst
case on a hard crash is losing the last two seconds of events, and the host
page's own crash reporter covers that case.

### Change 5 — send in batches

When more than one entry is queued, up to `maxBatchEntries` (50; the
server default limit is 100) go out in stratalog's batch envelope:

```json
{ "game": "mhs", "entries": [ { ...entry... }, { ...entry... } ] }
```

A backlog of 1,000 entries drains in 20 requests instead of 1,000. A single
entry is still sent bare, exactly as today. Batch-level rejections are
handled as in the Change 2 table.

---

## Server-side facts the client now relies on

From `stratalog/internal/app/features/logapi/handler.go` and
`system/auth/apikey.go`:

| Response | Meaning |
|---|---|
| `201` `{"status":"success","received_at":...}` | stored (single or batch) |
| `400` with `code` `MISSING_FIELD` / `INVALID_USER_ID` / `DEPRECATED_FIELD` / `INVALID_JSON` / `BATCH_TOO_LARGE` / `EMPTY_ENTRIES` / `INVALID_ENTRY` | rejected; for a batch, the **whole batch** is rejected on the first invalid entry, none are stored |
| `413` `BODY_TOO_LARGE` | request body over 1 MB |
| `401` | missing or wrong API key |
| `500` `INSERT_FAILED` | database write failed; safe to retry |

`max_batch_size` defaults to 100 entries. Both `/api/log/submit` and the
legacy `/logs` route accept the same single and batch payloads.

---

## How to apply

1. Copy `Game-Code/Systems/Logging/GameLogger.cs` over
   `Assets/Scripts/Systems/Logging/GameLogger.cs`.
2. Review the constants at the top of the file. None need changing for
   production; they are grouped there so they can be tuned without reading
   the rest of the file.
3. Build and run the verification below. No prefab, asset, or host-page
   change is involved; the public API of `GameLogger` (the eight
   `Log…Event` wrappers, `LogEvent`, `SendToServer`, `SetUserId`,
   `Instance`) is unchanged.

---

## Verification

**A. Normal path.** Play in a browser tab logged into StrataHub. In
DevTools → Network, POSTs to `/api/log/submit` return `201`. Isolated
events go out as a bare entry; bursts (e.g., start of a scene) go out as a
batch envelope with an `entries` array.

**B. Offline path (the bug).** DevTools → Network → Offline. Play for
several minutes (dialogue, movement). Expected:

* No `Failed to save cached logs` lines. At most one
  `GameLogger: unsent log cache over budget; dropped …` warning per minute,
  and only after a long time offline.
* Application → IndexedDB → `/idbfs` → `FILE_DATA`: the PlayerPrefs file
  stays under ~600 KB.
* Reload the page while still offline: the game starts, and the queued
  entries are loaded (the first send attempts after going online carry
  events from before the reload).
* Switch Network back to Online: the backlog drains in batches; if any
  entries were dropped, a `LogCacheOverflow` event arrives at the server.

**C. Upgrade path.** On a machine (or profile) that still has a cache
written by the current build, load the new build. The old entries are sent
(Network tab shows them), and the PlayerPrefs value is rewritten in the new
array format on the first save.

**D. Rejected entry.** Temporarily call `SetUserId("not-a-user-id")`,
trigger an event, then restore the real id and trigger another. The bad
entry produces one `400` and one warning and is dropped; the good entry is
delivered. The loop does not stall.

---

## Out of scope / follow-ups

* **Legacy fallback URL.** `logServerUrl` still points at the legacy
  `/logs` route when the host page supplies no config. Unchanged here;
  see `mhsbridge-userid-cleanup-052626/RECOMMENDATIONS.md` §3.
* **A different store.** On WebGL `Application.persistentDataPath` is also
  IndexedDB-backed (`/idbfs`) and has no 1 MB cap, but writes there are
  not persisted until a JavaScript `FS.syncfs` call, which is presumably
  why PlayerPrefs was chosen. With the cache now bounded, there is no
  pressing reason to move it.
* **Surfacing log health in the host page.** The stratahub loading-status
  panel could report the last log POST result so a blocked log host is
  visible to the teacher without DevTools. That is a stratahub change, not
  a game change.
