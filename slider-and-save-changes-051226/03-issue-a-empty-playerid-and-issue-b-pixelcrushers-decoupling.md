# Issue A (400 on first settings/load) + Issue B (PixelCrushers coupling) — Fixes

Bundled change record. Two related fixes applied together because
verifying one without the other was impractical: the Issue A guard
suppresses a 400-generating spurious request, and the Issue B
decoupling is what actually lets settings changes survive reload.
Both were discovered while debugging Pass 1's mouse-sensitivity
slider.

Cross-references:

* `docs/00-session-state.md` — was the canonical "where we left off"
  doc when these issues were open
* `docs/settings-state-coupling-via-pixelcrushers.md` — long-form
  analysis of Issue B and three fix options (Option A is what we
  applied here)

---

## Audience

This doc is written for the developer who will fold these changes
back into the active Perforce mainline. Each section gives exact
before/after diffs they can apply directly, plus a why so they
understand the change without re-deriving it.

---

## Issue A — `/api/settings/load` returns 400 on first call

### Symptom

In every WebGL build session, the very first request to
`https://save.adroit.games/api/settings/load` returns
**HTTP 400 "Missing required fields"**. The next request to the same
endpoint (a few moments later) succeeds with 200.

Captured in browser console:

```
[DiagSettingsLoad] About to build request. … playerId='' (len=0), game='mhs'
[DiagSettingsLoad] Request body JSON: {"user_id":"","game":"mhs"}
…
save.adroit.games/api/settings/load:1  Failed to load resource: 400 (Bad Request)
[DiagSettingsLoad] Response: result=ProtocolError, responseCode=400,
                   body='{"error":"Missing required fields"}'

[DiagSettingsLoad] About to build request. … playerId='mhs_developer' (len=13), game='mhs'
[DiagSettingsLoad] Request body JSON: {"user_id":"mhs_developer","game":"mhs"}
[DiagSettingsLoad] Response: result=Success, responseCode=200, …
```

### Root cause

`Assets/Scripts/Character Customization/CharacterCustomization_DataController.cs:81`
raises the `loadSettings` event on every non-customization scene
load:

```csharp
private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
{
    ...
    if (SceneName == CharacterCusomizationSceneName)
    {
        CharacterCustomizationData = new CharacterCustomizationData();
    }
    else
    {
        loadSettings.Raise();
        return;
    }
    ...
}
```

That raise fires when the very first gameplay scene loads — **before**
`AuthManager.OnUserIdentityFetched` has populated
`playerId.Value` via `IdentityBridge.FetchUserIdentityAsync`. The
event handler `SettingsSaveManager.LoadSettingsFromDatabase` then
calls `LoadSettings` which reads `playerId.Value` (still empty) and
POSTs `{"user_id":"","game":"mhs"}`.

Server-side, `stratasave/internal/app/features/settingsapi/handler.go:167-170`:

```go
if in.UserID == "" || in.Game == "" {
    writeJSONError(w, r, "Missing required fields", http.StatusBadRequest)
    return
}
```

The 400 is correct server behavior. The bug is on the client side —
firing a guaranteed-to-fail request before auth has completed.

`AuthManager.cs:82` raises `loadSettings.Raise()` again a moment
later, after setting `playerId.Value = user.email`. That second call
is the one that succeeds.

### Fix — defensive guard on the client

Add an empty-`playerId` guard at the top of both
`SettingsSaveManager.LoadSettings` and `SettingsSaveManager.SaveSettings`.
If `playerId.Value` is null/empty, log a warning and return early.

Why guard at the manager rather than fix `CharacterCustomization_DataController`?

* The same problem could be triggered by any future code that raises
  `OnLoadSettings` early. Guarding at the consumer is the right
  abstraction — `SettingsSaveManager` should never POST a request
  the server is guaranteed to reject.
* `SaveSettings` gets the same guard symmetrically — there's no
  legitimate use case for posting `{"user_id":""}` to `/api/settings/save`
  either.

The early-raise behavior in `CharacterCustomization_DataController` is
left as-is. After auth completes, `AuthManager` re-raises and the
second call succeeds.

### Exact edits — File 1 of 2

**`Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs`**

Locate `SaveSettings(Action<bool> callback = null)` (around line 106).
Add the guard at the top:

```csharp
public void SaveSettings(Action<bool> callback = null)
{
    // Guard against missing identity. /api/settings/save requires a
    // non-empty user_id; the server returns 400 "Missing required
    // fields" otherwise. This typically happens when the save event
    // is raised before AuthManager has populated playerId.Value
    // (e.g. on early scene load before identity is fetched).
    if (playerId == null || string.IsNullOrEmpty(playerId.Value))
    {
        Debug.LogWarning("[SettingsSaveManager] SaveSettings skipped: playerId is empty. Caller should wait until auth has set playerId.Value before raising OnSaveSettings.");
        callback?.Invoke(false);
        return;
    }

    SettingsSaver settingsSaver = GetComponent<SettingsSaver>();
    if (settingsSaver == null) return;
    _currentSettings = JsonUtility.FromJson<SettingsSaver.SettingsValues>(settingsSaver.RecordData());
    StartCoroutine(SaveSettingsCoroutine(callback));
}
```

Locate `LoadSettings(Action<bool> callback)` (around line 167). Add
the same guard:

```csharp
public void LoadSettings(Action<bool> callback)
{
    // Guard against missing identity. /api/settings/load requires a
    // non-empty user_id; the server returns 400 "Missing required
    // fields" otherwise. CharacterCustomization_DataController.OnSceneLoaded
    // raises OnLoadSettings on every non-customization scene load,
    // which can fire before AuthManager has populated playerId.Value
    // — leading to a guaranteed-to-fail request with user_id="".
    if (playerId == null || string.IsNullOrEmpty(playerId.Value))
    {
        Debug.LogWarning("[SettingsSaveManager] LoadSettings skipped: playerId is empty. AuthManager will retry on its next identity-fetch tick once auth has populated playerId.Value.");
        callback?.Invoke(false);
        return;
    }

    StartCoroutine(LoadSettingsCoroutine(callback));
}
```

No other changes in this file for Issue A. The `[DiagSettingsLoad]`
diagnostic logs inside `LoadSettingsCoroutine` are intentionally left
in place pending verification — see "Pending cleanup" at the bottom
of this doc.

---

## Issue B — Settings overwritten by state-load (PixelCrushers coupling)

### Symptom

Even after `/api/settings/load` correctly returns a valid mouse
sensitivity (e.g. `1.2033`), the slider always appears at the maximum
position (2.0) on every game launch.

`[DiagPass1]` instrumentation in `GameplaySettings.cs` confirmed:

```
[DiagSettingsLoad] body=…"MouseSensitivity":1.2033472061157227,…    ← settings says 1.2033
[DiagPass1] GameplaySettings.Awake — mouseSensitivity.Value = 2     ← variable is 2 by panel-open
[DiagPass1] FloatVariable bounds: min=0.5, max=2, isClamped=True, defaultValue=1
```

### Root cause

`SettingsSaver` inherits from `PixelCrushers.Saver` (`SettingsSaver.cs:10`).
The base class's `OnEnable` method (`Assets/Imported/Tools/Pixel Crushers/Common/Scripts/Save System/Savers/Saver.cs:105-108`)
calls `SaveSystem.RegisterSaver(this)`, which puts `SettingsSaver`
into the PixelCrushers global saver list.

That registration means PixelCrushers' state save/load pipeline
treats `SettingsSaver` as one of its data sources, **even though its
data is settings, not state**:

1. Game-state events (`OnSaveGame` raise) →
   `DatabaseSaver.SaveGameToDatabase` → `SaveSystem.RecordSavedGameData()`
   iterates every registered saver including `SettingsSaver` →
   POST to `/api/state/save` with settings data embedded inside the
   `save_data` blob.
2. On load, `DatabaseSaver.LoadGameFromDatabaseCoroutine` →
   `SaveSystem.ApplySavedGameData(saveData)` iterates every
   registered saver and calls `ApplyData` on each → `SettingsSaver.ApplyData`
   writes the saved mouse sensitivity back into the SOAP FloatVariable.

Two write paths reach the same SOAP variables. The state-load path
wins the load-order race (its payload is larger so its response
arrives later) and overwrites whatever `/api/settings/load` just
applied. The `saved_games` Mongo collection on `save.adroit.games`
contains a `SettingsSaver` sub-entry that was written at some point
before Pass 1 changed the slider range; that legacy out-of-range
mouse sensitivity gets clamped to the new max (`2`) on every load.

Full analysis: `docs/settings-state-coupling-via-pixelcrushers.md`.

### Fix — Option A from the standalone doc

Override `SettingsSaver.OnEnable` and `OnDisable` to skip the base
class's `RegisterSaver` / `UnregisterSaver` calls. `SettingsSaver`
still inherits from `Saver` and keeps its `RecordData` / `ApplyData`
shape; `SettingsSaveManager` calls those methods directly
(`SettingsSaveManager.cs:110` for `RecordData`,
`SettingsSaveManager.cs:240` for `ApplyData`). After the override,
PixelCrushers' iteration no longer reaches `SettingsSaver`, and the
state save/load endpoints stop carrying settings data.

Why not Option C (drop the `Saver` inheritance entirely)?

* Option A is a 10-line change that solves the bug.
* Option C is a more thorough cleanup but requires renaming/refactoring
  and changing the type signature `SettingsSaveManager` works against.
* We can do Option C later as a separate hygiene pass without losing
  the fix from Option A. Incremental either way.

### Exact edits — File 2 of 2

**`Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`**

Locate the existing `OnEnable` and `OnDisable` overrides (lines 30–40):

Before:

```csharp
public override void OnEnable()
{
    base.OnEnable();
    SceneManager.sceneLoaded += OnSceneLoaded;
}

public override void OnDisable()
{
    base.OnDisable();
    SceneManager.sceneLoaded -= OnSceneLoaded;
}
```

After:

```csharp
public override void OnEnable()
{
    // Intentionally NOT calling base.OnEnable(). The PixelCrushers.Saver
    // base class's OnEnable() calls SaveSystem.RegisterSaver(this), which
    // would cause this saver's data to be picked up by
    // SaveSystem.RecordSavedGameData() and written into the game-state
    // payload that posts to /api/state/save — and applied via
    // SaveSystem.ApplySavedGameData() during /api/state/load. That
    // couples settings persistence to the state persistence path and
    // causes settings values to be overwritten by stale state-side
    // copies on load. Settings must flow exclusively through
    // SettingsSaveManager ⇄ /api/settings/*. SettingsSaveManager calls
    // RecordData()/ApplyData() on this component directly, so the
    // PixelCrushers registration is not needed for normal operation.
    // See docs/settings-state-coupling-via-pixelcrushers.md.
    SceneManager.sceneLoaded += OnSceneLoaded;
}

public override void OnDisable()
{
    // Match OnEnable: do NOT call base.OnDisable() (which would call
    // SaveSystem.UnregisterSaver(this)). Since we never registered,
    // there's nothing to unregister.
    SceneManager.sceneLoaded -= OnSceneLoaded;
}
```

Note `Start()` is **not** overridden. The base `Saver.Start()` only
calls `ApplyData` when `restoreStateOnStart=true`; the prefab has
`m_restoreStateOnStart: 0` so `base.Start()` is a no-op. No need to
touch it.

---

## What this change does **not** affect

* **Audio settings save/load** — still flows through `SettingsSaveManager`
  ⇄ `/api/settings/*`. The audio bus FloatVariables are written by
  `SettingsSaver.ApplyData` when `SettingsSaveManager.ApplySettings()`
  invokes it. No change in behavior.
* **Character customization data** — also flows through `SettingsSaver`
  inside `SettingsValues.characterCustomizationData`. After Issue B's
  fix, it continues to be persisted via `/api/settings/*`. That treats
  character customization as a user preference (persistent across
  playthroughs) rather than per-playthrough state. Worth confirming
  with the original implementer that this is the intended semantics —
  if customization should be per-playthrough state, a separate change
  is needed to lift it out of `SettingsValues`. Not in this pass.
* **Game state save/load** — `/api/state/save` and `/api/state/load`
  still work normally for everything else (quest state, dialogue
  variables, scrap counts, scene location, etc.). The only difference
  is the `SettingsSaver` sub-entry no longer appears in the
  `save_data` payload of new state-saves.
* **Existing data on the server** — legacy `saved_games` documents for
  existing users still have a `SettingsSaver` sub-entry inside their
  `save_data` blob. After this fix, PixelCrushers' `ApplySavedGameData`
  will see that sub-entry but won't find a registered saver for the
  `"SettingsSaver"` key, so it's silently ignored. The legacy data
  becomes dead data. No server-side cleanup is required, though a
  one-off Mongo script to remove the dead sub-entries from the
  `saved_games` collection would be a reasonable hygiene step at the
  team's leisure.

---

## Verification

In the running build, with the `[DiagPass1]` and `[DiagSettingsLoad]`
diagnostic logs still in place:

### Issue A verification

1. Open the build, watch the browser console.
2. Look for the **first** `[SettingsSaveManager]` log line. Expect:
   ```
   [SettingsSaveManager] LoadSettings skipped: playerId is empty.
   AuthManager will retry on its next identity-fetch tick…
   ```
   And **no** `[DiagSettingsLoad] About to build request… playerId=''`
   for that first attempt (it's skipped before reaching the coroutine).
3. Look for the second `LoadSettings` call (after auth completes).
   Expect a normal `[DiagSettingsLoad] About to build request…
   playerId='mhs_developer'` followed by `responseCode=200` with the
   server's settings JSON.
4. There should be **no** `400 (Bad Request)` for `settings/load`
   anywhere in the network log or console.

### Issue B verification

1. Continuing from the same launch, open the settings panel.
2. Watch for `[DiagPass1] GameplaySettings.Awake — mouseSensitivity.Value = X`.
   `X` should now match whatever `MouseSensitivity` came back from
   `/api/settings/load` (e.g. `1.3344…`), not `2`.
3. Slider should appear centered on the saved value, not pinned at
   the right edge.
4. Drag the slider to a different value, click **Apply**, close the
   panel. Refresh the page.
5. After reload, the new `[DiagSettingsLoad] body=` should show the
   value you just applied, and `[DiagPass1] GameplaySettings.Awake`
   should show that same value. The slider should appear at that
   value.
6. Walk around and trigger a state save (pick up scrap, complete a
   small quest step, etc.). In the browser DevTools Network tab, find
   the POST to `/api/state/save`. Inspect the request body. The
   `save_data` field should **no longer contain** a `"SettingsSaver"`
   entry — confirming that state save no longer carries settings.

If any of these don't hold, capture the relevant console/network
output and we diagnose from there.

---

## Pending cleanup (do once verified)

The following diagnostic instrumentation is still in the tree. Remove
after the verifications above pass:

| File | Diagnostic | Remove |
|---|---|---|
| `Assets/Scripts/Settings/GameplaySettings.cs` | `[DiagPass1]` `Debug.Log` calls in `Awake`, `OnEnable`, `Start`, `OnDisable`, `RevertGameplayChanges`, `ApplyGameplayChanges` | All. Revert the file to its post-Pass-2 state (just `Apply` / `Revert` / `OnApplicationQuit` methods with no logging). |
| `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs` | `[DiagSettingsLoad]` `Debug.Log` calls in `LoadSettingsCoroutine` (before request, after response) | All. The `[SettingsSaveManager]` warnings from the new guards stay — those are production-useful diagnostics. |

The `[SettingsSaveManager] LoadSettings skipped` and `SaveSettings
skipped` warnings from the new guards should remain in production —
they're cheap, fire only on misconfiguration, and would help diagnose
future "settings not persisting" reports.

---

## Why bundle A and B

Bundled because:

1. Verifying B without A in place produces noisy console output (an
   actual 400 every launch) that masks the real verification signal.
2. Both touch the same logical area (settings persistence path) and
   share a verification cycle.
3. The Perforce maintainer can apply both as a single change set with
   one verification pass.

Either fix is safe to apply alone if needed — they're independent.
A guards against a class of misconfiguration; B fixes the structural
data-flow.

---

## Distribution / Perforce notes

Two files touched:

* `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs`
  — two new guards added; no other behavior changes.
* `Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`
  — `OnEnable` and `OnDisable` overrides modified to skip base
  class calls.

No prefab edits. No asset edits. No scene edits. No new dependencies.
The diff against the active Perforce branch should be small and
mechanical — paste the "After:" snippets from each file's section
above and the change is complete. If the active branch has further
changes to either of these methods, apply the same guard / `base.`
skip pattern at the same logical positions.
