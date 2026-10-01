# The game's logging, if we started over

**Date:** 2026-09-30
**Status:** a design note, nothing implemented. The delivered fix (`01-changes.md`, the three files under `Game-Code/`) is version 1 (now 1.3) and stands on its own; this note is the basis for a possible version 2, to be picked up later as its own project.
**Audience:** the project lead and the game team.

## 1. What exists today

Events reach the log service like this:

1. **Where events come from.** Two paths. Code calls one of `GameLogger`'s typed wrappers (`LogDialogueNodeEvent`, `LogDialogueEvent`, `LogQuestEvent`, `LogPlayerPositionEvent`, `LogTopoMapEvent`, the three argumentation wrappers) and each builds a dictionary from its parameters. Or a component holds a `LoggingData` asset: a ScriptableObject in which designers declare, in the Inspector, an event type, a list of fixed key/value strings (`specificEventInformation`) and a list of variable fields (`logEntries`, typed by the entry class: `StringEntry`, `IntEntry`, `BoolEntry`, `Vector3VariableEntry`, …). The component sets the variable fields with `SetValues` and calls `ExecuteLogData`, which turns the lists into a dictionary and hands it to `GameLogger.LogEvent`. There are 21 such assets, used by about twenty scripts (among them `DebugMenuController`, `QuestEventValueListener`, `InputActionLogger`, `SoilKeyPuzzle`, `SoilMachine`, `GardenBoxLogger`, `ArgumentationLogger`, `LogUsableUsage`, `LogUnit5DungeonWaterChambers`, `LogEndOfUnit`, `LogPuzzleViewManager`, `LogDaniMenuEvents`, `LogVisibleConversationLines`, the map scripts `LogMapLegendEvents`, `LogMapActiveEvents`, `LogWaypointPlaced`).
2. **The envelope.** `LogEvent` wraps the data in a second dictionary: `game`, `user_id`, `version`, `sceneName`, `timestamp`, `eventType`, `data`, and `eventKey` for dialogue-node and quest events. The `device` block is attached at send time.
3. **The transport.** `GameLogger` (a `MonoBehaviour` on the console-manager object of both core-systems prefabs) queues the entries, persists the whole queue to one PlayerPrefs key (Unity caps WebGL PlayerPrefs at 1 MB), and a coroutine sends batches of up to 50 in stratalog's envelope `{"game":"mhs","entries":[…]}`, retrying with backoff.
4. **Serialization.** Newtonsoft Json.NET (`com.unity.nuget.newtonsoft-json`), because `JsonUtility` cannot serialize dictionaries. As far as the project copies show, the logger is the only live user of Newtonsoft in the game (one archived script under `_Archive` also references it).
5. **Who reads it.** stratalog stores each entry as a document; mhsgrader matches mostly by `eventKey` and reads `data` for six rules; the StrataHub dashboard and the research partner read `data` by field name. The field names in `data` are therefore a contract.

Version 1 (the delivered fix) keeps all of this and repairs the defects inside it: entries are copied on enqueue, per-send state is local, unsendable entries are dropped, one instance, identity stamping, hardening.

## 2. Why a version 2 would be worth it

The defects we fixed were symptoms of the shape of the code, and the shape invites them back:

- **Mutable bags passed by reference.** A `Dictionary<string, object>` built by one component and handed to the logger can be changed by anyone who still holds it. Defect 3 (details overwritten while queued) was exactly that, in two places, for seven months. Version 1 copies on enqueue; a design in which an entry cannot be changed after it is logged needs no such discipline.
- **A schema made of strings.** Field names are typed by hand in code and in 21 assets. `LoggingData.cs` has `"DialogeNodeEvent"` where `"DialogueNodeEvent"` was meant; nothing can catch that at compile time, and there is no single place that says what each event type contains.
- **Reflection-based serialization of whatever is in the bag.** Newtonsoft serialized the Soap variable *objects* (name, hide flags, listeners…) into `data` because that is what was put in the dictionary. A typed record cannot carry a surprise.
- **Transport state spread across static fields, instance fields and two prefabs.** Defects 1, 2 and 5 were per-send state in fields, a loop that read an emptied entry as an empty queue, and two instances sharing a static flag. Version 1 contains these; a transport that is a plain class with one owner removes the conditions.
- **Logging before identity.** The session's first event is logged by a component destroying itself in `Awake`, before anyone knows who the player is. Version 1 holds and stamps it; a design where the logger is not available until identity is set removes the case.
- **Nothing testable without a build.** The send loop can only be exercised in a browser against the real service. Finding defect 2 took a reproduction on the dev site and a port of the loop to Python; it should have been a unit test.
- **`JsonUtility` has sharp edges too**, which matters if version 2 leans on it: it throws on `null`, on an array at the root and on any non-object body (the settings-load console error of `01-changes.md` §8), it cannot serialize `object`-typed fields, dictionaries or polymorphic values, and it serializes only public fields and `[SerializeField]` fields.

## 3. Principles for version 2

1. **An entry is immutable once logged.** It is rendered to its final JSON text (or a sealed record with only readonly fields) at the moment of logging. The transport never changes an entry. The one exception, stamping a user id that was unknown when the event happened, is done by rendering a new entry, not editing the old one.
2. **Typed event records are the schema.** One small `[Serializable]` record type per code-defined event, with typed fields. The record types are the documentation: a schema page for analysts can be generated from them.
3. **The wire format does not change.** The envelope fields, the `data` field names per event type, the `eventKey` formats and the `device` block stay byte-for-byte compatible. Everything downstream keeps working, and the test that proves it is part of the project (§6).
4. **The transport is a plain C# class with no Unity dependency.** Queue, persistence budget, batching, retry policy and identity gating take an injected clock, an injected sender and an injected store, so they run in Unity's test runner (and in a plain .NET test project) without a build or a browser.
5. **One logger, created on purpose.** A single bootstrap object creates the logger once; prefabs do not carry copies.
6. **Identity is a precondition.** No entry is sendable without a user id; events before identity wait in memory and are stamped when it arrives, as in version 1. The debug-menu controller stops logging while destroying itself.
7. **Keep what version 1 got right.** Bounded cache with oldest-first trimming and a `LogCacheOverflow` report, coalesced writes, batches of at most 50, retry forever with backoff (never give up, the project's rule), drop only what the server has said it will never accept, a pause after a refusal, a request timeout, removal by identity, a capped unit-transition wait.
8. **No external JSON library** if it can be avoided cleanly (§5).

## 4. Proposed shape

```
EventRecords/            one [Serializable] record per code-defined event type
  DialogueNodeEvent { int conversationId; int nodeId; }          eventKey "DialogueNodeEvent:{c}:{n}"
  QuestEvent        { string questEventType; int questId; string questName; string questSF; }
  PlayerPositionEvent { Vec3 position; }                          (Vec3 = {x,y,z} floats, NaN → 0)
  TopographicMapEvent, ArgumentationEvent, ArgumentationNodeEvent, ArgumentationToolEvent …
  KeyValueEvent     { string eventType; Field[] fields; }         the designer-authored path (LoggingData)

LogEntry (immutable)     game, userId (may be null until stamped), version, sceneName, timestamp,
                         eventType, eventKey, dataJson (rendered once), sessionId, seq, recovered
                         (§8), + WithUserId(id) → new LogEntry

JsonWriter               ~100 lines: objects, arrays, strings (escaped), numbers (invariant culture,
                         NaN/∞ → 0), bools, null. Renders records, the envelope, the device block,
                         the batch, and the cache.

LogTransport (pure C#)   Enqueue(LogEntry) · SetUserId(id) · Tick(now) · the policy:
                         batches ≤ 50, backoff 10→60 s, drop on 400 (single) / 413 (single),
                         single-send isolation after a batch 400, pause after a refusal,
                         bounded cache, coalesced writes, identity gating, remove-by-identity.
                         Depends on: IClock, ILogSender (Send(bodyText) → status/outcome),
                         ILogStore (Load() / Save(text)).

Unity adapters           UnityLogSender (UnityWebRequest, 30 s timeout),
                         PlayerPrefsLogStore (the 1 MB cap; or a file under persistentDataPath),
                         GameLoggerBehaviour (one MonoBehaviour: drives Tick, flushes on pause/focus/quit,
                         answers ICheckForUnitTransition with the capped wait).

GameLogger facade        keeps today's public API (Instance, LogEvent, the eight wrappers, SetUserId,
                         IsSendingLogs) so call sites do not change in the first cut;
                         LogEvent(string, Dictionary) becomes a thin shim that renders to a KeyValueEvent
                         and can be removed once callers are typed.
```

The designer-authored path is kept: `LoggingData` assets still declare events in the Inspector, but `ExecuteLogData` produces a `KeyValueEvent` whose fields are rendered by `JsonWriter` as an object with named keys, so `data` looks exactly as it does today. Over time the most important of those events (dialogue, quest, puzzle piece, soil machine, water chamber, the ones the grader reads) move to typed records.

The cache stays a list of pre-rendered entry strings, as the 2026-09-14 build's format was (`{"logs":["<entry json>", …]}`); reading it back needs no parsing of entry contents, only the envelope's `user_id` presence and `eventType`, which the transport keeps alongside each string. Loading the current builds' formats (version 1's array of objects, the older wrapper of strings) once, so a cache left by an earlier build is sent rather than lost, is a small compatibility step in the store adapter.

## 5. The serialization choice

| Option | For | Against |
|---|---|---|
| **Newtonsoft (today)** | Handles anything; already in the project. | A dependency used by one file; reflection over whatever is in a dictionary (the Soap-object bloat); IL2CPP stripping risks; it is what makes dictionaries convenient, and dictionaries are the problem. |
| **JsonUtility** | Built in, fast, fine for `[Serializable]` records and generic wrappers (`Wrapper<T>` already works in `JsonHelper`). | No dictionaries, no `object` fields, no polymorphism; root must be an object; throws on `null`/arrays/non-objects (§8 of `01-changes.md`); float formatting not controllable; writing the envelope around a record means string assembly anyway. |
| **Hand-written writer** | About a hundred lines; deterministic output; invariant culture; no reflection, nothing to strip; renders records, key/value lists, the envelope, batches and the cache with one code path; trivially unit-tested. | One more piece of code to own (but far smaller than either library's surface, and the output is pinned by the golden test of §6). |

Recommendation: the hand-written writer for everything the game writes; `JsonUtility.FromJson` only to read the cache wrapper (a list of strings) and only inside a try/catch that discards an unreadable cache. Newtonsoft can then be removed from the project.

## 6. The compatibility test that makes this safe

Before any code: capture **golden samples**, one real entry per event type, from the current build's output (the log service's data has every type; the specimen stores in `../gamelogger-cache-overflow-091626/specimen/` have a few). Normalise the volatile fields (`timestamp`, `version`, `sceneName`, `user_id`, `device`) and keep the rest literally. Then a unit test renders the same events through version 2 and compares the JSON object-for-object (key names, nesting, value types). The test is the definition of "the wire format did not change", and it runs in the Editor in seconds.

The seven browser checks of `00-plan.md` stay as the acceptance test on the dev site, and the read-only queries in `tools/queries/` give the server-side numbers.

## 7. Steps, if and when this is done

1. **Golden samples and the writer** (half a day): capture samples; write `JsonWriter`; the golden test.
2. **Transport core with tests** (one to two days): `LogTransport` as a plain class; port the twelve checks of `tools/send-loop-simulation-fixed.py` into Unity test-runner tests, plus tests for the cache budget, identity gating, removal by identity and the transition wait.
3. **Adapters and the facade** (one day): `UnityLogSender`, the store, the single `GameLoggerBehaviour`, the facade keeping today's API; remove the logger from the two core-systems prefabs and add the bootstrap object; stop the debug-menu controller logging from `OnDestroy` under `NO_DEBUG`.
4. **Typed records for the code-defined events** (half a day): the eight wrappers build records instead of dictionaries.
5. **The designer-authored path** (one day): `ExecuteLogData` produces `KeyValueEvent`; variable entries render values; the typo fixed with the key format kept.
6. **Cut-over** (one day plus the dev-site checks): build, the seven checks, golden test green, Newtonsoft removed, `LogManager.cs` (an unused earlier logger) deleted.
7. **Afterwards:** a generated schema page for analysts from the record types; move the grader-read designer events to typed records one at a time.

Roughly a week of focused work plus review, against a live research instrument, which is why it should be its own project rather than folded into the current fix.

## 8. Changes on both sides (the game and the log service)

We own stratalog as well as the client, so some of the most valuable changes are coordinated ones. Each can land on the server first as an optional field or an opt-in response, with the current builds unaffected, and the client adopts it in version 2. Ordered by how much each would have mattered in September 2026.

1. **An entry id, and de-duplication on the server.** Every entry carries a client-generated id, formed from a session id and a sequence number (§4's `LogEntry` gets `sessionId` and `seq`; the id is `"<sessionId>:<seq>"`). The server stores on a unique index over `(game, user_id, entry_id)` and ignores a repeat, answering as if it had been stored. Delivery becomes exactly-once: a request that times out and is retried, or a client defect like the September resend loop, is a no-op instead of 700,000 duplicate rows. Analysts get a hard key for every entry.
2. **Per-entry results for a batch.** Today one invalid entry refuses the whole batch and the client isolates it with up to fifty single requests. The server instead validates and stores each entry on its own and answers with the indices and reasons of the ones it rejected; the client drops exactly those. The refusal-loop class of defect and the single-send machinery both go. Opted into by a request field (or a `/v2` route) so the current builds keep the old behaviour.
3. **Session id and sequence number as data.** Beyond de-duplication: the server can see gaps (entries lost on a device, or trimmed from a full cache) and has an order that does not depend on the device clock, which matters where timestamps collide (puzzle-piece events logged several times within a second). StrataHub's logging-health check can compare what the session sent with what arrived instead of inferring from counts. A `session_start` entry carries the session's fixed facts once (§9 below).
4. **A "sent from cache" marker.** The client flags entries that were persisted and sent after a reload or an outage (`recovered: true`, the field name StrataHub's page repair was going to use). With the server's received time, analysts and the grader tell a live event from a recovered one, which is what the research partner needs to interpret the old overwritten backlogs.
5. **Flood protection that the client honours.** A per-account rate limit on submit, answering `429` with `Retry-After`; the client treats `429` as a backoff of that length (today it would fall into the generic backoff, which also works, but the server would be in control). The server protects itself from the next client bug.
6. **Per-session credentials.** The API key is one static string that the host page gives every browser, so anyone can post entries under any user id. StrataHub issues a short-lived token bound to the user id along with the bridge config; stratalog verifies it. This is the one item that also touches StrataHub, and the one that protects the integrity of the research data rather than its completeness.
7. **Capabilities from the server.** Batch limit, body limit, and whether per-entry results and de-duplication are supported, either in the bridge config or from a small endpoint, so the client adapts instead of hard-coding 50 and 1 MB.
8. **Soft schema checks per event type.** The server knows the event types; it validates `data` shapes against the typed records of §4 and records warnings in the ledger without refusing. That would have caught the `DialogeNodeEvent` typo on day one. It cannot detect the overwritten-details defect, whose values were well formed.
9. **Lower priority.** The device block once per session (in the `session_start` entry) instead of on every entry: about 300 bytes each, a few hundred megabytes over the data so far, but the Devices tab and the grader read it per entry today and would join on the session id instead. A client "sent at" time so the server can measure and correct device clock skew. Compression of backlog bodies, which are small anyway.

**Done on the client in version 1.2 of the fix (2026-10-01):** items 1, 3, 4, the client half of 5, and the "sent at" time of item 9 (`session_id`, `seq`, `entry_id`, `recovered`, `sent_at`, `429`/`Retry-After`). Entries cached by earlier builds carry `recovered` but no ids, so the server's unique index on `entry_id` must allow a missing value. The server half of 1 (unique index, repeats ignored), 5 (rate limit, and `Access-Control-Expose-Headers: Retry-After`) and the queries for 4 can follow without a game build. Item 6 is deferred on purpose: a token that expires or fails to verify would stop a classroom's logging; the client already sends whatever `auth` string the host page supplies, so a long-lived per-user token can be introduced later from StrataHub and stratalog alone.

Order of work: the server side of 1, 2 and 5 first (all backward compatible), then the client record of §4 carries `sessionId`, `seq`, `recovered` and uses per-entry results from the start; 6 and 7 with StrataHub when the bridge config next changes; 8 and 9 afterwards.

## 9. Decisions to make before starting

- Keep the designer-authored `LoggingData` path (recommended, it is how content people add events) or require code for every event?
- Where the cache lives: PlayerPrefs (1 MB cap, what we know) or a file under `persistentDataPath` (IndexedDB-backed on WebGL, larger, needs an explicit sync). The cap has not been a problem since the cache was bounded; keeping PlayerPrefs is the smaller change.
- The standalone (non-WebGL) builds the team uses for testing: same transport, file store; confirm they stay in scope.
- The version string: who sets `bundleVersion` and when, so analysts can group by build (today it is edited by hand in the build profiles).
- Whether the schema page for analysts is generated in the game repo or written once into `mhscurriculum`/the research notes.
- Which of the server-side changes of §8 go first, and whether the research partner's analyses need the entry id and the recovered marker before version 2 ships (they can be added to the current client as extra fields without the rest of the redesign).

## 10. Risks

- **Breaking the data contract** is the main one; §6 is the guard, and the field inventory in `stratahub/docs/mission-hydrosci/mhs-game-logging-issues-092826.md` §1 G2 lists every event type that matters.
- **Regressions in the 21 assets' events**, which are only exercised by playing through the units: the acceptance run should cover one unit fully, not only the opening minutes.
- **Two logging implementations alive at once** if the migration stalls half-way; the facade is there so the cut-over can be a single build.
- **Server changes ahead of the client**: every item in §8 must stay optional on the server until no build without it is in use by students; the collections on MHS Builds say which builds are live.
