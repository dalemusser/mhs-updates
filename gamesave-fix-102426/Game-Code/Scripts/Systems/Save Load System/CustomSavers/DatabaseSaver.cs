using System;
using System.Collections;
using System.Collections.Generic;
using AdroitStudios.MHS;
using Obvious.Soap;
using PixelCrushers;
using PixelCrushers.DialogueSystem;
using Sirenix.OdinInspector;
using TMPro;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.SceneManagement;

namespace MHS
{
    // The save policy, on the SaveSystem prefab: when the game saves, what
    // happens when a save or a load does not get through, and what the player
    // sees meanwhile. GameSaveManager (same object) does the requests.
    //
    // Rules this file keeps (mhs-updates/gamesave-fix-102426, 2026-10-24):
    //  - A save is never lost to a failed request. The newest recorded save is
    //    kept on the device and sent until the service accepts it, with backoff
    //    and a visible status (reason, countdown, attempt). An older unsent save
    //    is replaced by a newer one; at most one request is in flight.
    //  - A failed load never starts the unit over. Only the service's explicit
    //    answer "no saves" may start a new game. While the service cannot be
    //    reached the game resumes from the copy kept on this device, if there
    //    is one for this player, and keeps asking the service in the background;
    //    the service's answer wins when it arrives and is newer.
    //  - Nothing is sent without a valid player id, and nothing without the
    //    endpoint config from the host page; both are waited for.
    //  - The unit transition waits for an unsent save, but never longer than a
    //    few seconds; the save stays on the device and the next unit's build
    //    sends it.
    //  - All waits are real time, so a paused game (Time.timeScale = 0) does
    //    not stall saving or loading.
    //  - The auth value is never written to the console.
    //  - A copy the service accepted is the service's to order: at START GAME the
    //    service's answer wins over such a copy; only unsent progress on this
    //    device can beat it, and progress made since a resume is never rewound.
    //  - A page with no save-service settings at all plays without saving: the
    //    settings are static for a page, so there is nothing to wait for.
    //  - An unsent save is never sent over newer progress the service is known to
    //    hold (a late send from a stale tab or a second device must not roll it
    //    back). For a save of this launch that is decided by identity, not clocks:
    //    it is dropped only when the service's newest is an answer this launch did
    //    not adopt as its base.
    //  - One saver per launch; a second one in a scene, or one without its
    //    references, stays inert.
    [RequireComponent(typeof(PlayerPrefsSavedGameDataStorer))]
    public class DatabaseSaver : MonoBehaviour, ICheckForUnitTransition
    {
        [SerializeField] private IntVariable slotNumber;
        [SerializeField] private ScriptableEventInt OnSaveGame;
        [SerializeField] private ScriptableEventInt OnLoadGameFromSlot;
        [SerializeField] private BoolVariable autoSave;

        public string sessionId;
        [SerializeField] private StringVariable playerId;

        [SerializeField] private BoolVariable enableDebugLogs;

        [Header("Addressable Asset References")]
        [SerializeField] private UnityEngine.AddressableAssets.AssetReference autoSaveAssetReference;
        [SerializeField] private UnityEngine.AddressableAssets.AssetReference onSaveGameAssetReference;
        [SerializeField] private UnityEngine.AddressableAssets.AssetReference onLoadGameAssetReference;

        public bool startNewGameIfNoExistingSaveData = true;
        static bool sceneLoaded = false;
        private const string STARTINGSCENE = "Unit 1 Dev";
        public static SavedGameData saveData { get; private set; }

        [SerializeField] private ScriptableEventNoParam saveNotificationFadeInEvent;
        [SerializeField] private ScriptableEventNoParam saveNotificationFadeOutEvent;
        private TextMeshProUGUI _saveNotificationText;

        [SerializeField] private BoolVariable saveGameInProgress;

        public bool ReadyToTransition { get; set; }

#if USING_ADDRESSABLES
        private bool _usingAddressables = true;
#else
        private bool _usingAddressables = false;

#endif

        // ---- Policy constants ----
        private const string GameId = "mhs";
        // The newest save recorded on this device (PlayerPrefs; IndexedDB on
        // WebGL, shared by all unit builds of a site). One slot.
        private const string LocalSaveKey = "mhs_last_save";
        // An unsent save of another player found in the slot is moved here so
        // the next player on a shared device cannot overwrite it.
        private const string PendingSaveKeyPrefix = "mhs_pending_save_";
        // WebGL PlayerPrefs is capped at 1 MB for the whole site, shared with the
        // logger's cache (512 KB); a save is ~45 KB today. Anything over this
        // stays in memory only, so the slot and the pending copies of other
        // players together stay under the cap.
        private const int LocalSaveMaxChars = 128 * 1024;
        private const float SaveRetryInitial = 5f;
        private const float SaveRetryMax = 60f;
        private const float LoadRetryInitial = 3f;
        private const float LoadRetryMax = 30f;
        private const float IdentityPollInterval = 1f;
        private const float ConfigPollInterval = 5f;
        private const float TransitionMaxWaitSeconds = 5f;
        private const float ToastSeconds = 2f;
        private const int LocalRecordFormat = 1;
        // When the service's save has no mhs_meta (made by an older build) its
        // time is the server's clock; a device clock may be off by minutes, so
        // the service's copy counts as newer only by more than this.
        private const float ClockSkewAllowanceSeconds = 90f;
        // Pending saves of other players kept aside on a shared device, at most.
        private const int MaxPendingKeys = 8;
        private const string PendingIndexKey = "mhs_pending_index";
        // How long to wait for a request's callback beyond the request timeout
        // before treating the request as failed.
        private const float RequestWaitMarginSeconds = 15f;
        // The service's answer fetched at the main menu is reused by START GAME
        // for this long; older than that, START asks again (a tab left open at
        // the menu while the student played elsewhere would otherwise resume
        // stale progress and write it over the newer save at the next checkpoint).
        public static float serverAnswerMaxAgeSeconds = 60f;
        // With no save-service settings at all (a page without the host's config,
        // the Editor without a developer config file) nothing can be loaded or
        // saved: after this long START GAME plays a new game without saving. The
        // settings are static for a page, so this is never a network condition.
        public static float noServiceConfigStartSeconds = 5f;
        // How many saves to ask for when the newest one cannot be read as a save.
        private const int LoadLimitWhenNewestUnreadable = 5;

        // One id per launch of the game, and a counter for the saves it makes.
        private static readonly string launchId = Guid.NewGuid().ToString("N");
        private static long sequence = 0;

        // The record kept on the device: the save and what is known about it.
        [Serializable]
        public class LocalSaveRecord
        {
            public int format = LocalRecordFormat;
            public string user_id;
            public long seq;
            public string saved_at;     // device clock, UTC, ISO 8601 round-trip form
            public string session_id;
            public string version;
            public string scene;
            public int checkpoint = -1;
            public bool sent;           // the service accepted it
            public bool dropped;        // the service rejected it for good; kept as a copy, never resent
            public string save_json;    // JsonUtility form of the SavedGameData, with mhs_meta inside

            public bool Unsent => !sent && !dropped;
        }

        // ---- Runtime state ----
        private GameSaveManager saveManager;

        // Saving.
        private LocalSaveRecord newestSave;     // newest recorded this launch, or recovered from the device
        private bool newestRecovered;           // recovered: the service's newest is checked before it is sent
        private bool sendLoopRunning;
        private int saveAttempt;
        private bool localWriteWarned;
        private bool slotIsOurs;                // this launch has written the slot; no other player's save is in it
        private string lastRecordedJson;        // the raw save of the newest record, to skip an identical repeat
        private bool identityWarned;
        private bool configWarned;
        private string localReadForUser;        // the player id whose device record has been read this launch
        private LocalSaveRecord localCopy;      // that record (sent or not), or null

        // Loading: the service's newest save, fetched once per launch (and
        // refreshed after each accepted save), shared by the main menu and
        // START GAME so a launch makes one load request, not two.
        private class ServerLatest
        {
            public bool hasSave;
            public SavedGameData data;
            public GameSaveManager.SaveMeta meta;
            public string time;            // mhs_meta.saved_at (device clock) or the service's timestamp
            public bool timeFromDevice;    // true when time is a device clock, as the device copy's is
        }
        private ServerLatest serverLatest;      // null until the service has answered this launch
        private bool fetchRunning;
        private bool fetchVisible;              // a player is waiting on it: show the status
        private int fetchFailures;              // failed attempts in a row this launch
        private GameSaveManager.LoadResult lastFetchFailure;
        private bool startRunning;
        private bool resumedLocally;            // START GAME is playing from this device's copy while the service is asked
        private bool resumeInProgress;          // a scene load for a resume has been requested and not finished
        private float serverLatestAt;           // when serverLatest was set (unscaled time)
        private bool recoveredWaiting;          // the send loop holds a recovered save until the service has answered
        private bool noServiceConfig;           // this page has no save-service settings: nothing is loaded or saved
        private float noConfigSince = -1f;
        private string resumedBaseTime;         // the time of the state START resumed from while the service was unreachable
        private ServerLatest baselineServer;    // the service answer this launch adopted as its base (resumed from, applied, or its own accepted save)

        // Unit transition.
        private bool transitionPending;
        private float transitionRequestedAt;
        private float lastTransitionCallTime = -1000f;
        private bool transitionPersisted;

        // Status display (the Alert Canvas on this prefab).
        private bool statusShown;
        private Coroutine statusHideRoutine;
        public string StatusText { get; private set; } = "";

        public bool HasUnsentSave => newestSave != null && newestSave.Unsent;
        public int SaveAttempt => saveAttempt;
        public bool ServerAnswered => serverLatest != null;
        public int FetchFailures => fetchFailures;
        public bool StartRunning => startRunning;
        public bool PlayingWithoutService => noServiceConfig;
        public int NewGamesStarted { get; private set; }   // decisions to start a new game this launch
        public bool IsDuplicate => isDuplicate;

        // One saver is in charge per launch (see OnEnable).
        private static DatabaseSaver live;
        private bool isDuplicate;

        private void Start()
        {
            if (isDuplicate) return;
#if UNITY_EDITOR
            autoSave.Value = false; // Disable auto-save in editor by default
#elif UNITY_WEBGL && !UNITY_EDITOR
            autoSave.Value = true; // Enable auto-save in WebGL builds
#endif
            _saveNotificationText = gameObject.GetComponentInChildren<TextMeshProUGUI>();
            if (_saveNotificationText != null)
            {
                _saveNotificationText.text = "";
            }

            if (SceneManager.GetActiveScene().name == "MainMenu")
            {
                // Resets the in-memory save state between launches in the
                // Editor. The copy this device keeps (LocalSaveKey) is not
                // touched: it is what a launch without the service resumes from.
                ClearData();
            }

            if (_usingAddressables == true)
            {
                LoadAddressableScriptableObjects();
            }

            StartCoroutine(RecoverPendingAtLaunch());
        }

        // A save the previous launch could not send is found as soon as the
        // player is known, whatever scene this is, so it goes out even when the
        // menu flow does not run (another host page, the Editor).
        private IEnumerator RecoverPendingAtLaunch()
        {
            string uid;
            while ((uid = ResolveUserId()) == null) yield return new WaitForSecondsRealtime(IdentityPollInterval);
            LocalCopyFor(uid);
        }

        private void OnEnable()
        {
            // The references are checked here, before Start; on the Addressables path
            // they arrive in Start, so only the manager is required then.
            bool wired = GetComponent<GameSaveManager>() != null && (_usingAddressables
                || (slotNumber != null && OnSaveGame != null && OnLoadGameFromSlot != null && autoSave != null
                    && playerId != null && enableDebugLogs != null));
            if (!wired)
            {
                // A saver without its references (a stray component on a UI prefab) can
                // neither save nor load; it stays inert and never takes charge.
                isDuplicate = true;
                Debug.LogWarning($"DatabaseSaver on '{gameObject.name}' in scene '{SceneManager.GetActiveScene().name}' has no references assigned (or no GameSaveManager on its object); it stays inert.");
                return;
            }
            if (live != null && live != this && live.isActiveAndEnabled)
            {
                // A second saver: the gameplay core-systems prefab carries the SaveSystem
                // prefab as well as the main menu's (the PixelCrushers SaveSystem removes
                // the second object at the end of its first frame, but Start would run
                // before that), or a stray component. It does nothing; the first one
                // stays in charge.
                isDuplicate = true;
                Print($"A second DatabaseSaver in scene '{SceneManager.GetActiveScene().name}' stays inert; the first one is in charge.");
                return;
            }
            isDuplicate = false;
            live = this;

            saveManager = GetComponent<GameSaveManager>();
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            if (_usingAddressables == false)
            {
                OnSaveGame.OnRaised += SaveGameToDatabase;
                OnLoadGameFromSlot.OnRaised += LoadGameFromDatabase;
            }

            if (HasUnsentSave) EnsureSendLoop();
        }

        private void OnDisable()
        {
            if (isDuplicate) return;
            OnSaveGame.OnRaised -= SaveGameToDatabase;
            OnLoadGameFromSlot.OnRaised -= LoadGameFromDatabase;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.sceneLoaded -= ApplyDataAfterSceneLoaded;
            // A deactivated object stops its coroutines without their finally
            // blocks; the flags must not stay set with no loop behind them.
            if (!gameObject.activeInHierarchy)
            {
                sendLoopRunning = false;
                fetchRunning = false;
                startRunning = false;
            }
        }

        private void OnDestroy()
        {
            if (live == this) live = null;
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            // A new scene means the transition that was waiting on us is over.
            transitionPending = false;
            resumeInProgress = false;
        }

        public void InitializeDataAfterSceneLoaded(Scene scene)
        {
            if (isDuplicate) return;
            if (scene.name == SceneNameReference.MainMenu)
            {
                ApplyGameProgressFromDatabase();
            }
        }

        // =====================================================================
        // Saving
        // =====================================================================

        /// <summary>
        /// Records the game state and sends it to the save service. The record
        /// is serialized now (the SaveSystem reuses one SavedGameData object),
        /// kept on the device, and sent by the send loop until the service
        /// accepts it. A newer save replaces an older unsent one.
        /// </summary>
        /// <param name="slotN">Default to zero. Should be blank or 0 unless explicitly saving to a different slot number.</param>
        [Button]
        public void SaveGameToDatabase(int slotN = 0)
        {
            if (isDuplicate) return;
            if (!autoSave) return;
            if (noServiceConfig)
            {
                Print("This page has no save service; the checkpoint is not kept.");
                return;
            }
            int slotToUse = slotN == 0 ? slotNumber.Value : slotN;
            slotNumber.Value = slotToUse;

            SavedGameData savedDataRaw = SaveSystem.RecordSavedGameData();
            string json;
            try
            {
                json = SaveSystem.Serialize(savedDataRaw);
            }
            catch (Exception e)
            {
                // Nothing to send; the previous save (if any) stays as it is.
                Debug.LogError($"DatabaseSaver: the game state could not be serialized ({e.Message}); this checkpoint was not saved.");
                return;
            }
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("DatabaseSaver: the game state serialized to nothing; this checkpoint was not saved.");
                return;
            }

            // The same checkpoint is often raised twice within milliseconds (two
            // raisers of OnSaveGame); the second body is byte-identical. Nothing
            // new to keep, so it is not recorded or sent again.
            if (newestSave != null && !newestSave.dropped && json == lastRecordedJson)
            {
                Print("Save identical to the previous one; not sent again.");
                if (!newestSave.Unsent) ShowToast("Game Saved!");
                return;
            }

            var meta = new GameSaveManager.SaveMeta
            {
                seq = ++sequence,
                saved_at = DateTime.UtcNow.ToString("o"),
                session_id = launchId,
                version = Application.version,
                scene = savedDataRaw.sceneName,
                checkpoint = ReadCheckpoint(savedDataRaw),
            };
            var record = new LocalSaveRecord
            {
                user_id = ResolveUserId(),
                seq = meta.seq,
                saved_at = meta.saved_at,
                session_id = meta.session_id,
                version = meta.version,
                scene = meta.scene,
                checkpoint = meta.checkpoint,
                save_json = GameSaveManager.WithMeta(json, meta),
            };

            if (newestSave != null && newestSave.Unsent)
            {
                Print($"A newer save replaces the unsent one (seq {newestSave.seq} -> {record.seq}).");
            }
            newestSave = record;
            newestRecovered = false;
            lastRecordedJson = json;
            saveGameInProgress.Value = true;
            PersistLocal(record);

            Print($"Save recorded: scene {record.scene}, checkpoint {record.checkpoint}, {record.save_json.Length} chars, seq {record.seq}.");
            ShowStatus("Saving Game...");
            EnsureSendLoop();
        }

        private void EnsureSendLoop()
        {
            if (!sendLoopRunning && isActiveAndEnabled) StartCoroutine(SendLoop());
        }

        // Sends the newest unsent save until the service accepts it. Everything
        // about an attempt is local to the pass; only the attempt counter and
        // the backoff carry over, on purpose.
        private IEnumerator SendLoop()
        {
            sendLoopRunning = true;
            try
            {
                float delay = SaveRetryInitial;
                while (true)
                {
                    var item = newestSave;
                    if (item == null || !item.Unsent) break;
                    if (noServiceConfig)
                    {
                        // Nothing can be sent from this page; the record stays on the device.
                        HideStatusUnlessToast();
                        saveGameInProgress.Value = false;
                        break;
                    }

                    // The settings come first (a page without any does not get a player id
                    // either, and must not wait for one for ever), then the id.
                    if (saveManager == null) saveManager = GetComponent<GameSaveManager>();
                    if (saveManager == null)
                    {
                        Debug.LogError("DatabaseSaver: no GameSaveManager on this object; saves cannot be sent.");
                        yield return new WaitForSecondsRealtime(ConfigPollInterval);
                        continue;
                    }
                    if (saveManager.SaveConfig == null)
                    {
                        if (NoServiceConfigAfterGrace()) continue; // the loop ends at its top
                        if (!configWarned)
                        {
                            configWarned = true;
                            Debug.LogWarning("DatabaseSaver: no save endpoint configured (MHSBridge has no state_save config); holding the save until there is one.");
                        }
                        SetStatus("Saving... waiting for the save service settings");
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }

                    string uid = ResolveUserId();
                    if (uid == null)
                    {
                        if (!identityWarned)
                        {
                            identityWarned = true;
                            Debug.LogWarning("DatabaseSaver: no valid player id yet (24 lowercase hex characters); the save waits for it.");
                        }
                        SetStatus("Saving... waiting for your sign-in");
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }
                    if (item.user_id != uid)
                    {
                        item.user_id = uid;
                        PersistLocal(item);
                    }

                    if (newestRecovered && serverLatest == null)
                    {
                        // A save left over from an earlier launch waits for the service's
                        // answer before it goes out (something newer may have been made on
                        // another device meanwhile). The fetch's lines own the display.
                        EnsureFetch(false);
                        recoveredWaiting = true;
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }
                    recoveredWaiting = false;
                    if (serverLatest != null && baselineServer == null) baselineServer = serverLatest; // the launch's first knowledge of the service

                    bool superseded = false;
                    if (serverLatest != null && serverLatest.hasSave)
                    {
                        if (newestRecovered)
                        {
                            // An earlier launch's save has no base of its own to compare
                            // with: the time rule (a server-clock time with the allowance).
                            superseded = ServerNewerThan(serverLatest, item.saved_at);
                        }
                        else
                        {
                            // A save of this launch is newer than the state it was played
                            // from, whatever two devices' clocks say. It is superseded only
                            // when the service has moved on to an answer this launch did not
                            // adopt as its base (someone else saved since).
                            superseded = baselineServer != null && !ReferenceEquals(serverLatest, baselineServer);
                        }
                    }
                    if (superseded)
                    {
                        // Sending it would roll the newer progress back. Kept as a copy,
                        // never sent; the player is told.
                        Debug.LogWarning($"DatabaseSaver: the save service already holds newer progress ({serverLatest.time}) than the unsent save from {item.saved_at}; the unsent save is dropped.");
                        item.dropped = true;
                        PersistAfterRequest(item);
                        RemovePendingKey(uid);
                        FinishItem(item, "Not saved: the save service has newer progress.");
                        continue;
                    }

                    saveAttempt++;
                    if (saveAttempt == 1) SetStatus("Saving Game...");
                    else SetStatus($"Saving Game... (attempt {saveAttempt})");

                    GameSaveManager.RequestResult result = null;
                    saveManager.SaveJson(uid, GameId, item.save_json, r => result = r);
                    float deadline = Time.unscaledTime + GameSaveManager.requestTimeoutSeconds + RequestWaitMarginSeconds;
                    while (result == null && Time.unscaledTime < deadline) yield return null;
                    if (result == null)
                    {
                        // The request never called back (it should always; this is a guard).
                        result = new GameSaveManager.RequestResult { outcome = GameSaveManager.Outcome.Failed, retryable = true, error = "no answer from the request", reason = "the save service did not answer in time" };
                    }

                    if (result.Accepted)
                    {
                        item.sent = true;
                        PersistAfterRequest(item);
                        RemovePendingKey(uid);
                        RememberAsServerNewest(item);
                        saveAttempt = 0;
                        delay = SaveRetryInitial;
                        Print("Save confirmed by the server: " + Truncate(result.body, 160));
                        FinishItem(item, "Game Saved!");
                        continue;
                    }

                    if (!result.retryable)
                    {
                        // The service rejected this save for good (400, 413). Sending it
                        // again cannot succeed; it is kept on the device as a copy but
                        // not resent. This is a game or service defect, so it is loud.
                        Debug.LogError($"DatabaseSaver: the save service rejected a save for good ({result.statusCode} {result.error}; {Truncate(result.body, 200)}); it will not be resent.");
                        item.dropped = true;
                        PersistAfterRequest(item);
                        RemovePendingKey(uid);
                        saveAttempt = 0;
                        delay = SaveRetryInitial;
                        FinishItem(item, "Caution: Game Save Failed...");
                        continue;
                    }

                    // Transient: wait with a visible countdown, then try again. A
                    // newer save recorded meanwhile is picked up by the next pass.
                    float wait = result.retryAfterSeconds > 0 ? result.retryAfterSeconds : delay;
                    delay = Mathf.Min(delay * 2f, SaveRetryMax);
                    Debug.LogWarning($"DatabaseSaver: save not accepted ({result.statusCode} {result.error}; {result.reason}); attempt {saveAttempt}, retrying in {wait:F0} s.");
                    yield return Countdown(wait, remaining => $"Not saved yet: {result.reason}.\nRetrying in {remaining} s (attempt {saveAttempt + 1})", null, () => newestSave != item || !item.Unsent);
                    if (newestSave != item)
                    {
                        // A newer save replaced this one during the wait: it goes out now,
                        // with its own count and delay.
                        saveAttempt = 0;
                        delay = SaveRetryInitial;
                    }
                }
            }
            finally
            {
                sendLoopRunning = false;
                recoveredWaiting = false;
            }
        }

        // After a request the slot keeps this device's newest record. The item that
        // was sent may be an older one that a newer save replaced while it was in
        // flight; writing it back would overwrite the newer, unsent record.
        private void PersistAfterRequest(LocalSaveRecord item)
        {
            PersistLocal(newestSave ?? item);
        }

        // The item's story ends (accepted or dropped). If it is still the
        // newest save, the save flag comes down and the player sees the toast.
        private void FinishItem(LocalSaveRecord item, string toast)
        {
            if (newestSave == item)
            {
                saveGameInProgress.Value = false;
                if (!string.IsNullOrEmpty(toast)) ShowToast(toast);
                else HideStatusUnlessToast();
            }
        }

        // After the service accepted a save, it is the newest the service holds.
        private void RememberAsServerNewest(LocalSaveRecord item)
        {
            var data = ParseSave(item.save_json);
            if (data == null) return;
            serverLatest = new ServerLatest
            {
                hasSave = true,
                data = data,
                meta = new GameSaveManager.SaveMeta { seq = item.seq, saved_at = item.saved_at, session_id = item.session_id, version = item.version, scene = item.scene, checkpoint = item.checkpoint },
                time = item.saved_at,
                timeFromDevice = true,
            };
            serverLatestAt = Time.unscaledTime;
            baselineServer = serverLatest;
        }

        // An unsent record made before a newer service save is applied in place: the
        // state it holds is being replaced on screen, so it is kept as a copy, never
        // sent (the send loop's countdown for it ends at once).
        private void DropUnsentAsSuperseded()
        {
            var item = newestSave;
            if (item == null || !item.Unsent) return;
            Debug.LogWarning($"DatabaseSaver: the unsent save from {item.saved_at} is superseded by the newer progress being loaded from the service; it is dropped.");
            item.dropped = true;
            PersistLocal(item);
            RemovePendingKey(ResolveUserId());
            saveGameInProgress.Value = false;
        }

        // =====================================================================
        // Loading
        // =====================================================================

        /// <summary>
        /// Loads saved data from database or playerprefs
        /// </summary>
        /// <param name="slotN">The save slot number to use. Default is 0. Give it a number to set the slot to save to by default.</param>
        [Button]
        public void LoadGameFromDatabase(int slotN = 0)
        {
            if (isDuplicate) return;
            ResetFlag();
            slotNumber.Value = slotN;
            LoadGameFromDatabaseCoroutine();
        }

        public bool CheckIfSceneShouldLoad(string sceneName)
        {
            int sceneNumber = SceneNumber(sceneName);
            int savedSceneNumber = saveData != null ? SceneNumber(saveData.sceneName) : -1;

            if (savedSceneNumber == -1)
            {
                return false;
            }

            return savedSceneNumber > sceneNumber;
        }

        // The unit digit at position 5 of a scene name ("Unit 4 Dev" -> 4), or -1.
        private static int SceneNumber(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || sceneName.Length < 6) return -1;
            return Int32.TryParse(sceneName[5].ToString(), out int n) ? n : -1;
        }

        // Main menu: fetch the newest save as soon as the player is known and
        // apply its dialogue state (quest log) while the menu is showing. START
        // GAME reuses the answer, so a launch makes one load request.
        private void ApplyGameProgressFromDatabase()
        {
            if (!TryGetComponent<GameSaveManager>(out saveManager))
            {
                ConsoleLogManager.Instance?.PrintToConsole("No game save manager found.", true, ConsoleLogManager.ConsoleLogType.Error);
                return;
            }
            Print("Applying Progress From Database in Main Menu...");
            EnsureFetch(false);
            StartCoroutine(ApplyMenuProgressWhenKnown());
        }

        private IEnumerator ApplyMenuProgressWhenKnown()
        {
            // Wait for the service's answer, or for the first failure if this
            // device has a copy to show meanwhile.
            while (serverLatest == null && !noServiceConfig && !(fetchFailures > 0 && LocalCopyFor(ResolveUserId()) != null))
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (noServiceConfig) yield break;
            if (serverLatest != null && baselineServer == null) baselineServer = serverLatest;
            var chosen = ChooseResume(LocalCopyFor(ResolveUserId()), serverLatest);
            if (chosen == null)
            {
                Print("No saves found for this player yet.");
                yield break;
            }
            saveData = chosen;
            Print("Save Data Received - " + saveData.sceneName);
            var dialogueSystemSaver = FindFirstObjectByType<DialogueSystemSaver>();
            if (dialogueSystemSaver != null)
            {
                var dialogueData = saveData.GetData(dialogueSystemSaver.key);
                if (!string.IsNullOrEmpty(dialogueData))
                {
                    try { dialogueSystemSaver.ApplyData(dialogueData); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
        }

        /// <summary>
        /// START GAME: resume from the newest save. The service's explicit "no
        /// saves" starts a new game; a failed request never does. While the
        /// service cannot be reached the game resumes from this device's copy
        /// (if there is one for this player) and keeps asking in the background.
        /// </summary>
        public void LoadGameFromDatabaseCoroutine()
        {
            if (!TryGetComponent<GameSaveManager>(out saveManager))
            {
                ConsoleLogManager.Instance?.PrintToConsole("No game save manager found.", true, ConsoleLogManager.ConsoleLogType.Error);
                return;
            }
            if (startRunning)
            {
                Print("Start already in progress; ignoring a second request.");
                return;
            }
            StartCoroutine(StartFromSave());
        }

        private IEnumerator StartFromSave()
        {
            startRunning = true;
            try
            {
                Print("Loading Game From Database...");
                ShowStatus("Loading your progress...");
                ServerLatest aged = null;
                if (serverLatest != null && Time.unscaledTime - serverLatestAt > serverAnswerMaxAgeSeconds)
                {
                    // The menu's answer is old (a tab left open): ask again rather than
                    // resume progress another device may have moved past meanwhile. The
                    // old answer stays as the fallback should the service not answer now.
                    Print($"The service's answer is {Time.unscaledTime - serverLatestAt:F0} s old; asking again.");
                    aged = serverLatest;
                    serverLatest = null;
                }
                EnsureFetch(true);

                bool usedLocal = false;
                resumedLocally = false;
                resumedBaseTime = null;
                LocalSaveRecord local = null;
                while (serverLatest == null && !noServiceConfig)
                {
                    string uid = ResolveUserId();
                    if (uid != null && local == null) local = LocalCopyFor(uid);
                    if (!usedLocal && fetchFailures > 0 && (local != null || (aged != null && aged.hasSave)))
                    {
                        // The service is not answering. Play on from what this device knows
                        // (its earlier answer from the service, or the copy of the last save;
                        // ChooseResume ranks them) and keep asking.
                        bool fromAged = aged != null && aged.hasSave;
                        var fallback = fromAged ? ChooseResume(local, aged) : (local != null ? ParseSave(local.save_json) : null);
                        if (fallback != null)
                        {
                            usedLocal = true;
                            resumedLocally = true;
                            if (fromAged && fallback == aged.data)
                            {
                                resumedBaseTime = aged.time;
                                Debug.LogWarning($"DatabaseSaver: the save service is not answering ({lastFetchFailure?.reason}); resuming from its earlier answer ({aged.data.sceneName}, {aged.time}) and still checking the server.");
                            }
                            else
                            {
                                resumedBaseTime = local.saved_at;
                                Debug.LogWarning($"DatabaseSaver: the save service is not answering ({lastFetchFailure?.reason}); resuming from the copy on this device ({local.scene}, checkpoint {local.checkpoint}, {local.saved_at}{(local.Unsent ? ", not yet sent" : "")}) and still checking the server.");
                            }
                            ShowStatus("Using the progress saved on this device.\nStill checking the save service...");
                            ResumeWith(fallback);
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.25f);
                }

                if (noServiceConfig && serverLatest == null)
                {
                    // Nothing can be loaded or saved on this page (no save-service
                    // settings at all). Play, without saving.
                    Debug.LogWarning("DatabaseSaver: this page has no save-service settings; starting a new game without saving.");
                    ShowToast("No save service on this page.\nPlaying without saving.");
                    StartNewGame(true);
                    yield break;
                }

                // The service has answered. Decide against this device's newest
                // progress as of now: the state the game resumed from (if it did) and
                // anything recorded since, not the copy read at the start.
                var latestHere = LocalCopyFor(ResolveUserId());
                var chosen = ChooseResume(latestHere, serverLatest);

                if (usedLocal)
                {
                    string baseTime = latestHere != null ? latestHere.saved_at : null;
                    if (resumedBaseTime != null && (baseTime == null || IsNewer(resumedBaseTime, baseTime, 0f))) baseTime = resumedBaseTime;
                    // Progress made in this launch is never rewound on the server's clock
                    // alone (an old build's save without metadata).
                    bool progressThisLaunch = newestSave != null && !newestRecovered;
                    bool serverNewer = serverLatest.hasSave && (baseTime == null || ServerNewerThan(serverLatest, baseTime))
                        && (serverLatest.timeFromDevice || !progressThisLaunch);
                    if (serverNewer && chosen == serverLatest.data)
                    {
                        // Let the scene load that the local resume started finish first.
                        while (resumeInProgress || SceneManager.GetActiveScene().name == "Transition") yield return null;
                        if (chosen.sceneName == SceneManager.GetActiveScene().name)
                        {
                            // Same scene: the newer state replaces what is on screen; an
                            // unsent record made before this moment is superseded by it.
                            Debug.LogWarning("DatabaseSaver: the save service holds newer progress than this device's copy; loading it.");
                            ShowStatus("Newer progress found on the save service.\nLoading it...");
                            DropUnsentAsSuperseded();
                            ResumeWith(chosen);
                            baselineServer = serverLatest;
                            ShowToast("Progress loaded");
                        }
                        else
                        {
                            // Another scene (another unit's): pulling the player out of the
                            // scene they are in is worse than playing on, but nothing this
                            // session saves may overwrite the further progress; the send
                            // loop drops its checkpoints (each with a toast), and the next
                            // launch resolves it from the service as usual.
                            Debug.LogWarning($"DatabaseSaver: the save service holds newer progress in another scene ({chosen.sceneName}); the player stays in {SceneManager.GetActiveScene().name}, this session's saves are not sent, and the next launch uses the service's save.");
                            saveData = chosen;
                            ShowToast("The save service has newer progress in another unit.\nThis session is not saved.");
                        }
                    }
                    else
                    {
                        // In step with the service (or ahead of it): its answer is the base.
                        baselineServer = serverLatest;
                        HideStatusUnlessToast();
                    }
                    yield break;
                }

                if (chosen != null)
                {
                    HideStatus();
                    ResumeWith(chosen);
                    baselineServer = serverLatest;
                    yield break;
                }

                // The service says there are no saves (and this device has no
                // unsent one): a new game.
                HideStatus();
                baselineServer = serverLatest;
                Print("No saves found; starting a new game.");
                StartNewGame(false);
            }
            finally
            {
                startRunning = false;
                resumedLocally = false;
            }
        }

        // A new game from the main menu. In the Editor only when a player override
        // id is set (as before), unless forced: with no save service there is no
        // progress to protect.
        private void StartNewGame(bool force)
        {
            NewGamesStarted++;
#if UNITY_EDITOR
            startNewGameIfNoExistingSaveData = FindFirstObjectByType<AuthManager>()?.playerOverrideId != "";
#endif
            if (!startNewGameIfNoExistingSaveData && !force) return;
            if (TryGetComponent(out TransitionSceneLoader sceneLoader))
            {
                sceneLoader.LoadNextGameplayScene();
            }
            else
            {
                sceneLoader = FindFirstObjectByType<TransitionSceneLoader>();
                sceneLoader?.LoadNextGameplayScene();
            }
        }

        // Resumes from a save: load its scene (through the transition scene) and
        // apply the data once that scene is up, or apply it in place when the
        // scene is already the current one.
        private void ResumeWith(SavedGameData data)
        {
            saveData = data;
            Print("Resuming from save - " + saveData.sceneName);
            if (CheckIfSceneShouldLoad(SceneManager.GetActiveScene().name))
            {
                SceneManager.sceneLoaded -= ApplyDataAfterSceneLoaded;
                SceneManager.sceneLoaded += ApplyDataAfterSceneLoaded;
                resumeInProgress = true;
                var loader = FindFirstObjectByType<TransitionSceneLoader>();
                //Check if saved level exists in build settings
                if (SceneLoadingUtilities.IsSceneInBuildSettings(saveData.sceneName) == false)
                {
                    //If saved scene doesn't exist in build settings load next gameplay scene in build settings hierarchy
                    ConsoleLogManager.Instance?.PrintToConsole($"{saveData.sceneName} does not exist in Build Settings. Moving to next gameplay scene", true, ConsoleLogManager.ConsoleLogType.Warning);
                    loader?.LoadNextGameplayScene();
                }
                else
                {
                    //If saved scene exists in build settings load it
                    loader?.SetSceneToLoadThenLoadTransitionScene(saveData.sceneName);
                }
            }
            else
            {
                SaveSystem.ApplySavedGameData(saveData);
            }
        }

        // Which save to resume from, given this device's copy and the service's
        // answer. Null means "no save anywhere": start a new game.
        private SavedGameData ChooseResume(LocalSaveRecord local, ServerLatest server)
        {
            if (server == null)
            {
                return local != null ? ParseSave(local.save_json) : null;
            }
            if (!server.hasSave)
            {
                // The service answered "no saves". A sent copy on this device is
                // stale then (the saves were deleted on purpose); one the service
                // never accepted (unsent, or rejected for good) is progress that
                // exists only here, and it stays.
                if (local != null && !local.sent) return ParseSave(local.save_json);
                return null;
            }
            if (local == null) return server.data;
            if (!local.sent)
            {
                // The pending save is the newest this device knows; the service's
                // copy wins only when it is demonstrably newer (another device).
                if (ServerNewerThan(server, local.saved_at)) return server.data;
                return ParseSave(local.save_json) ?? server.data;
            }
            // A copy the service accepted: the service's answer is authoritative.
            // (Deciding it by time would pit two device clocks against each other;
            // a clock minutes ahead would resume this device's older progress over
            // newer progress made elsewhere, to be overwritten at the next checkpoint.)
            return server.data;
        }

        // The save-service settings are static for a page: either the host gave them
        // before the game started or it never will. After a short grace (the bridge
        // object may still be coming up) the page is taken to have no save service;
        // both loops stop on it, and START GAME plays a new game without saving.
        private bool NoServiceConfigAfterGrace()
        {
            if (noServiceConfig) return true;
            if (saveManager == null) saveManager = GetComponent<GameSaveManager>();
            bool none = saveManager == null || (saveManager.SaveConfig == null && saveManager.LoadConfig == null);
            if (!none)
            {
                // One endpoint is configured: a partial setup to wait on, not "no service".
                noConfigSince = -1f;
                return false;
            }
            if (noConfigSince < 0) noConfigSince = Time.unscaledTime;
            if (Time.unscaledTime - noConfigSince < noServiceConfigStartSeconds) return false;
            noServiceConfig = true;
            Debug.LogWarning($"DatabaseSaver: no save-service settings on this page (MHSBridge has no state_save/state_load config after {noServiceConfigStartSeconds:F0} s); nothing will be loaded or saved.");
            return true;
        }

        // ---- The shared fetch of the service's newest save ----

        private void EnsureFetch(bool visible)
        {
            fetchVisible |= visible;
            if (!fetchRunning && isActiveAndEnabled) StartCoroutine(FetchLoop());
        }

        // Asks the service for the newest save until it answers (with saves or
        // with none). Failures count and are shown when a player is waiting.
        private IEnumerator FetchLoop()
        {
            fetchRunning = true;
            try
            {
                float delay = LoadRetryInitial;
                int attempt = 0;
                int limit = 1;
                while (serverLatest == null && !noServiceConfig)
                {
                    // The settings come first: a page without any does not get a player
                    // id either, and must not wait for one for ever.
                    if (saveManager == null) saveManager = GetComponent<GameSaveManager>();
                    if (saveManager == null || saveManager.LoadConfig == null)
                    {
                        if (NoServiceConfigAfterGrace()) break;
                        if (!configWarned)
                        {
                            configWarned = true;
                            Debug.LogWarning("DatabaseSaver: no load endpoint configured (MHSBridge has no state_load config); waiting for it.");
                        }
                        if (FetchLineVisible()) SetStatus(FetchLineWaiting("waiting for the save service settings"));
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }
                    noConfigSince = -1f;

                    string uid = ResolveUserId();
                    if (uid == null)
                    {
                        if (!identityWarned)
                        {
                            identityWarned = true;
                            Debug.LogWarning("DatabaseSaver: no valid player id yet (24 lowercase hex characters); the load waits for it.");
                        }
                        if (FetchLineVisible()) SetStatus(FetchLineWaiting("waiting for your sign-in"));
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }

                    attempt++;
                    if (FetchLineVisible()) SetStatus(FetchLineWaiting(attempt == 1 ? null : $"(attempt {attempt})"));
                    GameSaveManager.LoadResult result = null;
                    saveManager.LoadLatest(uid, GameId, limit, r => result = r);
                    float deadline = Time.unscaledTime + GameSaveManager.requestTimeoutSeconds + RequestWaitMarginSeconds;
                    while (result == null && Time.unscaledTime < deadline) yield return null;
                    if (result == null)
                    {
                        result = new GameSaveManager.LoadResult { outcome = GameSaveManager.Outcome.Failed, retryable = true, error = "no answer from the request", reason = "the save service did not answer in time" };
                    }

                    if (result.outcome == GameSaveManager.Outcome.Accepted && !result.HasReadable)
                    {
                        // The service answered with documents, but none of them reads as a
                        // save. Only an empty list may mean "no saves": ask for the last few
                        // and resume from the newest readable one; if there is none, this
                        // is a failure to retry (and for staff to look at), not a new game.
                        if (limit < LoadLimitWhenNewestUnreadable && result.saves.Length >= limit)
                        {
                            Debug.LogWarning($"DatabaseSaver: the service's newest save cannot be read; asking for the last {LoadLimitWhenNewestUnreadable}.");
                            limit = LoadLimitWhenNewestUnreadable;
                            yield return new WaitForSecondsRealtime(1f);
                            continue;
                        }
                        result.outcome = GameSaveManager.Outcome.Failed;
                        result.retryable = true;
                        result.error = "no readable save in the answer";
                        result.reason = "the save service answered with something unreadable";
                    }

                    if (result.outcome != GameSaveManager.Outcome.Failed)
                    {
                        var newest = result.Readable;
                        bool isSave = newest != null;
                        var meta = isSave ? result.ReadableMeta : null;
                        if (isSave && result.ReadableIndex > 0) Debug.LogWarning($"DatabaseSaver: resuming from the service's save #{result.ReadableIndex + 1}; the {result.ReadableIndex} newer could not be read.");
                        serverLatest = new ServerLatest
                        {
                            hasSave = isSave,
                            data = isSave ? newest.save_data : null,
                            meta = meta,
                            time = isSave ? (meta != null ? meta.saved_at : newest.timestamp) : null,
                            timeFromDevice = meta != null,
                        };
                        serverLatestAt = Time.unscaledTime;
                        fetchFailures = 0;
                        Print(serverLatest.hasSave ? $"Server save received: {serverLatest.data.sceneName} ({serverLatest.time}{(meta == null ? ", server clock" : "")})" : "Server answered: no saves.");
                        break;
                    }

                    fetchFailures++;
                    lastFetchFailure = result;
                    float wait = result.retryAfterSeconds > 0 ? result.retryAfterSeconds : delay;
                    delay = Mathf.Min(delay * 2f, LoadRetryMax);
                    Debug.LogWarning($"DatabaseSaver: load not answered ({result.statusCode} {result.error}; {result.reason}); attempt {attempt}, retrying in {wait:F0} s.");
                    // One line, one owner: the countdown shows while a player waits at
                    // START GAME (and the game has not resumed from the device copy), or
                    // while a recovered save waits for this answer.
                    yield return Countdown(wait, remaining => FetchLineFailed(result.reason, remaining, attempt + 1), FetchLineVisible);
                }
            }
            finally
            {
                fetchRunning = false;
                fetchVisible = false;
            }
        }

        // =====================================================================
        // The copy on the device
        // =====================================================================

        // This player's record on the device: the slot, or a pending save moved
        // aside for them. Read once per player per launch; an unsent record
        // becomes the save to send.
        private LocalSaveRecord LocalCopyFor(string uid)
        {
            if (uid == null) return null;
            if (localReadForUser != uid) ReadLocalCopy(uid);
            // A save recorded this launch is newer than anything read from the device at launch.
            if (newestSave != null && newestSave.user_id == uid && (localCopy == null || IsNewer(newestSave.saved_at, localCopy.saved_at, 0f))) return newestSave;
            return localCopy;
        }

        private void ReadLocalCopy(string uid)
        {
            localReadForUser = uid;
            localCopy = null;

            var slot = ReadRecord(LocalSaveKey);
            var pending = ReadRecord(PendingSaveKeyPrefix + uid);
            LocalSaveRecord mine = slot != null && slot.user_id == uid ? slot : null;
            if (pending != null && pending.user_id == uid)
            {
                if (mine == null || IsNewer(pending.saved_at, mine.saved_at, 0f) || (!mine.Unsent && pending.Unsent)) mine = pending;
            }
            localCopy = mine;
            if (mine == null) return;

            Print($"Device copy for this player: {mine.scene}, checkpoint {mine.checkpoint}, {mine.saved_at}, {(mine.Unsent ? "NOT sent" : mine.dropped ? "dropped" : "sent")}.");
            if (mine.Unsent && (newestSave == null || IsNewer(mine.saved_at, newestSave.saved_at, 0f)))
            {
                // Progress the service never got: send it (after the server check).
                Debug.LogWarning($"DatabaseSaver: an unsent save from an earlier launch was found on this device ({mine.scene}, checkpoint {mine.checkpoint}, {mine.saved_at}); it will be sent once the service is reachable.");
                newestSave = mine;
                newestRecovered = true;
                saveGameInProgress.Value = true;
                EnsureSendLoop();
            }
        }

        private static LocalSaveRecord ReadRecord(string key)
        {
            try
            {
                if (!PlayerPrefs.HasKey(key)) return null;
                string json = PlayerPrefs.GetString(key);
                if (string.IsNullOrEmpty(json)) return null;
                var rec = JsonUtility.FromJson<LocalSaveRecord>(json);
                if (rec == null || string.IsNullOrEmpty(rec.save_json) || string.IsNullOrEmpty(rec.user_id)) return null;
                return rec;
            }
            catch (Exception e)
            {
                // An unreadable record would fail the same way every launch.
                Debug.LogWarning($"DatabaseSaver: the device's save record '{key}' could not be read ({e.Message}); discarding it.");
                try { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); } catch (Exception) { }
                return null;
            }
        }

        // Writes the record to the slot. An unsent record of another player in
        // the slot is moved aside first, so a shared device loses nobody's
        // progress.
        private void PersistLocal(LocalSaveRecord record)
        {
            if (record == null) return;
            string json;
            try
            {
                json = JsonUtility.ToJson(record);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DatabaseSaver: the save record could not be serialized ({e.Message}); it stays in memory only.");
                return;
            }
            if (json.Length > LocalSaveMaxChars)
            {
                if (!localWriteWarned)
                {
                    localWriteWarned = true;
                    Debug.LogWarning($"DatabaseSaver: the save is too large to keep on the device ({json.Length} chars); it stays in memory only for this launch.");
                }
                return;
            }
            if (!slotIsOurs) MoveAsideOtherPlayersUnsentSave(record.user_id);
            try
            {
                PlayerPrefs.SetString(LocalSaveKey, json);
                PlayerPrefs.Save();
                slotIsOurs = !string.IsNullOrEmpty(record.user_id);
                localWriteWarned = false;
            }
            catch (Exception e)
            {
                // Storage refused the write (quota, private window, site data
                // blocked). Saving to the service carries on; only the device
                // copy is missing.
                if (!localWriteWarned)
                {
                    localWriteWarned = true;
                    Debug.LogWarning($"DatabaseSaver: could not keep the save on the device ({e.Message}); the service copy is unaffected.");
                }
            }
        }

        // An unsent record of another player in the slot is kept aside under their
        // own key before the slot is overwritten. Its own guard: when the device's
        // storage is full, the oldest pending copy is evicted and the move tried
        // once more; failing that, the move is skipped (once, loudly) and the
        // current player's save is still written.
        private static void MoveAsideOtherPlayersUnsentSave(string ourUid)
        {
            var slot = ReadRecord(LocalSaveKey);
            if (slot == null || !slot.Unsent || string.IsNullOrEmpty(slot.user_id) || slot.user_id == ourUid) return;
            string json = JsonUtility.ToJson(slot);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    AddPendingKey(slot.user_id, json);
                    return;
                }
                catch (Exception e)
                {
                    if (attempt == 0 && EvictOldestPending()) continue;
                    Debug.LogWarning($"DatabaseSaver: could not keep aside another player's unsent save ({e.Message}); the device's storage is full, and this player's save takes the slot.");
                    return;
                }
            }
        }

        private static bool EvictOldestPending()
        {
            // Frees the oldest pending copy that is actually present; entries whose
            // key is already gone (a failed write, an unreadable record discarded)
            // are only dropped from the index.
            var index = ReadPendingIndex();
            bool evicted = false;
            try
            {
                while (index.Count > 0 && !evicted)
                {
                    string oldest = index[0];
                    index.RemoveAt(0);
                    string key = PendingSaveKeyPrefix + oldest;
                    if (!PlayerPrefs.HasKey(key)) continue;
                    PlayerPrefs.DeleteKey(key);
                    evicted = true;
                    Debug.LogWarning($"DatabaseSaver: the device's storage is full; the oldest pending save of another player (…{oldest.Substring(18)}) was dropped.");
                }
                PlayerPrefs.SetString(PendingIndexKey, string.Join(",", index));
            }
            catch (Exception) { }
            return evicted;
        }

        // The pending saves of other players are few and bounded: an index key
        // lists them oldest first, and the oldest goes when there are too many,
        // so a shared device cannot fill its PlayerPrefs with them.
        private static List<string> ReadPendingIndex()
        {
            var list = new List<string>();
            try
            {
                string v = PlayerPrefs.HasKey(PendingIndexKey) ? PlayerPrefs.GetString(PendingIndexKey) : "";
                foreach (var id in v.Split(',')) if (GameSaveManager.IsValidUserId(id)) list.Add(id);
            }
            catch (Exception) { }
            return list;
        }

        private static void AddPendingKey(string uid, string json)
        {
            var index = ReadPendingIndex();
            index.Remove(uid);
            var overflow = new List<string>();
            while (index.Count >= MaxPendingKeys)
            {
                overflow.Add(index[0]);
                index.RemoveAt(0);
            }
            index.Add(uid);
            // The index first: a failed write later leaves at worst a listed, absent key,
            // which eviction skips; nothing is deleted before the index says so.
            PlayerPrefs.SetString(PendingIndexKey, string.Join(",", index));
            foreach (var oldest in overflow)
            {
                PlayerPrefs.DeleteKey(PendingSaveKeyPrefix + oldest);
                Debug.LogWarning($"DatabaseSaver: too many pending saves of other players on this device; the oldest (player …{oldest.Substring(18)}) was dropped.");
            }
            PlayerPrefs.SetString(PendingSaveKeyPrefix + uid, json);
        }

        private static void RemovePendingKey(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            try
            {
                bool had = PlayerPrefs.HasKey(PendingSaveKeyPrefix + uid);
                if (had) PlayerPrefs.DeleteKey(PendingSaveKeyPrefix + uid);
                var index = ReadPendingIndex();
                if (index.Remove(uid)) PlayerPrefs.SetString(PendingIndexKey, string.Join(",", index));
                if (had) PlayerPrefs.Save();
            }
            catch (Exception) { }
        }

        /// <summary>Removes the copy of the last save kept on this device (developer use).</summary>
        [Button]
        public void ClearLocalSaveCopy()
        {
            try
            {
                PlayerPrefs.DeleteKey(LocalSaveKey);
                string uid = ResolveUserId();
                if (uid != null) RemovePendingKey(uid);
                PlayerPrefs.Save();
            }
            catch (Exception) { }
            localReadForUser = null;
            localCopy = null;
        }

        [Button]
        public void ClearData(int slotN = 0)
        {
            int slotToUse = slotN == 0 ? slotNumber.Value : slotN;
            SaveSystem.DeleteSavedGameInSlot(slotToUse);
            SaveSystem.ResetGameState();
            PlayerPrefs.SetString($"SaveSlot{slotToUse}", "");
        }

        public void ApplyDataAfterSceneLoaded(Scene sceneName, LoadSceneMode sceneMode)
        {
            // Only apply data if correct scene is loaded
            if (saveData != null && SceneManager.GetActiveScene().name == saveData.sceneName)
            {
                StartCoroutine(WaitToApplyData());
            }
        }

        private IEnumerator WaitToApplyData()
        {
            Print("Applying Data...");
            yield return new WaitForEndOfFrame();
            SceneManager.sceneLoaded -= ApplyDataAfterSceneLoaded;
            resumeInProgress = false;
            SaveSystem.ApplySavedGameData(saveData);
            Print("Data Applied!");
        }

        public void ResetFlag()
        {
            sceneLoaded = false;
        }

        // =====================================================================
        // Unit transition gate (ICheckForUnitTransition)
        // =====================================================================

        // The transition waits while a save is unsent, but never for more than
        // TransitionMaxWaitSeconds: with the service unreachable the send loop
        // retries indefinitely, and holding the unit change for that would
        // strand the player. The save is on the device; the next unit's build
        // finds it there and sends it.
        public void CallToTransitionToNextUnit()
        {
            if (isDuplicate)
            {
                ReadyToTransition = true;
                return;
            }
            float now = Time.unscaledTime;
            // The loader polls every frame or two while it waits; a gap of more
            // than a second since the last call means a new transition is asking.
            // (A frame-count rule here would reset on every two-frame poll and the
            // cap would never fire.)
            if (!transitionPending || now - lastTransitionCallTime > 1f)
            {
                transitionPending = true;
                transitionRequestedAt = now;
                transitionPersisted = false;
            }
            lastTransitionCallTime = now;

            bool nothingUnsent = newestSave == null || !newestSave.Unsent || noServiceConfig;
            bool timedOut = now - transitionRequestedAt >= TransitionMaxWaitSeconds;
            ReadyToTransition = nothingUnsent || timedOut;
            if (ReadyToTransition && !nothingUnsent && !transitionPersisted)
            {
                transitionPersisted = true;
                Debug.LogWarning("DatabaseSaver: the unit transition goes ahead with a save still unsent; it is kept on this device and sent by the next unit's build.");
                PersistLocal(newestSave);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        // The player id for requests: the Soap variable AuthManager sets, or on
        // WebGL the id the host page gave MHSBridge before the game started.
        // Only 24 lowercase hex characters are accepted (the service refuses
        // anything else).
        private string ResolveUserId()
        {
            string id = playerId != null ? playerId.Value : null;
            if (GameSaveManager.IsValidUserId(id)) return id;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (MHSBridge.Instance != null)
            {
                string bridgeId = MHSBridge.Instance.GetUserID();
                if (GameSaveManager.IsValidUserId(bridgeId)) return bridgeId;
            }
#endif
            return null;
        }

        private static int ReadCheckpoint(SavedGameData data)
        {
            if (data == null) return -1;
            foreach (var kv in data.Dict)
            {
                if (kv.Key == null || !kv.Key.EndsWith("CheckpointSaver") || kv.Value == null || string.IsNullOrEmpty(kv.Value.data)) continue;
                try
                {
                    var cp = JsonUtility.FromJson<CheckpointSaver.CheckpointData>(kv.Value.data);
                    if (cp != null) return cp.id;
                }
                catch (Exception) { }
            }
            return -1;
        }

        private static SavedGameData ParseSave(string saveJson)
        {
            if (string.IsNullOrEmpty(saveJson)) return null;
            try
            {
                return JsonUtility.FromJson<SavedGameData>(saveJson);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DatabaseSaver: a save on this device could not be read ({e.Message}).");
                return null;
            }
        }

        // True when a is later than b (both ISO 8601) by more than the tolerance.
        // Unparsable values are never "newer".
        private static bool IsNewer(string a, string b, float toleranceSeconds)
        {
            if (string.IsNullOrEmpty(a)) return false;
            if (string.IsNullOrEmpty(b)) return true;
            if (!DateTime.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ta)) return false;
            if (!DateTime.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var tb)) return true;
            return (ta.ToUniversalTime() - tb.ToUniversalTime()).TotalSeconds > toleranceSeconds;
        }

        // The service's save is newer than a time from this device's clock. When
        // the service's time is its own clock (a save without mhs_meta) a clock
        // skew allowance applies, so a device a minute behind does not throw its
        // own newer save away.
        private static bool ServerNewerThan(ServerLatest server, string deviceTime)
        {
            if (server == null || !server.hasSave) return false;
            return IsNewer(server.time, deviceTime, server.timeFromDevice ? 0f : ClockSkewAllowanceSeconds);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        // ---- Status display ----

        private void SetStatus(string text)
        {
            StatusText = text ?? "";
            if (statusHideRoutine != null)
            {
                StopCoroutine(statusHideRoutine);
                statusHideRoutine = null;
            }
            if (_saveNotificationText == null) return;
            if (!statusShown)
            {
                statusShown = true;
                saveNotificationFadeInEvent?.Raise();
            }
            _saveNotificationText.text = StatusText;
        }

        private void ShowStatus(string text)
        {
            SetStatus(text);
        }

        private void ShowToast(string text)
        {
            SetStatus(text);
            if (isActiveAndEnabled) statusHideRoutine = StartCoroutine(HideStatusAfter(ToastSeconds));
        }

        private IEnumerator HideStatusAfter(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            statusHideRoutine = null;
            HideStatus();
        }

        // Hides the line unless a toast is showing; the toast ends on its own.
        private void HideStatusUnlessToast()
        {
            if (statusHideRoutine == null) HideStatus();
        }

        private void HideStatus()
        {
            StatusText = "";
            if (!statusShown) return;
            statusShown = false;
            saveNotificationFadeOutEvent?.Raise();
            if (_saveNotificationText != null) _saveNotificationText.text = "";
        }

        // Who owns the status line while the fetch is working. In order: a player
        // waiting at START GAME; a save of this launch that is unsent (the send
        // loop's own lines); the game playing from the device copy while the
        // service is still being asked; a recovered save waiting for the answer.
        // A toast is never cut short.
        private enum FetchLineKind { None, Start, DeviceCopy, Recovered }

        private FetchLineKind FetchLineOwner()
        {
            if (fetchVisible && startRunning && !resumedLocally) return FetchLineKind.Start;
            if (statusHideRoutine != null) return FetchLineKind.None;
            if (newestSave != null && newestSave.Unsent && !newestRecovered) return FetchLineKind.None;
            if (resumedLocally && startRunning) return FetchLineKind.DeviceCopy;
            if (recoveredWaiting) return FetchLineKind.Recovered;
            return FetchLineKind.None;
        }

        private bool FetchLineVisible() => FetchLineOwner() != FetchLineKind.None;

        private string FetchLineWaiting(string what)
        {
            switch (FetchLineOwner())
            {
                case FetchLineKind.Start:
                    return string.IsNullOrEmpty(what) ? "Loading your progress..." : "Loading your progress... " + what;
                case FetchLineKind.DeviceCopy:
                    return "Using the progress saved on this device.\nStill checking the save service...";
                default:
                    return string.IsNullOrEmpty(what) ? "Saving an earlier checkpoint..." : "Saving an earlier checkpoint... " + what;
            }
        }

        private string FetchLineFailed(string reason, int remaining, int attempt)
        {
            switch (FetchLineOwner())
            {
                case FetchLineKind.Start:
                    return $"Can't load your progress yet: {reason}.\nRetrying in {remaining} s (attempt {attempt})";
                case FetchLineKind.DeviceCopy:
                    return $"Using the progress saved on this device.\nStill checking the save service... {remaining} s (attempt {attempt})";
                default:
                    return $"Saving an earlier checkpoint: {reason}.\nRetrying in {remaining} s (attempt {attempt})";
            }
        }

        // Waits the given time in real seconds, updating the status line once a
        // second with the remaining count while keepShowing (if given) is true;
        // ends early when stopWhen (if given) becomes true.
        private IEnumerator Countdown(float seconds, Func<int, string> text, Func<bool> keepShowing = null, Func<bool> stopWhen = null)
        {
            float end = Time.unscaledTime + seconds;
            while (true)
            {
                if (stopWhen != null && stopWhen()) break;
                int remaining = Mathf.CeilToInt(end - Time.unscaledTime);
                if (remaining <= 0) break;
                if (keepShowing == null || keepShowing()) SetStatus(text(remaining));
                yield return new WaitForSecondsRealtime(Mathf.Min(1f, end - Time.unscaledTime));
            }
        }

        private bool DebugLogsEnabled => enableDebugLogs != null && enableDebugLogs.Value;

        private void Print(string message, ConsoleLogManager.ConsoleLogType type = ConsoleLogManager.ConsoleLogType.Standard)
        {
            var console = ConsoleLogManager.Instance;
            if (console != null) console.PrintToConsole("DatabaseSaver: " + message, DebugLogsEnabled, type);
        }

        #region Addressable Functions

        public void LoadAddressableScriptableObjects()
        {
            if (_usingAddressables == false)
            {
                return;
            }

            ConsoleLogManager.Instance?.PrintToConsole("Loading AutoSave, OnSaveGame and OnLoadGame SOAP Assets", enableDebugLogs.Value);
            StartCoroutine(AddressableHelpers.LoadAddressableAsset<BoolVariable>(
                autoSaveAssetReference,
                result =>
                {
                    ConsoleLogManager.Instance?.PrintToConsole($"{result.name} was loaded with ID {result.GetInstanceID()}", enableDebugLogs.Value);
                    autoSave = result;
                    autoSave.Value = true;
                }));

            StartCoroutine(AddressableHelpers.LoadAddressableAsset<ScriptableEventInt>(
        onSaveGameAssetReference,
        result =>
        {
            ConsoleLogManager.Instance?.PrintToConsole($"{result.name} was loaded with ID {result.GetInstanceID()}", enableDebugLogs.Value);
            OnSaveGame = result;
            OnSaveGame.OnRaised += SaveGameToDatabase;
        }));

            StartCoroutine(AddressableHelpers.LoadAddressableAsset<ScriptableEventInt>(
            onLoadGameAssetReference,
            result =>
            {
                ConsoleLogManager.Instance?.PrintToConsole($"{result.name} was loaded with ID {result.GetInstanceID()}", enableDebugLogs.Value);
                OnLoadGameFromSlot = result;
                OnLoadGameFromSlot.OnRaised += LoadGameFromDatabase;
            }));
        }

        #endregion
    }
}
