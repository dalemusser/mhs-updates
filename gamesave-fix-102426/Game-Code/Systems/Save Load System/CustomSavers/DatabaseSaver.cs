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
        // WebGL PlayerPrefs is capped at 1 MB for the whole site; a save is
        // ~45 KB today. Anything over this stays in memory only.
        private const int LocalSaveMaxChars = 256 * 1024;
        private const float SaveRetryInitial = 5f;
        private const float SaveRetryMax = 60f;
        private const float LoadRetryInitial = 3f;
        private const float LoadRetryMax = 30f;
        private const float IdentityPollInterval = 1f;
        private const float ConfigPollInterval = 5f;
        private const float TransitionMaxWaitSeconds = 5f;
        private const float ToastSeconds = 2f;
        private const int LocalRecordFormat = 1;

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
        }
        private ServerLatest serverLatest;      // null until the service has answered this launch
        private bool fetchRunning;
        private bool fetchVisible;              // a player is waiting on it: show the status
        private int fetchFailures;              // failed attempts in a row this launch
        private GameSaveManager.LoadResult lastFetchFailure;
        private bool startRunning;
        private bool resumedLocally;            // START GAME is playing from this device's copy while the service is asked
        private bool resumeInProgress;          // a scene load for a resume has been requested and not finished

        // Unit transition.
        private bool transitionPending;
        private float transitionRequestedAt;
        private int lastTransitionCallFrame = -1000;

        // Status display (the Alert Canvas on this prefab).
        private bool statusShown;
        private Coroutine statusHideRoutine;
        public string StatusText { get; private set; } = "";

        public bool HasUnsentSave => newestSave != null && newestSave.Unsent;
        public int SaveAttempt => saveAttempt;
        public bool ServerAnswered => serverLatest != null;
        public int FetchFailures => fetchFailures;
        public bool StartRunning => startRunning;

        private void Start()
        {
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
            Assert.IsNotNull(slotNumber, $"SOAP Variable {slotNumber} cannot be null");
            Assert.IsNotNull(OnSaveGame, $"SOAP Variable {OnSaveGame} cannot be null");
            Assert.IsNotNull(OnLoadGameFromSlot, $"SOAP Variable {OnLoadGameFromSlot} cannot be null");
            Assert.IsNotNull(autoSave, $"SOAP Variable {autoSave} cannot be null");
            Assert.IsNotNull(playerId, $"SOAP Variable {playerId} cannot be null");
            Assert.IsNotNull(enableDebugLogs, $"SOAP Variable {enableDebugLogs} cannot be null");

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

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            // A new scene means the transition that was waiting on us is over.
            transitionPending = false;
            resumeInProgress = false;
        }

        public void InitializeDataAfterSceneLoaded(Scene scene)
        {
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
            if (!autoSave) return;
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

                    if (newestRecovered)
                    {
                        // A save left over from an earlier launch. Before it goes out,
                        // make sure the service does not already hold something newer
                        // (made on another device meanwhile); a late send must not
                        // roll progress back.
                        if (serverLatest == null)
                        {
                            EnsureFetch(false);
                            SetStatus("Saving an earlier checkpoint... checking the server");
                            yield return new WaitForSecondsRealtime(IdentityPollInterval);
                            continue;
                        }
                        if (serverLatest.hasSave && serverLatest.meta != null && IsNewer(serverLatest.meta.saved_at, item.saved_at))
                        {
                            Debug.LogWarning($"DatabaseSaver: the save service already holds newer progress ({serverLatest.meta.saved_at}) than the unsent save from {item.saved_at}; the unsent save is dropped.");
                            item.dropped = true;
                            PersistLocal(item);
                            RemovePendingKey(uid);
                            FinishItem(item, "");
                            continue;
                        }
                    }

                    if (saveManager == null) saveManager = GetComponent<GameSaveManager>();
                    if (saveManager == null)
                    {
                        Debug.LogError("DatabaseSaver: no GameSaveManager on this object; saves cannot be sent.");
                        yield return new WaitForSecondsRealtime(ConfigPollInterval);
                        continue;
                    }
                    if (saveManager.SaveConfig == null)
                    {
                        if (!configWarned)
                        {
                            configWarned = true;
                            Debug.LogWarning("DatabaseSaver: no save endpoint configured (MHSBridge has no state_save config); holding the save until there is one.");
                        }
                        SetStatus("Saving... waiting for the save service settings");
                        yield return new WaitForSecondsRealtime(ConfigPollInterval);
                        continue;
                    }

                    saveAttempt++;
                    if (saveAttempt == 1) SetStatus("Saving Game...");
                    else SetStatus($"Saving Game... (attempt {saveAttempt})");

                    GameSaveManager.RequestResult result = null;
                    saveManager.SaveJson(uid, GameId, item.save_json, r => result = r);
                    while (result == null) yield return null;

                    if (result.Accepted)
                    {
                        item.sent = true;
                        PersistLocal(item);
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
                        PersistLocal(item);
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
                    yield return Countdown(wait, remaining => $"Not saved yet: {result.reason}.\nRetrying in {remaining} s (attempt {saveAttempt + 1})");
                }
            }
            finally
            {
                sendLoopRunning = false;
            }
        }

        // The item's story ends (accepted or dropped). If it is still the
        // newest save, the save flag comes down and the player sees the toast.
        private void FinishItem(LocalSaveRecord item, string toast)
        {
            if (newestSave == item)
            {
                saveGameInProgress.Value = false;
                if (!string.IsNullOrEmpty(toast)) ShowToast(toast);
                else HideStatus();
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
            };
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
            while (serverLatest == null && !(fetchFailures > 0 && LocalCopyFor(ResolveUserId()) != null))
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }
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
                EnsureFetch(true);

                bool usedLocal = false;
                resumedLocally = false;
                LocalSaveRecord local = null;
                while (serverLatest == null)
                {
                    string uid = ResolveUserId();
                    if (uid != null && local == null) local = LocalCopyFor(uid);
                    if (!usedLocal && local != null && fetchFailures > 0)
                    {
                        // The service is not answering and this device has this
                        // player's last save: play on from it, keep asking.
                        var localData = ParseSave(local.save_json);
                        if (localData != null)
                        {
                            usedLocal = true;
                            resumedLocally = true;
                            Debug.LogWarning($"DatabaseSaver: the save service is not answering ({lastFetchFailure?.reason}); resuming from the copy on this device ({local.scene}, checkpoint {local.checkpoint}, {local.saved_at}{(local.Unsent ? ", not yet sent" : "")}) and still checking the server.");
                            ShowStatus("Using the progress saved on this device.\nStill checking the save service...");
                            ResumeWith(localData);
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.25f);
                }

                // The service has answered.
                if (local == null) local = LocalCopyFor(ResolveUserId());
                var chosen = ChooseResume(local, serverLatest);

                if (usedLocal)
                {
                    bool serverNewer = serverLatest.hasSave && local != null && serverLatest.meta != null && IsNewer(serverLatest.meta.saved_at, local.saved_at);
                    if (serverNewer && chosen == serverLatest.data)
                    {
                        Debug.LogWarning("DatabaseSaver: the save service holds newer progress than this device's copy; loading it.");
                        ShowStatus("Newer progress found on the save service.\nLoading it...");
                        // Let a scene load that is under way finish first.
                        while (resumeInProgress || SceneManager.GetActiveScene().name == "Transition") yield return null;
                        ResumeWith(chosen);
                        ShowToast("Progress loaded");
                    }
                    else
                    {
                        HideStatus();
                    }
                    yield break;
                }

                if (chosen != null)
                {
                    HideStatus();
                    ResumeWith(chosen);
                    yield break;
                }

                // The service says there are no saves (and this device has no
                // unsent one): a new game.
                HideStatus();
                Print("No saves found; starting a new game.");
#if UNITY_EDITOR
                startNewGameIfNoExistingSaveData = FindFirstObjectByType<AuthManager>()?.playerOverrideId != "";
#endif
                if (startNewGameIfNoExistingSaveData)
                {
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
            }
            finally
            {
                startRunning = false;
                resumedLocally = false;
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
                // stale then (the saves were deleted on purpose); an unsent one is
                // progress the service never got, and it stays.
                if (local != null && local.Unsent) return ParseSave(local.save_json);
                return null;
            }
            if (local == null) return server.data;
            bool serverHasTime = server.meta != null && server.meta.HasTime;
            if (local.Unsent)
            {
                // The pending save is the newest this device knows; the service's
                // copy wins only when it is demonstrably newer (another device).
                if (serverHasTime && IsNewer(server.meta.saved_at, local.saved_at)) return server.data;
                return ParseSave(local.save_json) ?? server.data;
            }
            if (serverHasTime && IsNewer(local.saved_at, server.meta.saved_at))
            {
                // This device's accepted save is newer than what the service
                // returned (the service lost it, or returned an older one).
                return ParseSave(local.save_json) ?? server.data;
            }
            return server.data;
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
                while (serverLatest == null)
                {
                    string uid = ResolveUserId();
                    if (uid == null)
                    {
                        if (!identityWarned)
                        {
                            identityWarned = true;
                            Debug.LogWarning("DatabaseSaver: no valid player id yet (24 lowercase hex characters); the load waits for it.");
                        }
                        if (fetchVisible) SetStatus("Loading your progress... waiting for your sign-in");
                        yield return new WaitForSecondsRealtime(IdentityPollInterval);
                        continue;
                    }
                    if (saveManager == null) saveManager = GetComponent<GameSaveManager>();
                    if (saveManager == null || saveManager.LoadConfig == null)
                    {
                        if (!configWarned)
                        {
                            configWarned = true;
                            Debug.LogWarning("DatabaseSaver: no load endpoint configured (MHSBridge has no state_load config); waiting for it.");
                        }
                        if (fetchVisible) SetStatus("Loading your progress... waiting for the save service settings");
                        yield return new WaitForSecondsRealtime(ConfigPollInterval);
                        continue;
                    }

                    attempt++;
                    GameSaveManager.LoadResult result = null;
                    saveManager.LoadLatest(uid, GameId, 1, r => result = r);
                    while (result == null) yield return null;

                    if (result.outcome != GameSaveManager.Outcome.Failed)
                    {
                        var newest = result.Newest;
                        serverLatest = new ServerLatest
                        {
                            hasSave = newest != null && newest.save_data != null,
                            data = newest?.save_data,
                            meta = result.NewestMeta,
                        };
                        fetchFailures = 0;
                        Print(serverLatest.hasSave ? $"Server save received: {serverLatest.data.sceneName} ({serverLatest.meta?.saved_at ?? "no meta"})" : "Server answered: no saves.");
                        break;
                    }

                    fetchFailures++;
                    lastFetchFailure = result;
                    float wait = result.retryAfterSeconds > 0 ? result.retryAfterSeconds : delay;
                    delay = Mathf.Min(delay * 2f, LoadRetryMax);
                    Debug.LogWarning($"DatabaseSaver: load not answered ({result.statusCode} {result.error}; {result.reason}); attempt {attempt}, retrying in {wait:F0} s.");
                    // The countdown is shown while a player waits at START GAME and
                    // the game has not resumed from the device copy meanwhile.
                    yield return Countdown(wait, remaining => $"Can't load your progress yet: {result.reason}.\nRetrying in {remaining} s (attempt {attempt + 1})",
                        () => fetchVisible && startRunning && !resumedLocally);
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
            if (localReadForUser == uid) return localCopy;
            localReadForUser = uid;
            localCopy = null;

            var slot = ReadRecord(LocalSaveKey);
            var pending = ReadRecord(PendingSaveKeyPrefix + uid);
            LocalSaveRecord mine = slot != null && slot.user_id == uid ? slot : null;
            if (pending != null && pending.user_id == uid)
            {
                if (mine == null || IsNewer(pending.saved_at, mine.saved_at) || (!mine.Unsent && pending.Unsent)) mine = pending;
            }
            localCopy = mine;
            if (mine == null) return null;

            Print($"Device copy for this player: {mine.scene}, checkpoint {mine.checkpoint}, {mine.saved_at}, {(mine.Unsent ? "NOT sent" : mine.dropped ? "dropped" : "sent")}.");
            if (mine.Unsent && (newestSave == null || IsNewer(mine.saved_at, newestSave.saved_at)))
            {
                // Progress the service never got: send it (after the server check).
                Debug.LogWarning($"DatabaseSaver: an unsent save from an earlier launch was found on this device ({mine.scene}, checkpoint {mine.checkpoint}, {mine.saved_at}); it will be sent once the service is reachable.");
                newestSave = mine;
                newestRecovered = true;
                saveGameInProgress.Value = true;
                EnsureSendLoop();
            }
            return mine;
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
            try
            {
                string json = JsonUtility.ToJson(record);
                if (json.Length > LocalSaveMaxChars)
                {
                    if (!localWriteWarned)
                    {
                        localWriteWarned = true;
                        Debug.LogWarning($"DatabaseSaver: the save is too large to keep on the device ({json.Length} chars); it stays in memory only for this launch.");
                    }
                    return;
                }
                if (!slotIsOurs)
                {
                    var slot = ReadRecord(LocalSaveKey);
                    if (slot != null && slot.Unsent && !string.IsNullOrEmpty(slot.user_id) && slot.user_id != record.user_id)
                    {
                        PlayerPrefs.SetString(PendingSaveKeyPrefix + slot.user_id, JsonUtility.ToJson(slot));
                    }
                }
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

        private static void RemovePendingKey(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            try
            {
                if (PlayerPrefs.HasKey(PendingSaveKeyPrefix + uid))
                {
                    PlayerPrefs.DeleteKey(PendingSaveKeyPrefix + uid);
                    PlayerPrefs.Save();
                }
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
                if (uid != null) PlayerPrefs.DeleteKey(PendingSaveKeyPrefix + uid);
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
            float now = Time.unscaledTime;
            // The loader polls every frame; a gap of more than one frame since the
            // last call means a new transition is asking.
            if (!transitionPending || Time.frameCount - lastTransitionCallFrame > 1)
            {
                transitionPending = true;
                transitionRequestedAt = now;
            }
            lastTransitionCallFrame = Time.frameCount;

            bool nothingUnsent = newestSave == null || !newestSave.Unsent;
            bool timedOut = now - transitionRequestedAt >= TransitionMaxWaitSeconds;
            ReadyToTransition = nothingUnsent || timedOut;
            if (ReadyToTransition && !nothingUnsent)
            {
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

        // True when a is a later moment than b (both ISO 8601). Unparsable
        // values are never "newer".
        private static bool IsNewer(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return false;
            if (string.IsNullOrEmpty(b)) return true;
            if (!DateTime.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ta)) return false;
            if (!DateTime.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var tb)) return true;
            return ta.ToUniversalTime() > tb.ToUniversalTime();
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

        private void HideStatus()
        {
            StatusText = "";
            if (!statusShown) return;
            statusShown = false;
            saveNotificationFadeOutEvent?.Raise();
            if (_saveNotificationText != null) _saveNotificationText.text = "";
        }

        // Waits the given time in real seconds, updating the status line once a
        // second with the remaining count while keepShowing (if given) is true.
        private IEnumerator Countdown(float seconds, Func<int, string> text, Func<bool> keepShowing = null)
        {
            float end = Time.unscaledTime + seconds;
            while (true)
            {
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
