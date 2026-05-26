# Recommendations (not required for the bug fix)

These items are not necessary to fix the logging bug or to make this drop compile and run. They're refactors and small cleanups that would tighten the codebase. Take them in your own time, as separate commits.

---

## 1. Rename the `playerId` / `playerName` BindableVariable assets

**Files involved:**
- `Assets/Scripts/Systems/Authentication/AuthManager.cs` — `[SerializeField] private StringVariable playerId;` and `playerName;` (lines 12-13)
- `Assets/Scripts/Systems/Save Load System/CustomSavers/DatabaseSaver.cs` — `[SerializeField] private StringVariable playerId;` (line 24)
- `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs` — `[SerializeField] private StringVariable playerId;` (line 39)
- The two Obvious.Soap ScriptableObject asset files in `Assets/.../Resources/` (or wherever your bindables live) named `playerId.asset` and `playerName.asset`.

**Recommendation:** Rename the assets to `userId.asset` / `userName.asset` and the `[SerializeField]` fields to match. Update any Inspector wiring that breaks.

**Why:** The string VALUE these bindables carry is a `user_id` (24-char hex ObjectID) — they have nothing to do with a "player" anymore. The current naming actively suggests the wrong contract to anyone reading the code (it's how the `"playerId"` JSON key got there in the first place). A reader who sees `playerId.Value` going into a network request will reasonably assume the JSON key on the wire should also be `playerId`.

**Cost / risk:** Pure refactor. The risk is Unity scene/prefab references — Unity tracks asset references by GUID, so renaming the `.asset` file is safe, but Inspector-set `[SerializeField]` slots must be re-wired (or asset GUIDs preserved through Unity's `Rename` operation rather than file-system rename).

---

## 2. Use `.Value` explicitly when assigning a StringVariable to a string field

**File:** `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs`
- Line 148: `user_id = playerId,` → `user_id = playerId.Value,`
- Line 218: `user_id = playerId,` → `user_id = playerId.Value,`

**Why:** Both lines work today because Obvious.Soap defines an implicit `StringVariable → string` conversion. But:
- An implicit conversion that walks through a SOAP wrapper is the kind of thing a future SOAP upgrade could remove or change.
- It hides the dereference. A reader has to know Soap's operator overloads to understand that `user_id = playerId` is sending a string, not a `StringVariable` reference.
- The rest of the same files already use `playerId.Value` everywhere else (e.g., `string.IsNullOrEmpty(playerId.Value)` on lines 113 and 187). The two assignments are the odd ones out.

**Cost / risk:** Zero behavior change. Two-character edit each.

---

## 3. Update the legacy log endpoint fallback

**File:** `Assets/Scripts/Systems/Logging/GameLogger.cs`
- Line 17: `private const string logServerUrl = "https://log.adroit.games/logs";`

**Recommendation:** Either (a) update to `https://log.adroit.games/api/log/submit` to match what the bridge config advertises, or (b) delete the fallback path entirely and surface a clear error if `MHSBridge.Instance.GetLogSubmitConfig()` returns null.

**Why:** The `/logs` route is the legacy stratalog endpoint. It still exists for backward-compat, but everything the host page configures uses `/api/log/submit`. If the bridge config is ever missing (a stale host page, a malformed `__mhsBridgeConfig`), the game silently falls through to a stale URL with a stale auth header (`"Bearer LEARN_FAST"`) — which probably works against the legacy route, hiding the fact that the bridge config broke.

Same observation applies to the equivalent fallbacks in:
- `SettingsSaveManager.cs:44-45` (`_apiBaseUrl`, `_apiKey`)
- `GameSaveManager.cs:13-18` (`Domain`, `ApiKey`)
- `Logging/LogManager.cs:13-14` (mostly moot since this file is dead — see `DEAD-CODE.md`)

A failing-loud fallback ("logging disabled — bridge config not loaded") is usually safer than a silently-stale URL.

**Cost / risk:** Low. Pick the approach you prefer; either is a few-line change.

---

## 4. Clean out the commented-out `LogManager` block in `SaveLoadManager.cs`

**File:** `Assets/Scripts/Systems/Save Load System/SaveLoadManager.cs` — lines 11, 15, 23, 32 (the commented-out `[RequireComponent(typeof(LogManager))]`, the commented `playerId` field, the commented `Awake`/`SaveGame` bodies).

**Why:** If you're removing `LogManager.cs` per `DEAD-CODE.md`, the comments referencing it become orientation noise for the next reader. They suggest there's a meaningful relationship between `SaveLoadManager` and `LogManager` when in fact there hasn't been one for a long time.

**Cost / risk:** Zero. Pure deletion of comment lines.

---

## 5. Rename `playerOverrideId` and `oldPlayerId` for consistency

**File:** `Assets/Scripts/Systems/Authentication/AuthManager.cs`
- Line 16: `public string playerOverrideId;` → `public string userOverrideId;`
- Line 73 (inside `OnUserIdentityFetched`): `var oldPlayerId = playerId.Value;` → `var oldUserId = playerId.Value;`

**Why:** Same reasoning as #1 — these names describe a "player ID" but the value is a user_id. The `playerOverrideId` is also a public field exposed in the Inspector, so renaming it would surface in tooling (and you'd need to re-set it in any scene that has it populated, since Unity tracks `[SerializeField] public` fields by name, not GUID).

**Cost / risk:** `playerOverrideId` requires Inspector re-wiring on any scene that uses it. `oldPlayerId` is purely local, no risk.

Defer this until you do #1; they touch the same files and rationale.

---

## What I'd skip

- **Renaming event-type strings like `"PlayerPositionEvent"`, `"DialogueNodeEvent"`** in `GameLogger.cs`. Stratalog doesn't validate event-type names; these are gameplay-domain labels (the player character's position), not identity fields. Renaming them would break any saved query/filter that targets them in the Log Browser without producing any contract benefit.
- **Replacing `UserIdentity.user_id` (snake_case) with a C# `UserId` property.** The snake_case is intentional — it matches what `JsonUtility.FromJson` looks for in `/api/user`'s response and what the bridge config sends. Adding a wrapper property would just be a second name for the same value.
