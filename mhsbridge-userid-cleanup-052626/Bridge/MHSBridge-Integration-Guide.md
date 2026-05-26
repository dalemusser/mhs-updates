# MHSBridge Integration Guide (2026-05-25)

## Overview

MHSBridge is the bridge between the Unity WebGL game and its hosting environment. It provides user identity, service endpoint configuration, unit navigation, and game lifecycle management. The game gets everything it needs from the host page rather than hardcoding domains, API keys, or URL patterns.

MHSBridge operates in two modes, determined automatically:
- **PWA mode** — the game runs inside StrataHub. Unit transitions, progress tracking, and downloads are managed by StrataHub.
- **URL mode** — the game runs standalone in a browser tab. Navigation uses relative URLs between unit folders.

---

## Files

| File | Location in Unity Project | Purpose |
|------|--------------------------|---------|
| `MHSBridge.cs` | `Assets/Scripts/MHSBridge.cs` | C# bridge script |
| `MHSBridge.jslib` | `Assets/Plugins/WebGL/MHSBridge.jslib` | JavaScript plugin for browser interop |
| `index.html` | Each unit's build folder (replaces Unity-generated index.html) | Host page for URL-launched builds |

## GameObject Setup

Create an empty GameObject named `MHSBridge` with the `MHSBridge.cs` script attached. Place it in the first scene of every unit and the loader. The script uses `DontDestroyOnLoad` and a singleton pattern.

---

## API Reference

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Instance` | `MHSBridge` | Singleton instance |
| `IsPWA` | `bool` | True if running inside StrataHub (PWA or browser via Mission HydroSci) |
| `HasConfig` | `bool` | True if `window.__mhsBridgeConfig` was present and parsed |

### Identity

| Method | Returns | Description |
|--------|---------|-------------|
| `GetUserID()` | `string` | User's `user_id` — 24-character lowercase hex of their stratahub.users `_id`. Empty string if not available. |
| `GetUserName()` | `string` | User's display name. Empty string if not available. |

`user_id` is the canonical identifier across stratahub, stratalog, stratasave, and mhsgrader. Stratalog and stratasave both reject any payload whose identity field is named `playerId`, `player_id`, `login_id`, or `email`, or whose `user_id` is not 24 lowercase hex characters.

### Service Endpoints

Each method returns a `ServiceConfig` with `url` (full endpoint) and `auth` (Authorization header value), or `null` if not configured.

| Method | Endpoint |
|--------|----------|
| `GetLogSubmitConfig()` | Log submission (e.g., `https://log.adroit.games/api/log/submit`) |
| `GetStateSaveConfig()` | Game state save (e.g., `https://save.adroit.games/api/state/save`) |
| `GetStateLoadConfig()` | Game state load (e.g., `https://save.adroit.games/api/state/load`) |
| `GetSettingsSaveConfig()` | User settings save (e.g., `https://save.adroit.games/api/settings/save`) |
| `GetSettingsLoadConfig()` | User settings load (e.g., `https://save.adroit.games/api/settings/load`) |

### Navigation

| Method | Description |
|--------|-------------|
| `GetUnitURL(unitName, out isLocked)` | Returns URL for a unit. Uses unitMap if available, otherwise relative URL. Returns null with `isLocked=true` if the unit is locked. |
| `NavigateToUnit(unitName)` | Navigates to a unit by name. No-op in PWA mode. |
| `CompleteUnit(unitId, nextUrl)` | Signals a unit is complete. In PWA mode, notifies StrataHub. In URL mode, navigates to `nextUrl`. |
| `EndGame()` | Signals the game has ended. In PWA mode, returns to StrataHub. In URL mode, closes the tab or shows exit message. |

---

## Implementation Guide

### User Identity

```csharp
string userId = MHSBridge.Instance.GetUserID();
string userName = MHSBridge.Instance.GetUserName();
```

Use `user_id` as the identity key in all log and save payloads. The JSON key MUST be `user_id` — stratalog and stratasave reject `playerId`, `player_id`, `login_id`, and `email`:

```csharp
// Log payload:  { "game": "mhs", "user_id": "69b4449ec6006ac370dad9df", "eventType": "...", ... }
// Save payload: { "user_id": "69b4449ec6006ac370dad9df", "game": "mhs", "save_data": { ... } }
```

### Service Endpoints

```csharp
var logConfig = MHSBridge.Instance.GetLogSubmitConfig();
if (logConfig != null)
{
    // POST directly to logConfig.url
    // Authorization header: logConfig.auth
}

var stateSave = MHSBridge.Instance.GetStateSaveConfig();
var stateLoad = MHSBridge.Instance.GetStateLoadConfig();
var settingsSave = MHSBridge.Instance.GetSettingsSaveConfig();
var settingsLoad = MHSBridge.Instance.GetSettingsLoadConfig();
// Same pattern: check for null, use .url and .auth
```

All URLs are complete endpoints — POST directly to them without appending paths.

### Unit Completion

Call `CompleteUnit` at the end of every unit's gameplay, including the last one. Always provide the next unit's relative URL.

```csharp
// End of Unit 1:
MHSBridge.Instance.CompleteUnit("unit1", "../unit2/index.html");

// End of Unit 2:
MHSBridge.Instance.CompleteUnit("unit2", "../unit3/index.html");

// End of Unit 5 (currently the last unit):
MHSBridge.Instance.CompleteUnit("unit5", "../unit6/index.html");
```

In PWA mode, the `nextUrl` parameter is ignored — StrataHub handles the transition. In URL mode, it navigates to the next unit. If the next unit doesn't exist yet (e.g., unit 6 isn't built yet), the navigation simply won't happen. When unit 6 is added, no code change is needed.

### End of Game

Call `EndGame()` from the game's end-of-game screen when the user clicks Continue or Return.

```csharp
// User clicks Continue on the end-of-game congratulatory screen:
MHSBridge.Instance.EndGame();
```

The game owns the end-of-game experience — design the congratulatory screen however you want. StrataHub does not show its own overlay.

**The flow for the final unit:**
1. Unit gameplay completes → call `CompleteUnit("unit5", "../unit6/index.html")`
2. Show the game's end-of-game screen (congratulations, achievements, etc.)
3. User clicks Continue → call `EndGame()`

**What EndGame does:**
- **PWA mode:** Navigates back to the Mission HydroSci units page in StrataHub
- **URL mode (browser tab):** Closes the tab. If the browser blocks it, shows "You can close this tab now."
- **Editor:** Logs a message (no-op)

**CompleteUnit vs EndGame:**
- `CompleteUnit` = "This unit's gameplay is done" → signals progress and transitions to the next unit
- `EndGame` = "The user is done and wants to leave" → exits the game

### Loader Navigation

The loader navigates to the student's current unit:

```csharp
void Start()
{
    string userId = MHSBridge.Instance.GetUserID();
    string currentUnit = DetermineCurrentUnit(userId); // Your save data logic
    MHSBridge.Instance.NavigateToUnit(currentUnit);
}
```

Units can be locked via the unitMap. A locked unit has a `null` entry in the map — `GetUnitURL` returns null with `isLocked=true`:

```csharp
bool isLocked;
string url = MHSBridge.Instance.GetUnitURL("unit3", out isLocked);

if (isLocked)
{
    // Show "unit not available" message
}
else if (url != null)
{
    MHSBridge.Instance.NavigateToUnit("unit3");
}
```

If no unitMap is present (e.g., developer builds), nothing is ever locked and `GetUnitURL` returns relative URLs.

---

## Host Page Configuration

The host page sets this JavaScript global before Unity starts:

```javascript
window.__mhsBridgeConfig = {
  identity: {
    user_id: "69b4449ec6006ac370dad9df",   // 24-char lowercase hex ObjectID
    name: "Dale Musser"
  },
  services: {
    log_submit:    { url: "https://log.adroit.games/api/log/submit",       auth: "Bearer abc123..." },
    state_save:    { url: "https://save.adroit.games/api/state/save",      auth: "Bearer xyz789..." },
    state_load:    { url: "https://save.adroit.games/api/state/load",      auth: "Bearer xyz789..." },
    settings_save: { url: "https://save.adroit.games/api/settings/save",   auth: "Bearer xyz789..." },
    settings_load: { url: "https://save.adroit.games/api/settings/load",   auth: "Bearer xyz789..." }
  },
  navigation: {
    unitMap: {                     // optional
      "unit1": "/play/t/abc123",  // available
      "unit2": "/play/t/def456",  // available
      "unit3": null,              // locked
      "unit4": null,              // locked
      "unit5": null               // locked
    }
  }
};
```

- **`identity`** — required. `user_id` (24-char lowercase hex) and `name`.
- **`services`** — required. Five service entries, each with `url` (full endpoint) and `auth` (Authorization header value).
- **`navigation.unitMap`** — optional. If present, `NavigateToUnit` uses it. If absent, relative URL navigation.

The game reads this at startup. It does NOT make any network calls for identity or configuration — the host page provides everything.

### How the Config is Provided

**StrataHub (PWA and browser):** The Go template injects the config server-side from the authenticated session and server configuration.

**Developer builds (URL mode):** The provided replacement `index.html` fetches identity from StrataHub's `/api/user` and service config from `/api/game-config?game=mhs`, assembles `__mhsBridgeConfig`, then starts Unity. You must be logged into StrataHub in your browser for this to work.

**Localhost:** The replacement `index.html` detects localhost and uses development defaults automatically — `GetUserID()` returns `"000000000000000000000001"` and all service configs return production endpoint URLs. No StrataHub login required. The matching `stratahub.users` document is seeded automatically by stratahub at startup.

**Editor:** Development defaults are built into `MHSBridge.cs` — same as localhost.

---

## Build Directory Structure

Each unit is in its own folder with the replacement `index.html`:

```
my-build/
  loader/
    index.html          <-- Replace with provided index.html
    Build/
    StreamingAssets/
  unit1/
    index.html          <-- Replace with provided index.html
    Build/
    StreamingAssets/
  unit2/
    index.html
    Build/
    StreamingAssets/
  ...
```

The replacement `index.html` auto-detects the build name from the folder name (e.g., `unit1/index.html` loads `Build/unit1.loader.js`).

### Build File Extensions

The replacement `index.html` expects Unity WebGL build files with the `.unityweb` extension:

```
Build/
  BuildName.loader.js
  BuildName.data.unityweb
  BuildName.framework.js.unityweb
  BuildName.wasm.unityweb
```

If your builds produce different file extensions (e.g., `.gz` or no extension), edit the file extension references in `index.html` to match.

---

## Testing

### Quick Smoke Test

```csharp
void Start()
{
    Debug.Log("User ID: " + MHSBridge.Instance.GetUserID());
    Debug.Log("User Name: " + MHSBridge.Instance.GetUserName());
    Debug.Log("Is PWA: " + MHSBridge.Instance.IsPWA);
    Debug.Log("Has Config: " + MHSBridge.Instance.HasConfig);

    var log = MHSBridge.Instance.GetLogSubmitConfig();
    Debug.Log("Log Submit: " + (log != null ? log.url : "null"));

    var stateSave = MHSBridge.Instance.GetStateSaveConfig();
    Debug.Log("State Save: " + (stateSave != null ? stateSave.url : "null"));

    var stateLoad = MHSBridge.Instance.GetStateLoadConfig();
    Debug.Log("State Load: " + (stateLoad != null ? stateLoad.url : "null"));

    var settingsSave = MHSBridge.Instance.GetSettingsSaveConfig();
    Debug.Log("Settings Save: " + (settingsSave != null ? settingsSave.url : "null"));

    var settingsLoad = MHSBridge.Instance.GetSettingsLoadConfig();
    Debug.Log("Settings Load: " + (settingsLoad != null ? settingsLoad.url : "null"));
}
```

### Test Scenarios

1. **URL-launched + logged into StrataHub:**
   - `HasConfig` = `true`, `IsPWA` = `false`
   - `GetUserID()` returns your stratahub user `_id` as a 24-char hex string
   - All service configs return endpoint URLs and auth

2. **Localhost:**
   - `HasConfig` = `true`, `IsPWA` = `false`
   - `GetUserID()` returns `"000000000000000000000001"`
   - All service configs return production endpoint URLs

3. **URL-launched + NOT logged into StrataHub:**
   - `HasConfig` = `false`
   - `GetUserID()` returns empty string
   - Service configs return null

4. **StrataHub (PWA or browser):**
   - `HasConfig` = `true`, `IsPWA` = `true`
   - `GetUserID()` returns the logged-in user's `_id` as a 24-char hex string
   - Service configs return endpoint URLs and auth

5. **EndGame in PWA/browser mode:**
   - After the end-of-game screen, click Continue
   - Navigates to the Mission HydroSci units page with all units completed

6. **EndGame in URL mode:**
   - After the end-of-game screen, click Continue
   - Tab closes, or shows "You can close this tab now"
