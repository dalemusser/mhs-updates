# MHSBridge Change Notice — 2026-05-25

This drop renames two public MHSBridge methods and updates the editor-mode development sentinel. It is the **second** drop of MHSBridge (the first being the 2026-04-15 dev-handoff). If you already integrated the earlier drop, only the items in **Migration** below need attention.

---

## Why this exists

Stratalog and stratasave now strictly enforce that the identity field is `user_id` and that the value is a 24-character lowercase hex string (a MongoDB ObjectID). Any payload using `playerId`, `player_id`, `login_id`, or `email`, or carrying a non-hex value, is rejected with HTTP 400.

The previous MHSBridge already returned the correct value in its `user_id` JSON field, but its **public method names** still said `GetPlayerID()` / `GetPlayerName()`. That made it look — to readers and to game-side code — like the value was a "player id". It also let mistakes like `{ "playerId": MHSBridge.Instance.GetPlayerID() }` slip past code review.

Renaming makes the contract obvious at the call site: you are reading a `user_id`, and any payload you send must use the JSON key `user_id`.

---

## Migration

| Before | After |
|--------|-------|
| `MHSBridge.Instance.GetPlayerID()` | `MHSBridge.Instance.GetUserID()` |
| `MHSBridge.Instance.GetPlayerName()` | `MHSBridge.Instance.GetUserName()` |

The return type, return value, and behavior are unchanged. This is a pure rename.

The editor-mode development sentinel also changed:

| Before | After |
|--------|-------|
| `GetPlayerID()` returned `"mhs_developer"` in the Unity Editor | `GetUserID()` returns `"000000000000000000000001"` in the Unity Editor |

This matches what `MHS-Bridge-index.html` already sends when launched from `localhost`. A stratahub user with `_id = ObjectId("000000000000000000000001")` must exist in the shared adroit.games stratahub for editor/localhost log and save round-trips to succeed — see top-level `DIRECTIONS.md`.

---

## Unchanged

- `MHSBridge.jslib` — no changes. Copy as-is.
- `MHSBridge_GetConfig`, the JSON shape it returns (`identity.user_id`, `identity.name`, `services.*`), and all other `MHSBridge_*` jslib functions.
- All service-endpoint, navigation, and lifecycle methods (`GetLogSubmitConfig`, `GetStateSaveConfig`, `CompleteUnit`, `EndGame`, etc.).
- The `IsPWA` / `HasConfig` properties and `OnPWAReady` callback.

---

## Files in this folder

| File | Action |
|------|--------|
| `MHSBridge.cs` | **Replace** `Assets/Scripts/MHSBridge.cs` with this file. |
| `MHSBridge.jslib` | **Replace** `Assets/Plugins/WebGL/MHSBridge.jslib` with this file. (Identical to the 2026-04-15 drop — included for completeness.) |
| `MHSBridge-Integration-Guide.md` | Reference documentation. Supersedes the 2026-04-15 guide. |
| `CHANGE-NOTICE.md` | This file. |
