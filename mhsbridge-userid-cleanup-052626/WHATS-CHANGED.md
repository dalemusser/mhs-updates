# What's Changed

Reference: line numbers below are against the source files we worked from. The actual `Bridge/` and `Game-Code/` files in this drop already have the changes applied.

## The bug

`Assets/Scripts/Systems/Logging/GameLogger.cs` builds its log payloads with:

```csharp
{ "playerId", playerId },   // GameLogger.cs:92 — wrong key
```

Stratalog's `/api/log/submit` rejects this with HTTP 400:

```json
{ "error": "'playerId' is not accepted; submit 'user_id' instead", "code": "DEPRECATED_FIELD" }
```

That single line is the entire reason MHS isn't logging right now. Every other piece of the stack was already correct: the host page sets `__mhsBridgeConfig.identity.user_id` to the ObjectID hex, `MHSBridge` parses it into `BridgeIdentity.user_id`, and `AuthManager` propagates it down. But `GameLogger.LogEvent` then re-keys the value as `"playerId"` on the way out the door, so stratalog rejects the payload.

## The fix, in three pieces

### 1. `Bridge/MHSBridge.cs`

- `GetPlayerID()` → `GetUserID()`  (line ~183)
- `GetPlayerName()` → `GetUserName()`  (line ~191)
- Editor-mode default `_userId = "mhs_developer"` → `_userId = "000000000000000000000001"`  (line ~117)
- Editor-mode `BridgeIdentity { user_id = "mhs_developer", ... }` → `user_id = "000000000000000000000001"`  (line ~121)
- Debug.Log string updated to mention `000000000000000000000001` (line ~132)
- Stale comment removed: `// (currently carries login_id value)` (was line ~177)

The internal `_userId` field, the JSON `BridgeIdentity.user_id` field, and the `.jslib`-side `MHSBridge_GetConfig` blob were already correctly named — no changes needed there. Renaming the two public methods is the substantive change.

### 2. `Game-Code/Systems/Logging/GameLogger.cs`

- `private static string playerId;` → `private static string userId;`  (line ~26)
- `{ "playerId", playerId },` → `{ "user_id", userId },`  (line ~92) ← **the actual bug**
- `public void SetPlayerId(string id)` → `public void SetUserId(string id)`  (line ~523)
- Removed a large commented-out `SendLog`/`LogEntry` block whose only remaining role was to confuse readers (lines ~240-279, ~535-567). These were already disabled before this drop; they're gone now.

### 3. `Game-Code/Systems/Authentication/Resources/IdentityBridge.cs`

- `UserIdentity.email` field → `UserIdentity.user_id`  (line ~13). Comment explains the snake_case shape matches what `/api/user` and the host bridge return.
- Editor-mode dummy: `email = MHSBridge.Instance.GetPlayerID()` → `user_id = MHSBridge.Instance.GetUserID()`  (lines ~48-49)
- WebGL DebugAuth dummy: `email = "test.user@adroit.edu"` → `user_id = "000000000000000000000001"`  (line ~72)
- WebGL path: `MHSBridge.Instance.GetPlayerID()` → `GetUserID()`, `GetPlayerName()` → `GetUserName()`  (lines ~80, ~93, ~94, ~98)
- Dropped a redundant `try/catch` around a second call to `GetPlayerName` (lines ~96-106 of the original) — `GetUserName()` cannot throw.

### 4. `Game-Code/Systems/Authentication/AuthManager.cs`

- Editor-override path: `GameLogger.Instance.SetPlayerId(playerOverrideId)` → `SetUserId(playerOverrideId)`  (line ~56)
- Fetched-identity path: `playerId.Value = user.email` → `playerId.Value = user.user_id`  (line ~78)
- Fetched-identity path: `GameLogger.Instance.SetPlayerId(user.email)` → `SetUserId(user.user_id)`  (line ~79)
- Console log lines and on-screen status text updated: `"User Email: " + user.email` → `"User ID: " + user.user_id`  (lines ~76, ~91)
- The `[SerializeField] private StringVariable playerId;` and `playerName;` BindableVariable references on lines 12-13 are **intentionally unchanged** — see DIRECTIONS.md "Out of scope".
- The `playerOverrideId` field name is **intentionally unchanged** for the same reason. Its tooltip was updated to require a 24-char lowercase hex value.

## What did NOT change

- `MHSBridge.jslib` — byte-identical to the 2026-04-15 drop.
- `MHS-Bridge-index.html` — no changes in *this* drop. The host page was already rewritten on 2026-05-24 during the de-identification cutover (when the identity contract became `user_id` and the localhost path started using the `000000000000000000000001` sentinel). The file in `mhs-updates/build-automation-update-051226/` still bears the 2026-05-12 folder name, but its contents are from that May 24 rewrite.
- The host page contract (`window.__mhsBridgeConfig` shape).
- Stratalog/stratasave URLs, auth headers, and payload shape (except for the field-name fix that's the whole point of this drop).
- Any of the eight `LogXxxEvent` wrapper methods in `GameLogger.cs` — they all flow through `LogEvent`, which is where the fix lives.
- Event-type strings like `"PlayerPositionEvent"` and `"DialogueNodeEvent"` — these are gameplay event labels stored in the log's `eventType` field, not identity fields, and stratalog does not validate them.
