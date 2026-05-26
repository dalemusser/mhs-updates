# Directions

## 0. Prerequisite — one shared stratahub user record

There is no separate "local stratahub". Editor builds and localhost browser launches both talk to the live stratahub at **adroit.games**, which is the single shared instance used by everyone.

For the development-mode sentinel `user_id` to round-trip through stratalog and stratasave, the following document must exist in the shared `stratahub.users` collection:

```js
{ _id: ObjectId("000000000000000000000001"),  /* + your usual user fields */ }
```

If it isn't there yet, please ask Dale to seed it. Without this row, editor and localhost launches will be authenticated as a non-existent user; stratalog/stratasave will still accept the payloads (the format is valid) but mhsgrader joins will turn up empty.

The production / classroom path is unaffected — real users get their own ObjectID from stratahub at login.

## 1. Replace the bridge files (2 files)

Copy from `Bridge/` over the corresponding files in the Unity project:

| Source | Destination in MHS project |
|--------|---------------------------|
| `Bridge/MHSBridge.cs` | `Assets/Scripts/MHSBridge.cs` |
| `Bridge/MHSBridge.jslib` | `Assets/Plugins/WebGL/MHSBridge.jslib` |

The `.jslib` is unchanged from the 2026-04-15 drop. It's included so this folder is a complete unit; if you already have the latest `.jslib` you can skip it.

## 2. Replace the WebGL host page (1 file)

| Source | Destination |
|--------|-------------|
| `WebGL-Template/MHS-Bridge-index.html` | Each unit's `index.html` in your WebGL build output, replacing Unity's default `index.html`. |

If you're already on the 2026-05-12 build-automation drop, this file is identical to what you have.

## 3. Replace the three game scripts (3 files)

Copy from `Game-Code/Systems/` over the corresponding files in `Assets/Scripts/Systems/`:

| Source | Destination |
|--------|-------------|
| `Game-Code/Systems/Logging/GameLogger.cs` | `Assets/Scripts/Systems/Logging/GameLogger.cs` |
| `Game-Code/Systems/Authentication/AuthManager.cs` | `Assets/Scripts/Systems/Authentication/AuthManager.cs` |
| `Game-Code/Systems/Authentication/Resources/IdentityBridge.cs` | `Assets/Scripts/Systems/Authentication/Resources/IdentityBridge.cs` |

## 4. Build and verify

1. Rebuild a single unit (e.g., Unit 1).
2. Open it from a browser tab while logged into StrataHub.
3. Play long enough to trigger at least one logged event (e.g., open a dialogue, move the character).
4. In the browser DevTools network tab, find the POST to `https://log.adroit.games/api/log/submit`. The request body should contain `"user_id": "<24-char-hex>"` and **not** `"playerId"`. The response should be HTTP 200.
5. Optionally, verify in stratalog's Console → Log Browser that the entries are now appearing under the user's `user_id`.

If you see HTTP 400 with `"code": "DEPRECATED_FIELD"`, something is still emitting `playerId`. Search the project for the string `"playerId"` (with quotes) — any remaining JSON-key occurrence is the offender.

## 5. Optional cleanup

- `DEAD-CODE.md` — an unused script (`LogManager.cs`) that can be deleted in the same PR.
- `RECOMMENDATIONS.md` — refactor and cleanup ideas that aren't required by this fix but tighten the codebase. The main one is renaming the `playerId`/`playerName` BindableVariable assets, which is the asset-side counterpart to the `MHSBridge.GetPlayerID()` → `GetUserID()` rename done in this drop.

## Out of scope for this drop

The two BindableVariable ScriptableObject assets used by `AuthManager.cs` / `DatabaseSaver.cs` / `SettingsSaveManager.cs` are still named `playerId` and `playerName` in the project, and the `[SerializeField]` fields that reference them are also still named `playerId`/`playerName`. We left them alone deliberately — the string VALUE they carry is now a user_id, which is all the network contract cares about, and renaming the assets requires Inspector re-wiring we couldn't validate from outside the project. See `RECOMMENDATIONS.md` §1 and §5 for the suggested follow-up.
