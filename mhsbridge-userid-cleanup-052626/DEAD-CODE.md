# Dead Code

## `Assets/Scripts/Systems/Logging/LogManager.cs`

This file appears to be an earlier, abandoned implementation of game-event logging that was superseded by `GameLogger.cs`. Recommend deleting it as part of this same PR.

### Why it's dead

- The `MHS.LogManager` class is **not referenced anywhere in live code**. The only references in the project are in commented-out blocks at the top of `Assets/Scripts/Systems/Save Load System/SaveLoadManager.cs` (lines 12, 15, 23, 32) — those imports are inside `/* ... */`.
- A different class named `consoleLogManager` (lowercase first letter, in `StateGraph_Scene_Gameplay.cs:593`) is unrelated — it refers to `ConsoleLogManager`, the developer-console helper, not `MHS.LogManager`.

### Why it's a hazard if left in

`LogManager.cs` carries three artifacts that would actively misbehave if anyone ever resurrected it:

1. **Hardcoded URL** to the legacy `https://log.adroit.games/logs` endpoint. Stratalog's current production submit URL is `/api/log/submit`. The legacy `/logs` route exists for backward-compat but is not the path the host page advertises in `__mhsBridgeConfig`.
2. **Hardcoded API key**: `private const string JWTToken = "LEARN_FAST";` — bypassing the per-config `Bearer` token MHSBridge provides.
3. **The `GameLog` struct has a `public string playerId;`** field. Marshalled through `JsonUtility.ToJson`, it would emit `{"playerId":"..."}` — which stratalog now rejects. This is the same shape of bug we just fixed in `GameLogger.cs`.

In other words, if someone discovers `LogManager.cs` later and wires it back up "because it looks like the logging class," they'll reintroduce the exact bug this drop is removing.

### How to remove

```bash
rm Assets/Scripts/Systems/Logging/LogManager.cs
# Also delete the matching .meta file if present:
rm Assets/Scripts/Systems/Logging/LogManager.cs.meta
```

No other source needs to change. The commented-out lines in `SaveLoadManager.cs` can stay or be cleaned up alongside — they're already inert.

### Verifying it's safe to remove

From the MHS project root:

```bash
grep -rln "MHS\.LogManager\|new LogManager\|: LogManager\b" Assets/
```

This should return no results, or only files that no longer compile-include the match (i.e., it's inside comments). If you do find a live reference, don't delete — investigate first.
