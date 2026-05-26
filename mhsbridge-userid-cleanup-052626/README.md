# MHSBridge `user_id` Cleanup — 2026-05-26

This drop fixes a logging-side bug in Mission HydroSci and makes the MHSBridge identity contract self-documenting at the call site.

## TL;DR

- Stratalog and stratasave now reject any payload that names the identity field `playerId`, `player_id`, `login_id`, or `email`, or that uses a non-hex value. The canonical identity is `user_id` — a 24-character lowercase hex MongoDB ObjectID.
- **The current game build sends `"playerId"` in its log payloads.** Stratalog rejects every one of these with HTTP 400 `DEPRECATED_FIELD`. As a result, no MHS gameplay logs are being recorded.
- This drop fixes that and renames `MHSBridge.GetPlayerID()`/`GetPlayerName()` to `GetUserID()`/`GetUserName()` so the JSON contract is obvious at the call site.

## What's in this folder

| Folder | Purpose |
|--------|---------|
| `Bridge/` | Cleaned `MHSBridge.cs` + unchanged `MHSBridge.jslib` + updated integration guide + per-folder change notice. |
| `WebGL-Template/` | Latest `MHS-Bridge-index.html` (the host page that wraps Unity builds and provides `__mhsBridgeConfig`). This file lives in `mhs-updates/build-automation-update-051226/` but was rewritten on 2026-05-24 during the de-identification cutover — that's when its identity contract changed to `user_id` (ObjectID hex) and the localhost path picked up the `000000000000000000000001` sentinel. Copied in here so this folder is a complete unit and so the localhost sentinel is documented next to the editor sentinel it pairs with. |
| `Game-Code/` | The three Unity scripts inside the MHS project that need to change. Tree mirrors `Assets/Scripts/Systems/` so you can drop them in place. |
| `DIRECTIONS.md` | Step-by-step install instructions and the one setup note about the shared adroit.games stratahub user record. |
| `WHATS-CHANGED.md` | Line-level summary of every change in this drop. |
| `DEAD-CODE.md` | One unused file (`LogManager.cs`) that can be removed. |
| `RECOMMENDATIONS.md` | Not required by this fix, but suggested follow-ups (asset renames, fallback URL hygiene, etc.). Take them as separate commits. |

## Where to start

1. Read **`DIRECTIONS.md`** for the install order and the one mandatory setup note.
2. Read **`WHATS-CHANGED.md`** if you want to know exactly what's different before you copy files.
3. The cleaned `MHSBridge.cs` lives in `Bridge/`. The three updated game scripts live under `Game-Code/Systems/`. File names and paths match what's in `Assets/Scripts/` today so you can copy each one over its counterpart.
