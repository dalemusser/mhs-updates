# Settings / State Coupling via PixelCrushers SaveSystem

Standalone document. Captures a structural issue discovered while debugging
the mouse-sensitivity slider — `SettingsSaver` is wired into the PixelCrushers
`SaveSystem`, which causes user settings to be saved/loaded through the
`/api/state/*` endpoints in addition to the dedicated `/api/settings/*`
endpoints. This breaks the intended separation and produces a class of bugs
that look like "settings ignoring my changes" or "settings reverting to stale
values" without an obvious cause.

Audience: the developer who implemented `SettingsSaver` / `SettingsSaveManager`
/ `DatabaseSaver`, and whoever owns the save server (`stratasave/`).

---

## Design intent (as we understand it)

Two separate persistence concerns, two separate endpoint sets on
`save.adroit.games`:

| Concern | What's persisted | Endpoints | Trigger | Server handler |
|---|---|---|---|---|
| **Game state** | Dialogue variables, quest state, scrap counts, scene location, etc. — per playthrough | `/api/state/save`, `/api/state/load` | Gameplay milestones raise `OnSaveGame` | `stratasave/internal/app/features/saveapi/handler.go` |
| **User settings** | Audio bus volumes, mouse sensitivity, character customization — persistent user preferences | `/api/settings/save`, `/api/settings/load` | Apply button on settings panel raises `OnSaveSettings` | `stratasave/internal/app/features/settingsapi/handler.go` |

This is reflected in:

* Two distinct Mongo collections (`saved_games` and `player_settings`).
* Two distinct HTTP handlers on the server.
* Two distinct manager scripts on the Unity side (`DatabaseSaver` for state,
  `SettingsSaveManager` for settings).
* Two distinct service entries on `__mhsBridgeConfig.services`
  (`state_save`/`state_load` vs. `settings_save`/`settings_load`).

The intent is that **a change to a setting flows exclusively through the
settings endpoints**, and a change to game progress flows exclusively
through the state endpoints. They are independent.

---

## The actual implementation does not honor that separation

`SettingsSaver` (`Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`)
inherits from `PixelCrushers.Saver` and lets the base class register it with
`PixelCrushers.SaveSystem`. That registration means **the PixelCrushers
save/load pipeline treats `SettingsSaver` as one of its data sources** —
even though its data has nothing to do with game state.

### The chain, exactly

1. `SettingsSaver.OnEnable` (inherited path):
   ```csharp
   public override void OnEnable()
   {
       base.OnEnable();                  // <-- PixelCrushers.Saver.OnEnable
       SceneManager.sceneLoaded += OnSceneLoaded;
   }
   ```
   `Saver.OnEnable` (`Assets/Imported/Tools/Pixel Crushers/Common/Scripts/Save System/Savers/Saver.cs:105-108`)
   calls `SaveSystem.RegisterSaver(this)` — `SettingsSaver` is now in the
   PixelCrushers global saver list.

2. `DatabaseSaver.SaveGameToDatabase`
   (`Assets/Scripts/Systems/Save Load System/CustomSavers/DatabaseSaver.cs:112-131`)
   fires whenever `OnSaveGame` is raised. `OnSaveGame` is raised at many
   gameplay milestones: scrap pickups, quest steps, scene transitions,
   mini-game completions, debug-menu commands. There are at least a dozen
   raisers (search GUID `e5dd03085a3a6c74b98aa2f2a5306fb7` across scenes
   and prefabs).

3. `SaveGameToDatabase` calls:
   ```csharp
   SavedGameData savedDataRaw = SaveSystem.RecordSavedGameData();
   ```
   `RecordSavedGameData` iterates **every** registered saver and asks for
   its data — including `SettingsSaver`, which returns
   `SettingsSaver.RecordData()`:

   ```csharp
   SettingsValues data = new() {
       MasterVolume = masterVolume.Value,
       MusicVolume  = musicVolume.Value,
       AmbientVolume = ambientVolume.Value,
       SFXVolume   = sfxVolume.Value,
       VoiceVolume = voiceVolume.Value,
       MouseSensitivity = mouseSensitivity.Value,
       characterCustomizationData = _customizationData ?? new CharacterCustomizationData()
   };
   ```

4. The resulting `SavedGameData` (which now contains both state data **and**
   settings data) is POSTed via `GameSaveManager.SaveCoroutine` to
   `/api/state/save`. The Mongo `saved_games` document for `mhs_developer`
   ends up with a `SettingsSaver` entry inside its `save_data` blob,
   containing every audio bus value and the mouse sensitivity at the moment
   of save.

5. On load, `DatabaseSaver.LoadGameFromDatabaseCoroutine` calls
   `GameSaveManager.LoadGame`, gets the `SavedGameData` back, and
   eventually (after the right scene is loaded) calls:
   ```csharp
   SaveSystem.ApplySavedGameData(saveData);
   ```
   `ApplySavedGameData` (PixelCrushers) iterates **every** registered saver
   and calls `ApplyData` on each. `SettingsSaver.ApplyData` then writes the
   saved values back into the SOAP `FloatVariable`s:
   ```csharp
   masterVolume.Value     = data.MasterVolume;
   musicVolume.Value      = data.MusicVolume;
   ambientVolume.Value    = data.AmbientVolume;
   sfxVolume.Value        = data.SFXVolume;
   voiceVolume.Value      = data.VoiceVolume;
   mouseSensitivity.Value = data.MouseSensitivity;
   ```

   So the **state load** path overwrites settings every time it runs.

In parallel, the dedicated `SettingsSaveManager` ⇄ `/api/settings/*` path
exists and does its own POSTs — but those POSTs share the same SOAP
`FloatVariable`s as their underlying storage. Whichever path applies its
data last wins for the current session, regardless of which has the
newer values on the server.

---

## Why this is a problem

### 1. Two write paths writing to the same in-memory variables

`SettingsSaveManager.SaveSettings` (called from the Apply button) writes
the current settings to `/api/settings/save`. Meanwhile, any number of
gameplay events trigger state saves that capture the same settings into
`/api/state/save`. The two endpoints can — and do — drift apart over time.

### 2. Two read paths writing to the same in-memory variables

On game load, both `LoadSettings` (from `OnLoadSettings`) and
`ApplySavedGameData` (after `DatabaseSaver.LoadGameFromDatabaseCoroutine`)
write settings into the SOAP variables. Whichever fires later sets the
visible value, even if the earlier one had newer or more correct data.

This is the proximate cause of the bug we hit:

* `mhs_developer`'s `/api/state` save has a stale `MouseSensitivity` value
  from before Pass 1 (when the slider went 0..100).
* `/api/settings/load` is returning 400 (separate bug — being diagnosed),
  so no fresh settings are applied that way.
* `/api/state/load` succeeds, triggers `ApplySavedGameData`, which calls
  `SettingsSaver.ApplyData` with the stale value, which SOAP clamps to the
  new max (`2`).
* User sees the slider pegged at maximum on every launch.

Even when we fix `/api/settings/load`, this remains a latent ordering bug —
if state-load fires last, it still overwrites whatever fresh settings the
settings-load just applied.

### 3. Settings are silently checkpointed by gameplay events

Every `OnSaveGame` raiser captures the current `mouseSensitivity.Value` at
that instant, regardless of whether the user has "applied" the change. The
settings panel's Apply / Cancel semantics (`GameplaySettings.cs`
`ApplyGameplayChanges` / `RevertGameplayChanges`) don't gate the state
save — they only gate the in-memory revert when the panel closes. If a
state save fires between the slider drag and the panel close, the
"un-applied" value gets persisted to `/api/state/save` anyway.

### 4. The `/api/settings/save` endpoint becomes effectively cosmetic

If `/api/state` also carries settings, and `/api/state/load` is the path
that fires reliably, the settings POSTs are doing the right thing on the
server but their effect is being overwritten on load. The dedicated
endpoint exists but doesn't influence final user-visible behavior.

### 5. The data model is unclear and surprising

A new dev (or this dev, six months from now) looking at the system will
reasonably expect:
* "Settings live in `/api/settings/*`" — partially true.
* "Game state lives in `/api/state/*`" — true, but state *also* contains
  settings.
* "If I want to know what mouse sensitivity is for `mhs_developer`, query
  `player_settings` collection" — incomplete; the *actual* applied value
  depends on what's in `saved_games` too.

This kind of split-data-model is exactly the situation that creates
support-ticket-shaped bugs months later.

---

## What's required to fix

There are three feasible paths. Recommended is Option A.

### Option A — Decouple `SettingsSaver` from PixelCrushers `SaveSystem` (recommended)

Override `SettingsSaver.OnEnable` and `OnDisable` to skip the base class's
`RegisterSaver` / `UnregisterSaver` calls. The class can still expose
`RecordData` and `ApplyData` (so `SettingsSaveManager` can call them
directly), but PixelCrushers won't automatically pull them into state
save/load.

**Smallest change** — two methods in
`Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`:

```csharp
public override void OnEnable()
{
    // Intentionally do NOT call base.OnEnable().
    // PixelCrushers.Saver.OnEnable registers this saver with
    // PixelCrushers.SaveSystem, which causes settings data to be saved
    // and loaded alongside game state via /api/state/*. Settings are
    // persisted exclusively through SettingsSaveManager ⇄ /api/settings/*.
    SceneManager.sceneLoaded += OnSceneLoaded;
}

public override void OnDisable()
{
    // Match: do not call base.OnDisable() (no UnregisterSaver call).
    SceneManager.sceneLoaded -= OnSceneLoaded;
}
```

**What changes after this:**

| Behavior | Before | After |
|---|---|---|
| `SaveSystem.RecordSavedGameData()` includes `SettingsSaver` data | yes | no |
| `/api/state/save` POST body contains `SettingsSaver` blob | yes | no |
| `SaveSystem.ApplySavedGameData(s)` calls `SettingsSaver.ApplyData` | yes | no |
| `/api/state/load` write-back affects mouse sensitivity / audio | yes | no |
| `/api/settings/save` is the only path that persists settings | no (silent overwrite) | yes |
| `/api/settings/load` (once 400 fixed) is the only path that restores | no (state-load wins) | yes |

**Risks / things to verify after the change:**

* The `CharacterCustomization_DataController` inside `SettingsSaver.ApplyData`
  is also being applied via the state path — verify whether character
  customization is meant to live in settings or in state. If it's a
  settings concern, the decoupling is fine; if it's per-playthrough state,
  we may need a different ownership model.
* No other code paths are calling `SettingsSaver.RecordData` /
  `SettingsSaver.ApplyData` in a way that depends on PixelCrushers
  invocation. We checked: only `SettingsSaveManager` calls these directly,
  which is what we want.
* The `m_key` ("SettingsSaver") is still set on the prefab — harmless after
  decoupling (PixelCrushers won't look it up because the saver isn't
  registered), but worth noting.

### Option B — Strip settings out of the saved-state blob at send/receive time

Modify `DatabaseSaver` to remove the `SettingsSaver` entry from
`SavedGameData` before posting to `/api/state/save`, and to remove it
from the received `saveData` before calling `SaveSystem.ApplySavedGameData`.

Less clean than Option A — it leaves the broken registration in place and
just papers over the symptom at the network boundary. New PixelCrushers
APIs added later could re-introduce the leak.

### Option C — Stop using `PixelCrushers.Saver` for settings entirely

Make `SettingsSaver` a plain `MonoBehaviour` (not a subclass of `Saver`)
with the same `RecordData(): string` / `ApplyData(string)` shape but no
inheritance. `SettingsSaveManager` already calls these methods directly via
`GetComponent<SettingsSaver>()`, so the rename/refactor is small.

Slightly more refactor than Option A. Cleaner conceptually — it expresses
in the type system that settings are not part of PixelCrushers' world. If
team appetite for a bigger change is there, this is the most honest
expression of the architecture.

---

## Migration / backwards-compatibility for existing users

After applying Option A, **existing users with stale settings in their
state save will see one of two outcomes**:

1. **If `/api/settings/load` is fixed and has data for them** — that fresh
   value is applied. The legacy data in `/api/state` is no longer touched
   on load. Fine, no action needed.

2. **If `/api/settings/load` is fixed but has no data for them** (e.g.,
   they've never clicked Apply since the new endpoint went live) — the
   FloatVariable starts at its asset default, which is the new midpoint
   value. The user gets the default first-time experience. Their first
   Apply persists to `/api/settings/save` and they're on the new path.

3. **If `/api/settings/load` is still broken** — the FloatVariable starts at
   the asset default and stays there. User sees the default on every
   reload, even after clicking Apply (because Apply writes to
   `/api/settings/save` which never reads back). This is no worse than
   today's behavior, but a stronger argument for fixing the 400.

Server-side cleanup is **not required**, but is an optional housekeeping
task: a one-off script could delete the `SettingsSaver` key from existing
`saved_games` documents to slim them down. Not critical.

---

## Open questions for the dev who implemented this

1. Was the intent to use PixelCrushers as the single source of truth for
   all persistence (in which case `/api/settings/*` is redundant), or to
   have two independent paths (in which case `SettingsSaver` should not
   be a PixelCrushers `Saver`)?
2. Is character-customization data ("CharacterCustomization_DataController")
   intended to be a settings concern or a per-playthrough state concern?
   The current code puts it inside `SettingsValues` but the decision
   should be explicit.
3. Are there other PixelCrushers `Saver`s in the project whose data is
   actually settings-shaped (audio mix, accessibility options, etc.)?
   This pattern may exist elsewhere.
4. Is there a reason `SettingsSaveManager` doesn't itself raise
   `OnSaveGame` when it saves? If we wanted both endpoints synced, that
   would be one way — but it produces the same drift problem in reverse.

---

## How we got here (history hint)

The two-endpoint design appears to have been added *after* `SettingsSaver`
was already implemented as a PixelCrushers `Saver`. The new
`SettingsSaveManager` was layered on top without removing the existing
PixelCrushers integration. This is a common pattern when a new persistence
concern emerges and the old wiring is left in place "just in case."

Worth re-checking with the original author whether the PixelCrushers
integration of `SettingsSaver` is a deliberate fallback for offline /
config-fetch-failed scenarios or a leftover. If it's deliberate, the
decoupling needs additional care (perhaps a flag to opt-into PixelCrushers
save only when settings_save endpoint is unavailable). If it's leftover,
remove it straightforwardly.

---

## Files involved

* `Assets/Scripts/Systems/Save Load System/CustomSavers/SettingsSaver.cs`
  — the saver whose registration causes the coupling
* `Assets/Scripts/Systems/Save Load System/CustomSavers/DatabaseSaver.cs`
  — orchestrates state save/load; would skip settings after the fix
* `Assets/Scripts/Systems/Save Load System/SettingsSaveManager.cs`
  — owns the settings save/load path
* `Assets/Scripts/Systems/Save Load System/GameSaveManager.cs`
  — network layer for state save/load
* `Assets/Imported/Tools/Pixel Crushers/Common/Scripts/Save System/Savers/Saver.cs`
  — base class whose `OnEnable` does the auto-registration
* `Assets/Prefabs/_Core/Managers/AudioSettingsManager.prefab`
  — prefab carrying both `SettingsSaver` and `SettingsSaveManager` on the
  same GameObject (`m_key: "SettingsSaver"`,
  `m_restoreStateOnStart: 0`, `m_saveAcrossSceneChanges: 1`)
* `stratasave/internal/app/features/saveapi/` — `/api/state/*` server side
* `stratasave/internal/app/features/settingsapi/` — `/api/settings/*` server side
