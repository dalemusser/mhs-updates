using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System.Text;
using System;
using System.IO;
using Obvious.Soap;
using Newtonsoft.Json;
using MHS;

// Game event logger: queues events, persists the unsent queue, and sends
// it to the log service in batches.
//
// Rules this file keeps (mhs-updates/gamelogger-fix-093026, 2026-09-30):
//  - Every queued entry owns its data. LogEvent copies the caller's
//    dictionary, so a component that reuses a dictionary cannot change an
//    entry that is still waiting to be sent.
//  - Per-send state lives in the send coroutine, never in a field. A field
//    that carried over between passes (toSend) made the 2026-09 builds
//    resend every accepted batch and loop on a refused one.
//  - Entries are removed from the queue by identity, only after the server
//    has accepted them or has said it never will (400 on a single entry, 413
//    on a single entry).
//  - An entry with no fields, no eventType or no user id is never sent: it is
//    dropped when the cache is loaded and again before a batch is built. The
//    one exception is an entry logged before identity is known: it waits, and
//    SetUserId stamps the id onto it.
//  - One instance: Awake assigns the singleton and a second component (the
//    gameplay core-systems prefab carries one too) removes only itself.
//  - The log service is never flooded: a refused request is followed by a
//    pause, network and server failures back off from 10 s to 60 s, and a
//    request times out after 30 s.
//  - Sending never blocks a unit transition for more than a few seconds; the
//    queue is persisted and the next unit's build sends it.
public class GameLogger : MonoBehaviour, ICheckForUnitTransition
{
    [SerializeField] private BoolVariable enableDebugLogs;

    private const string logCacheFile = "game_logs_cache.json";

    // ---- Cache limits (mhs-updates/gamelogger-cache-overflow-091626) ----
    // Unity caps WebGL PlayerPrefs at 1 MB for the whole store, all keys
    // combined. The serialized queue has to stay well under that, leaving
    // room for anything else the game keeps in PlayerPrefs. When the queue
    // is over budget the OLDEST entries are dropped first.
    private const int maxCacheBytes = 512 * 1024;
    // Hard cap on queued entries regardless of size, so the cost of
    // serializing the queue on each save stays bounded.
    private const int maxCacheEntries = 1500;
    // Cache writes are coalesced: at most one PlayerPrefs write per interval
    // (plus an immediate flush on pause / focus loss / quit / unit change).
    private const float cacheSaveInterval = 2f;
    // Don't repeat the "dropping entries" warning more often than this.
    private const float dropWarningInterval = 60f;

    // ---- Send behaviour ----
    // stratalog accepts up to max_batch_size entries per request (default 100).
    private const int maxBatchEntries = 50;
    private const float retryDelayInitial = 10f;
    private const float retryDelayMax = 60f;
    private const float offlinePollInterval = 5f;
    // Pause after a refused request (400 or 413) before the next one, so a
    // refusal can never turn into a tight loop.
    private const float rejectedPauseSeconds = 1f;
    // How often to look again when the head of the queue is waiting for the
    // user id (SetUserId has not been called yet).
    private const float identityPollInterval = 1f;
    // A request that gets no answer in this time is a failure (backoff), not
    // a hang.
    private const int requestTimeoutSeconds = 30;
    // The longest a unit transition waits for in-flight sends.
    private const float transitionMaxWaitSeconds = 5f;

    private static GameLogger _instance;
    private readonly Queue<Dictionary<string, object>> logQueue = new();
    private static readonly object queueLock = new object(); // Protect logQueue with a lock
    private Dictionary<string, object> cachedDeviceInfo;
    private static string userId;
    private bool isDuplicate = false;

    #region  Properties
    public static bool IsSendingLogs => _instance != null && _instance.isSendingLogs;

    // Send state.
    private bool isSendingLogs = false;
    private int batchSize = maxBatchEntries;
    private int singleSendRemaining = 0; // > 0: send one entry per request (isolating a rejected entry)
    private bool endpointWarningShown = false;

    // Cache write state.
    private bool cacheDirty = false;
    private float lastCacheSaveTime = -1000f;
    private float lastDropWarningTime = -1000f;
    private bool cacheWriteFailureReported = false;
    private int droppedSinceLastReport = 0; // trimmed entries not yet reported to the server
    private long droppedTotal = 0;

    // Unit transition state.
    private bool transitionPending = false;
    private float transitionRequestedAt = 0f;

    public bool ReadyToTransition{get; set;}
    private readonly JsonSerializerSettings _settings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
    };
    #endregion

    public static GameLogger Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<GameLogger>(); // Unity 2023+ compatible
                if (_instance == null)
                {
                    Debug.LogWarning("GameLogger: no instance in the scene; creating one (its debug-log setting is unassigned).");
                    GameObject loggerObject = new GameObject("GameLogger");
                    _instance = loggerObject.AddComponent<GameLogger>();
                    DontDestroyOnLoad(loggerObject.gameObject);
                }
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            // A second logger (the gameplay core-systems prefab carries one as
            // well as the main-menu one). Keep the first; remove only this
            // component, because the game object is shared with other
            // singletons that handle their own duplicates.
            isDuplicate = true;
            Destroy(this);
            return;
        }
        _instance = this;

        // Cache device info once at startup
        cachedDeviceInfo = BuildDeviceInfo();
        if (transform.parent != null) transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        LoadCachedLogs();
    }

    private void Start()
    {
        if (isDuplicate) return;

        // Only start sending logs if there are cached logs
        lock (queueLock)
        {
            if (logQueue.Count > 0 && !isSendingLogs)
            {
                StartCoroutine(SendQueuedLogs());
            }
        }
    }

    private void Update()
    {
        if (isDuplicate) return;

        // Coalesced cache write: events that arrived inside the interval are
        // written together here instead of one PlayerPrefs write per event.
        if (cacheDirty && Time.unscaledTime - lastCacheSaveTime >= cacheSaveInterval)
        {
            lock (queueLock)
            {
                SaveCachedLogs();
            }
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused) FlushCache();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) FlushCache();
    }

    private void OnApplicationQuit()
    {
        FlushCache();
    }

    private void OnDestroy()
    {
        if (isDuplicate) return;
        FlushCache();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        if (_instance == this) _instance = null;
    }

    private void OnActiveSceneChanged(Scene previous, Scene next)
    {
        // A new scene means the transition that was waiting on us is over.
        transitionPending = false;
    }

    private void FlushCache()
    {
        if (isDuplicate) return;
        lock (queueLock)
        {
            if (cacheDirty) SaveCachedLogs();
        }
    }

    // Queues an entry built by LogEvent. The queue owns the dictionary from
    // here on; callers must not modify it afterwards (LogEvent copies the
    // caller's data for that reason).
    public void SendToServer(Dictionary<string, object> logData)
    {
        if (logData == null) return;

        lock (queueLock)
        {
            logQueue.Enqueue(logData);
            MarkCacheDirty();

            if (!isSendingLogs)
            {
                StartCoroutine(SendQueuedLogs());
            }
        }
    }

    // Call with queueLock held. The first change after a quiet period is
    // written immediately; further changes inside cacheSaveInterval are
    // coalesced and written by Update().
    private void MarkCacheDirty()
    {
        cacheDirty = true;
        if (Time.unscaledTime - lastCacheSaveTime >= cacheSaveInterval)
        {
            SaveCachedLogs();
        }
    }

    public void LogEvent(string eventType, Dictionary<string, object> data, string eventKey = null)
    {
        // The "user_id" key here is the contract with stratalog. The server
        // rejects any payload using "playerId", "player_id", "login_id", or
        // "email", and requires a 24-char lowercase hex value.
        //
        // The entry gets its own copy of the data. Several logging components
        // reuse one dictionary for every event they log; without the copy a
        // queued entry would show the details of a later event.
        Dictionary<string, object> json = new()
        {
            { "game", "mhs" },
            { "user_id", userId },
            { "version", Application.version },
            { "sceneName", SceneManager.GetActiveScene().name },
            { "timestamp", DateTime.UtcNow.ToString("o") },
            { "eventType", eventType },
            { "data", CopyData(data) }
        };

        if (eventKey != null)
            json["eventKey"] = eventKey;

        if (data != null && data.TryGetValue("timestamp", out var value))
        {
            json["timestamp"] = value;
        }

        SendToServer(json);
    }

    // A copy of the event data, including nested dictionaries and lists, so
    // nothing the caller keeps a reference to is shared with the queue.
    private static Dictionary<string, object> CopyData(Dictionary<string, object> data)
    {
        var copy = new Dictionary<string, object>(data != null ? data.Count : 0);
        if (data == null) return copy;
        foreach (var kv in data)
        {
            copy[kv.Key] = CopyValue(kv.Value);
        }
        return copy;
    }

    private static object CopyValue(object value)
    {
        switch (value)
        {
            case Dictionary<string, object> dict:
                return CopyData(dict);
            case List<object> list:
                {
                    var copy = new List<object>(list.Count);
                    foreach (var item in list) copy.Add(CopyValue(item));
                    return copy;
                }
            default:
                return value; // strings and boxed value types are immutable
        }
    }

    // All 8 wrapper functions...

    // 1. DialogueNodeEvent
    public void LogDialogueNodeEvent(int conversationId, int nodeId)
    {
        var data = new Dictionary<string, object>
        {
            { "conversationId", conversationId },
            { "nodeId", nodeId }
        };

        string eventKey = $"DialogueNodeEvent:{conversationId}:{nodeId}";
        LogEvent("DialogueNodeEvent", data, eventKey);
    }

    // 2. DialogueEvent
    public void LogDialogueEvent(string dialogueEventType, int conversationId)
    {
        var data = new Dictionary<string, object>
        {
            { "dialogueEventType", dialogueEventType },
            { "conversationId", conversationId }
        };

        LogEvent("DialogueEvent", data);
    }

    // 3. Topographic Map Event
    public void LogTopoMapEvent(string featureUsed, string actionType, Vector3 location = new())
    {
        var data = new Dictionary<string, object>
        {
            { "featureUsed", featureUsed },
            { "actionType", actionType }
        };

        if (location != null)
        {
            data["location"] = new Dictionary<string, object>
            {
                { "x", SafeFloat(location.x) },
                { "y", SafeFloat(location.y) },
                { "z", SafeFloat(location.z) }
            };
        }

        LogEvent("TopographicMapEvent", data);
    }

    // 4. ArgumentationEvent
    public void LogArgumentationEvent(string eventType, string title, string description)
    {
        var data = new Dictionary<string, object>
        {
            { "eventType", eventType },
            { "title", title },
            { "description", description }
        };

        LogEvent("ArgumentationEvent", data);
    }

    // 5. ArgumentationNodeEvent
    public void LogArgumentationNodeEvent(string actionType, string title, string nodeName)
    {
        var data = new Dictionary<string, object>
        {
            { "actionType", actionType },
            { "title", title },
            { "nodeName", nodeName }
        };

        LogEvent("ArgumentationNodeEvent", data);
    }

    // 6. ArgumentationToolEvent
    public void LogArgumentationToolEvent(string actionType, string title, string toolName, string toolState)
    {
        var data = new Dictionary<string, object>
        {
            { "actionType", actionType },
            { "title", title },
            { "tool", new Dictionary<string, object>
                {
                    { "name", toolName },
                    { "state", toolState }
                }
            }
        };

        LogEvent("ArgumentationToolEvent", data);
    }

    // 7. PlayerPositionEvent
    // New dictionaries on every call: the previous version reused two fields,
    // so every queued position event showed the latest position.
    public void LogPlayerPositionEvent(Vector3 position)
    {
        var data = new Dictionary<string, object>
        {
            { "position", new Dictionary<string, object>
                {
                    { "x", SafeFloat(position.x) },
                    { "y", SafeFloat(position.y) },
                    { "z", SafeFloat(position.z) }
                }
            }
        };

        LogEvent("PlayerPositionEvent", data);
    }

    // 8. QuestEvent
    public void LogQuestEvent(string questEventType, int questId, string questName, string questSuccessOrFailure = null)
    {
        var data = new Dictionary<string, object>
        {
            { "questEventType", questEventType },
            { "questId", questId },
            { "questName", questName }
        };

        if (questSuccessOrFailure != null)
        {
            data["questSF"] = questSuccessOrFailure;
        }

        string eventKey = $"{questEventType}:{questId}";
        LogEvent("QuestEvent", data, eventKey);
    }

    // The send loop. Everything it works with is local to one pass: the
    // entries taken from the head of the queue, the request body and the
    // request. Nothing about a pass survives into the next one except the
    // retry delay and the single-send counter.
    private IEnumerator SendQueuedLogs()
    {
        isSendingLogs = true;
        try
        {
            float retryDelay = retryDelayInitial;

            while (true)
            {
                // Take the oldest entries without removing them. They are only
                // removed once the server has accepted them (or has told us it
                // never will). A new list on every pass.
                List<Dictionary<string, object>> toSend = new();
                bool waitForIdentity = false;

                lock (queueLock)
                {
                    // Nothing unsendable ever goes out. Drop such entries at the
                    // head, except one that is only waiting for its user id.
                    while (logQueue.Count > 0 && !IsSendable(logQueue.Peek()))
                    {
                        if (userId == null && LacksOnlyUserId(logQueue.Peek()))
                        {
                            waitForIdentity = true;
                            break;
                        }
                        var bad = logQueue.Dequeue();
                        MarkCacheDirty();
                        Debug.LogWarning($"GameLogger: dropping an unsendable queued entry ({Describe(bad)}).");
                    }

                    if (!waitForIdentity)
                    {
                        int take = singleSendRemaining > 0 ? 1 : batchSize;
                        foreach (var entry in logQueue)
                        {
                            if (!IsSendable(entry)) break; // handled at the head on a later pass
                            toSend.Add(entry);
                            if (toSend.Count >= take) break;
                        }
                    }
                }

                if (waitForIdentity)
                {
                    yield return new WaitForSeconds(identityPollInterval);
                    continue;
                }

                if (toSend.Count == 0) break; // Exit loop if no logs are available

                // The endpoint comes from the host page (window.__mhsBridgeConfig)
                // through MHSBridge. Without it there is nowhere to send, so
                // wait for it; the queue is persisted meanwhile.
                MHSBridge.ServiceConfig logConfig = MHSBridge.Instance != null ? MHSBridge.Instance.GetLogSubmitConfig() : null;
                if (logConfig == null || string.IsNullOrEmpty(logConfig.url))
                {
                    if (!endpointWarningShown)
                    {
                        endpointWarningShown = true;
                        Debug.LogWarning("GameLogger: no log endpoint configured (MHSBridge has no log_submit config); holding queued entries until it is.");
                    }
                    yield return new WaitForSeconds(offlinePollInterval);
                    continue;
                }

                if (!IsNetworkAvailable())
                {
                    yield return new WaitForSeconds(offlinePollInterval);
                    continue;
                }

                string jsonData = BuildRequestBody(toSend);

                using (UnityWebRequest request = new UnityWebRequest(logConfig.url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    if (!string.IsNullOrEmpty(logConfig.auth))
                    {
                        request.SetRequestHeader("Authorization", logConfig.auth);
                    }
                    request.timeout = requestTimeoutSeconds;

                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        Print($"Log Sent ({toSend.Count}): {request.downloadHandler.text}");

                        RemoveSent(toSend);
                        if (singleSendRemaining > 0) singleSendRemaining--;
                        retryDelay = retryDelayInitial;
                        ReportDroppedEntriesIfAny();
                        continue;
                    }

                    long code = request.responseCode;
                    Print($"Log Post Failed: {request.error}; {code}", ConsoleLogManager.ConsoleLogType.Error);

                    if (code == 400)
                    {
                        // The server rejected the payload itself. Retrying can
                        // never succeed, so the entry has to go.
                        if (toSend.Count == 1)
                        {
                            Debug.LogWarning($"GameLogger: server rejected a log entry (400), dropping it: {request.downloadHandler.text}");
                            RemoveSent(toSend);
                            if (singleSendRemaining > 0) singleSendRemaining--;
                        }
                        else
                        {
                            // stratalog rejects a whole batch if any one entry is
                            // invalid. Resend this batch's entries one at a time so
                            // only the bad one is dropped.
                            singleSendRemaining = toSend.Count;
                        }
                        yield return new WaitForSeconds(rejectedPauseSeconds);
                        continue;
                    }

                    if (code == 413)
                    {
                        // Request body over the server's 1 MB limit.
                        if (toSend.Count > 1)
                        {
                            batchSize = Math.Max(1, toSend.Count / 2);
                        }
                        else
                        {
                            Debug.LogWarning("GameLogger: a single log entry exceeds the server's size limit (413), dropping it.");
                            RemoveSent(toSend);
                            if (singleSendRemaining > 0) singleSendRemaining--;
                        }
                        yield return new WaitForSeconds(rejectedPauseSeconds);
                        continue;
                    }

                    // Anything else (connection failure, timeout, 401/403, 5xx):
                    // keep the entries and retry with backoff. The cache limits
                    // above bound how much can pile up while this goes on.
                    yield return new WaitForSeconds(retryDelay);
                    retryDelay = Mathf.Min(retryDelay * 2f, retryDelayMax);
                }
            }
        }
        finally
        {
            isSendingLogs = false;
        }
    }

    // Single entry: the entry itself. Several entries: stratalog's batch
    // envelope, { "game": ..., "entries": [ ... ] }.
    private string BuildRequestBody(List<Dictionary<string, object>> entries)
    {
        if (entries.Count == 1)
        {
            return JsonConvert.SerializeObject(WithDevice(entries[0]), _settings);
        }

        var list = new List<Dictionary<string, object>>(entries.Count);
        foreach (var entry in entries)
        {
            list.Add(WithDevice(entry));
        }

        string game = "mhs";
        if (entries[0].TryGetValue("game", out var g) && g is string gs && !string.IsNullOrEmpty(gs))
        {
            game = gs;
        }

        var envelope = new Dictionary<string, object>
        {
            { "game", game },
            { "entries", list }
        };
        return JsonConvert.SerializeObject(envelope, _settings);
    }

    // Device info is attached to a shallow copy at send time. The queued
    // entry (and therefore the cached copy) stays small.
    private Dictionary<string, object> WithDevice(Dictionary<string, object> entry)
    {
        var copy = new Dictionary<string, object>(entry);
        copy["device"] = cachedDeviceInfo;
        return copy;
    }

    // Removes exactly the entries just sent, by identity: dequeue while the
    // head is one of them. Entries the cache trimming already dropped while
    // the request was in flight are simply not there any more; entries that
    // were not sent are never removed.
    private void RemoveSent(List<Dictionary<string, object>> sent)
    {
        lock (queueLock)
        {
            var sentSet = new HashSet<Dictionary<string, object>>(sent); // reference equality
            while (logQueue.Count > 0 && sentSet.Contains(logQueue.Peek()))
            {
                logQueue.Dequeue();
            }
            MarkCacheDirty();
        }
    }

    // An entry the server could accept: it has fields, an event type and a
    // user id.
    private static bool IsSendable(Dictionary<string, object> entry)
    {
        return entry != null
            && entry.Count > 0
            && entry.TryGetValue("eventType", out var t) && t is string ts && ts.Length > 0
            && HasUserId(entry);
    }

    private static bool HasUserId(Dictionary<string, object> entry)
    {
        return entry.TryGetValue("user_id", out var u) && u is string us && us.Length > 0;
    }

    // A real event that only lacks its user id (logged before SetUserId).
    private static bool LacksOnlyUserId(Dictionary<string, object> entry)
    {
        return entry != null
            && entry.Count > 0
            && entry.TryGetValue("eventType", out var t) && t is string ts && ts.Length > 0
            && !HasUserId(entry);
    }

    private static string Describe(Dictionary<string, object> entry)
    {
        if (entry == null) return "null";
        if (entry.Count == 0) return "empty";
        entry.TryGetValue("eventType", out var t);
        entry.TryGetValue("timestamp", out var ts);
        return $"{t ?? "no eventType"} {ts ?? ""}{(HasUserId(entry) ? "" : " without user_id")}";
    }

    // If cache trimming had to drop entries while the server was unreachable,
    // tell the server once the connection is back so the gap is visible in
    // the data rather than silent.
    private void ReportDroppedEntriesIfAny()
    {
        int dropped;
        lock (queueLock)
        {
            dropped = droppedSinceLastReport;
            droppedSinceLastReport = 0;
        }
        if (dropped <= 0) return;

        var data = new Dictionary<string, object>
        {
            { "dropped", dropped },
            { "droppedTotal", droppedTotal }
        };
        LogEvent("LogCacheOverflow", data);
    }

    private bool DebugLogsEnabled => enableDebugLogs != null && enableDebugLogs.Value;

    private void Print(string message, ConsoleLogManager.ConsoleLogType logType = ConsoleLogManager.ConsoleLogType.Standard)
    {
        var console = ConsoleLogManager.Instance;
        if (console != null)
        {
            console.PrintToConsole(message, DebugLogsEnabled, logType);
        }
    }

    private float SafeFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0f;
        return value;
    }

    private string GetPlatform()
    {
#if UNITY_WEBGL
        return "UnityWebGL";
#else
        return "Standalone";
#endif
    }

    private void SaveCachedLogs()
    {
        cacheDirty = false;
        lastCacheSaveTime = Time.unscaledTime;
        string json = SerializeQueueTrimmed();
#if UNITY_WEBGL
        SaveCachedLogsWebGL(json);
#else
        SaveCachedLogsStandalone(json);
#endif
    }

    private void LoadCachedLogs()
    {
#if UNITY_WEBGL
        LoadCachedLogsWebGL();
#else
        LoadCachedLogsStandalone();
#endif
    }

    private bool IsNetworkAvailable()
    {
#if UNITY_WEBGL
        // Advisory only: on WebGL this does not reliably report an unreachable
        // host. The send loop copes with failures either way.
        return Application.internetReachability != NetworkReachability.NotReachable;
#else
        return true; // Assume always online for non-WebGL platforms
#endif
    }

    // Serializes the queue as a JSON array of entries, dropping the oldest
    // entries until the result fits within maxCacheEntries and maxCacheBytes.
    // Call with queueLock held.
    private string SerializeQueueTrimmed()
    {
        int dropped = 0;

        while (logQueue.Count > maxCacheEntries)
        {
            logQueue.Dequeue();
            dropped++;
        }

        string json = JsonConvert.SerializeObject(new List<Dictionary<string, object>>(logQueue), _settings);
        int bytes = Encoding.UTF8.GetByteCount(json);

        while (bytes > maxCacheBytes && logQueue.Count > 1)
        {
            // Drop enough of the oldest entries to land under budget with
            // some headroom; this converges in one or two passes.
            int keep = (int)(logQueue.Count * ((double)maxCacheBytes / bytes) * 0.9);
            int drop = Math.Max(1, logQueue.Count - keep);
            for (int i = 0; i < drop && logQueue.Count > 1; i++)
            {
                logQueue.Dequeue();
                dropped++;
            }
            json = JsonConvert.SerializeObject(new List<Dictionary<string, object>>(logQueue), _settings);
            bytes = Encoding.UTF8.GetByteCount(json);
        }

        if (dropped > 0)
        {
            droppedSinceLastReport += dropped;
            droppedTotal += dropped;
            if (Time.unscaledTime - lastDropWarningTime >= dropWarningInterval)
            {
                lastDropWarningTime = Time.unscaledTime;
                Debug.LogWarning($"GameLogger: unsent log cache over budget; dropped {dropped} oldest entries ({droppedTotal} this session). Logs are not reaching the server. Check that the log endpoint is reachable.");
            }
        }

        return json;
    }

    private void SaveCachedLogsWebGL(string json)
    {
        try
        {
            PlayerPrefs.SetString(logCacheFile, json);
            PlayerPrefs.Save();
            cacheWriteFailureReported = false;
        }
        catch (Exception e)
        {
            // With the limits above this should not happen. If the browser
            // refuses the write anyway (private window, site data blocked,
            // storage quota), logging carries on in memory for this session.
            if (!cacheWriteFailureReported)
            {
                cacheWriteFailureReported = true;
                Debug.LogWarning($"GameLogger: could not persist the log cache ({Encoding.UTF8.GetByteCount(json)} bytes, {logQueue.Count} entries): {e.Message}. Continuing in memory only.");
            }
        }
    }

    private void SaveCachedLogsStandalone(string json)
    {
        string path = Path.Combine(Application.persistentDataPath, logCacheFile);
        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to save cached logs (Standalone): {e.Message}");
        }
    }

    private void LoadCachedLogsWebGL()
    {
        if (!PlayerPrefs.HasKey(logCacheFile)) return;

        try
        {
            string json = PlayerPrefs.GetString(logCacheFile);
            LoadEntries(ParseCache(json));
        }
        catch (Exception e)
        {
            // An unreadable cache would fail the same way on every launch,
            // so discard it rather than carry it forever.
            Debug.LogWarning($"Failed to load cached logs (WebGL): {e.Message}. Discarding the cache.");
            PlayerPrefs.DeleteKey(logCacheFile);
            PlayerPrefs.Save();
        }
    }

    private void LoadCachedLogsStandalone()
    {
        string path = Path.Combine(Application.persistentDataPath, logCacheFile);
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            LoadEntries(ParseCache(json));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to load cached logs (Standalone): {e.Message}. Discarding the cache.");
            try { File.Delete(path); } catch { }
        }
    }

    // Puts the cached entries in the queue, leaving out any that could never
    // be sent: an empty entry (what the 2026-09-14 build leaves behind after
    // a failed send), or one without an event type or a user id. If anything
    // was left out the cleaned queue is written back at once.
    private void LoadEntries(List<Dictionary<string, object>> entries)
    {
        int skipped = 0;
        lock (queueLock)
        {
            logQueue.Clear();
            foreach (var entry in entries)
            {
                if (IsSendable(entry))
                {
                    logQueue.Enqueue(entry);
                }
                else
                {
                    skipped++;
                }
            }
            if (skipped > 0)
            {
                Debug.LogWarning($"GameLogger: left {skipped} unsendable entries out of the loaded log cache (empty, or without an event type or user id); {logQueue.Count} kept.");
                MarkCacheDirty();
            }
        }
    }

    // Accepts both the current cache format (a JSON array of entries) and the
    // format written by builds before this change ({"logs":["<entry json>",
    // ...]}) so a cache left by an older build is sent, not lost, after an
    // upgrade.
    private static List<Dictionary<string, object>> ParseCache(string json)
    {
        var result = new List<Dictionary<string, object>>();
        if (string.IsNullOrEmpty(json)) return result;

        if (json.TrimStart().StartsWith("["))
        {
            var list = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(json);
            if (list != null)
            {
                foreach (var entry in list)
                {
                    if (entry != null) result.Add(entry);
                }
            }
            return result;
        }

        LogListWrapper wrapper = JsonUtility.FromJson<LogListWrapper>(json);
        if (wrapper?.logs != null)
        {
            foreach (string log in wrapper.logs)
            {
                Dictionary<string, object> entry = null;
                try
                {
                    entry = JsonConvert.DeserializeObject<Dictionary<string, object>>(log);
                }
                catch (Exception)
                {
                    // One unreadable entry does not cost the rest of the cache.
                }
                if (entry != null) result.Add(entry);
            }
        }
        return result;
    }

    private Dictionary<string, object> BuildDeviceInfo()
    {
        var resolution = Screen.currentResolution;

        return new Dictionary<string, object>
        {
            { "platform", GetPlatform() },
            { "processors", SystemInfo.processorCount },
            { "memory", SystemInfo.systemMemorySize },
            { "gdName", SystemInfo.graphicsDeviceName },
            { "gMemory", SystemInfo.graphicsMemorySize },
            { "gdApiType", SystemInfo.graphicsDeviceType.ToString() },
            { "resolution", new Dictionary<string, object>
                {
                    { "width", resolution.width },
                    { "height", resolution.height },
                    { "refreshRate", resolution.refreshRateRatio }
                }
            },
            { "dpi", SafeFloat(Screen.dpi) },
            { "os", SystemInfo.operatingSystem }
        };
    }

    // Sets the user id for every event from now on, and stamps it onto the
    // entries already queued without one (events logged before identity was
    // known, such as the debug-menu event at session start). Those entries
    // wait in the queue until this is called.
    public void SetUserId(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        userId = id;

        lock (queueLock)
        {
            int stamped = 0;
            foreach (var entry in logQueue)
            {
                if (entry != null && entry.Count > 0 && !HasUserId(entry))
                {
                    entry["user_id"] = id;
                    stamped++;
                }
            }
            if (stamped > 0)
            {
                MarkCacheDirty();
                Print($"GameLogger: user id set; stamped it onto {stamped} queued entries.");
            }
        }
    }

    // Legacy cache format (pre-091626). Kept so ParseCache can read it.
    [Serializable]
    private class LogListWrapper
    {
        public List<string> logs;
        public LogListWrapper(List<string> logs) => this.logs = logs;
    }

    // Unit transition gate (ICheckForUnitTransition). The transition waits
    // while a send is in progress, but never for more than
    // transitionMaxWaitSeconds: with the log service unreachable the loop
    // retries indefinitely, and holding the unit change for that would strand
    // the player. Whatever is still queued is persisted here and sent by the
    // next unit's build.
    public void CallToTransitionToNextUnit()
    {
        float now = Time.unscaledTime;
        if (!transitionPending)
        {
            transitionPending = true;
            transitionRequestedAt = now;
        }

        bool queueEmpty;
        lock (queueLock)
        {
            queueEmpty = logQueue.Count == 0;
        }

        ReadyToTransition = queueEmpty
            || !isSendingLogs
            || now - transitionRequestedAt >= transitionMaxWaitSeconds;

        if (ReadyToTransition) FlushCache();
    }
}
