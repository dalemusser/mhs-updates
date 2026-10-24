# 01 — Evidence from production (2026-09-14 → 2026-10-23)

Source: the game's own crash events in the log service (`eventType: crash`,
`data.type: runtime_error`), each joined to the student's game events around it; the
StrataHub launch records and heartbeats for the same sessions. Read-only queries, run
2026-10-23; full output in `data/unit4_crash_context.out`. Times below are US Central.

## 1. What crashes, and how often

| | |
|---|---|
| Runtime-error crash events since 2026-09-14 | 15 |
| … in Unit 4 with "Uncaught RuntimeError: memory access out of bounds" | 13 |
| … in Unit 4 with "Uncaught RuntimeError: null function or function signature mismatch" | 1 (2026-10-08) |
| … elsewhere (Unit 2, "unityFramework is not defined", a staff account) | 1, unrelated |
| Builds affected | unit4 v2.8.1 (7), v2.8.4 (4), v2.8.9 (3) — every build the schools have run |
| Devices | 4 GB Chromebooks with 2, 4 and 8 cores; one 8 GB Windows PC; Chrome 147–154 |
| Distinct students | 14 (one each); 6 schools |
| Time into the session | 35–60 min (the time it takes to reach the garden boxes) |

Rate per 100 Unit 4 launches by students, by week: 0.7 (wk of 9/28), 1.4 (10/5),
1.2 (10/12), 2.6 (10/19). Students who interacted with a garden box that week: 24, 61, 63,
30 → roughly one in ten students who reach the boxes hits it.

Memory is not the cause: the heartbeats of the sessions that crashed show the wasm heap flat
at 495 MB (the build's fixed heap) and the JS heap at 199–216 MB from start to crash, the
same as sessions that did not crash; 4 GB and 8 GB devices crash alike.

## 2. Where in play it happens

Eleven of the fourteen Unit 4 crashes have game events in the preceding minutes (the other
three have none, their logging was quiet). In all eleven the last events are the garden box
interaction `ObjectInterEvent {actionType: "Press E To Place Cameras", objectName: "Garden
Box N"}`, then a dialogue event, then the crash, within 2 s. Two full traces (ids shortened):

**Crash 11 — 2026-10-16 08:50:05, unit4 v2.8.4, student …b56ae3, 4 GB / 8 cores, Chrome 154**

```
08:49:09  ObjectInterEvent   Press E To Place Cameras @ Garden Box 2
08:49:09  DialogueEvent      conversation 92 start, node 27
08:49:17  TerasGardenBox     cameraPlaced  boxId 1  soilType None      ← placed with no soil chosen
08:49:18  DialogueEvent      conversation 92 finish; start; node 29
08:49:39  ObjectInterEvent   Press E To Place Cameras @ Garden Box 3
08:49:39  DialogueEvent      conversation 92 start, node 30
08:49:43  DialogueEvent      node 67
08:49:45  TerasGardenBox     cameraPlaced  boxId 2  soilType None      ← placed with no soil chosen
08:49:47  DialogueEvent      finish; start; node 0; node 32            ← the "all placed" wrap-up
08:49:58  DialogueEvent      node 33
08:50:01  DialogueEvent      finish
08:50:03  ObjectInterEvent   Press E To Place Cameras @ Garden Box 3   ← re-opens a box placed with "None"
08:50:05  DialogueEvent      node 62                                   ← feedback dialogue trigger fires
08:50:05  CRASH              memory access out of bounds
08:50:40  relaunch (gameStartEvent), quests re-activated; events continue
```

**Crash 13 — 2026-10-20 12:47:32, unit4 v2.8.9, student …b56bdd, 4 GB / 2 cores, Chrome 154**

```
12:46:26  ObjectInterEvent   Press E To Place Cameras @ Garden Box 2
12:46:36  TerasGardenBox     cameraPlaced  boxId 1  soilType None      ← no soil chosen (no soil-selected event before it)
12:46:54  ObjectInterEvent   Press E To Place Cameras @ Garden Box 3
12:47:02  TerasGardenBox     (soil selected)  boxId 2  soilType Gravel
12:47:04  TerasGardenBox     cameraPlaced  boxId 2  soilType Gravel
12:47:05  DialogueEvent      finish; start; node 0; node 32            ← wrap-up
12:47:12  DialogueEvent      node 33
12:47:15  DialogueEvent      finish
12:47:18  ObjectInterEvent   Press E To Place Cameras @ Garden Box 3   ← re-open a box placed WITH a soil: fine
12:47:20  DialogueEvent      node 62 start … 12:47:28 finish
12:47:30  ObjectInterEvent   Press E To Place Cameras @ Garden Box 2   ← re-open the box placed with "None"
12:47:32  CRASH              memory access out of bounds
12:48:27  relaunch; events continue
```

Both: the crash is the re-opening of a box whose camera was placed with soil "None", after
the wrap-up dialogue (nodes 32–33) that follows the third placement. The box re-opened with a
real soil value (Gravel) a few seconds earlier did not crash. `boxId` is the zero-based
planter index (Garden Box 2 → boxId 1).

The fleet-wide soil values confirm "None" placements are common:

| TerasGardenBox events since 9/14 | count |
|---|---|
| cameraPlaced, soilType Sand / Clay / Gravel | 285 / 269 / 248 |
| cameraPlaced, soilType **None** | **213** (21 % of placements) |
| soil-selected (empty actionType), Sand / Clay / Gravel | 442 / 400 / 322 |

The Place button's onClick calls `PlaceCamera` first and `LogCameraPlaced` second
(prefab lines 3136, 3148), so a logged "None" means the planter's soil variable really was
set to a value outside 0–2 (i.e. −1, nothing selected) at placement time.

## 3. The stacks

Same shape in every build; only the function indices shift with the build:

```
RuntimeError: memory access out of bounds
    at wasm-function[108552]:0x24e867a      (v2.8.9; v2.8.4: [108544]:0x24e60da)
    at wasm-function[3309]
    at wasm-function[59792]
    at wasm-function[3347]
    at wasm-function[82509]
    at wasm-function[83459]
    at wasm-function[110316]
    at invoke_iiii                          (framework.js)
    … Browser_mainLoop_runner               (the Unity main loop; i.e. during Update/coroutine stepping)
```

No symbols: the project builds with `webGLDebugSymbols: 0`, and the content server does not
serve `unit4.symbols.json`. Symbolicating is possible with a rebuild of the same version with
Debug Symbols on, but the hypothesis in `02-` is testable in the editor without it.

## 4. The "null function" variant (crash 7, 2026-10-08)

Same unit, same build generation, no game events in the preceding 20 minutes. "null function
or function signature mismatch" is what a call through a null object (a C# `NullReferenceException`
on a virtual call) becomes under the same exception setting. Keep it in view when fixing;
it may be the `GameObject.Find("Dialogue Canvas")` paths in the same files.

## 5. What students experience

The tab shows the StrataHub play page's crash panel; every student relaunched within 15 s to
2 min (14 of 14 relaunched in the week reviewed), the quests re-activate, and 10 of 14 placed
cameras again within 30 minutes. The cost is the minutes since the last save and the
confusion. The StrataHub activity page counts these as real crashes (correctly).
