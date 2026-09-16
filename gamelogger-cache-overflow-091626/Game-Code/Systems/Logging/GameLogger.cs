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

public class GameLogger : MonoBehaviour
{
    [SerializeField] private BoolVariable enableDebugLogs;

    private const string logServerUrl = "https://log.adroit.games/logs";
    private const string apiKey = "LEARN_FAST"; // Replace with your actual API key
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
    // (plus an immediate flush on pause / focus loss / quit).
    private const float cacheSaveInterval = 2f;
    // Don't repeat the "dropping entries" warning more often than this.
    private const float dropWarningInterval = 60f;

    // ---- Send behaviour ----
    // stratalog accepts up to max_batch_size entries per request (default 100).
    private const int maxBatchEntries = 50;
    private const float retryDelayInitial = 10f;
    private const float retryDelayMax = 60f;
    private const float offlinePollInterval = 5f;

    private static GameLogger _instance;
    [SerializeField] private Queue<Dictionary<string, object>> logQueue = new();
    private static bool isSendingLogs = false;
    private static object queueLock = new object(); // Protect logQueue with a lock
    private Dictionary<string, object> cachedDeviceInfo;
    private static string userId;

    // Cache write state.
    private bool cacheDirty = false;
    private float lastCacheSaveTime = -1000f;
    private float lastDropWarningTime = -1000f;
    private bool cacheWriteFailureReported = false;
    private int droppedSinceLastReport = 0; // trimmed entries not yet reported to the server
    private long droppedTotal = 0;

    // Send state.
    private int batchSize = maxBatchEntries;
    private int singleSendRemaining = 0; // > 0: send one entry per request (isolating a rejected entry)

    public static GameLogger Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<GameLogger>(); // Unity 2023+ compatible
                if (_instance == null)
                {
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
            Destroy(gameObject);
            return;
        }

        // Cache device info once at startup
        cachedDeviceInfo = BuildDeviceInfo();
        if (transform.parent != null) transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
        LoadCachedLogs();
    }

    private void Start()
    {
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

    private void FlushCache()
    {
        lock (queueLock)
        {
            if (cacheDirty) SaveCachedLogs();
        }
    }

    public void SendToServer(Dictionary<string, object> logData)
    {
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
        Dictionary<string, object> json = new()
        {
            { "game", "mhs" },
            { "user_id", userId },
            { "version", Application.version },
            { "sceneName", SceneManager.GetActiveScene().name },
            { "timestamp", DateTime.UtcNow.ToString("o") },
            { "eventType", eventType },
            { "data", data }
        };

        if (eventKey != null)
            json["eventKey"] = eventKey;

        if (data.TryGetValue("timestamp", out var value))
        {
            json["timestamp"] = value;
        }

        SendToServer(json);
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

    private IEnumerator SendQueuedLogs()
    {
        string logUrl;
        string logAuth;

        var logConfig = MHSBridge.Instance.GetLogSubmitConfig();
        if (logConfig != null)
        {
             logUrl = logConfig.url;    // e.g., "https://log.adroit.games/api/log/submit"
             logAuth = logConfig.auth;  // e.g., "Bearer abc123..."
        }
        else
        {
            // Fall back to hardcoded values (old host page without config)
            logUrl = logServerUrl;
            logAuth = $"Bearer {apiKey}";
        }
        isSendingLogs = true;
        float retryDelay = retryDelayInitial;

        while (true)
        {
            // Take the oldest entries without removing them. They are only
            // removed once the server has accepted them (or has told us it
            // never will).
            List<Dictionary<string, object>> toSend = new();
            lock (queueLock)
            {
                int take = singleSendRemaining > 0 ? 1 : batchSize;
                foreach (var entry in logQueue)
                {
                    toSend.Add(entry);
                    if (toSend.Count >= take) break;
                }
            }

            if (toSend.Count == 0) break; // Exit loop if no logs are available

            if (!IsNetworkAvailable())
            {
                yield return new WaitForSeconds(offlinePollInterval);
                continue;
            }

            string jsonData = BuildRequestBody(toSend);

            using (UnityWebRequest request = new UnityWebRequest(logUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", logAuth);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    ConsoleLogManager.Instance.PrintToConsole($"Log Sent ({toSend.Count}): {request.downloadHandler.text}", enableDebugLogs.Value);

                    RemoveFromQueue(toSend.Count);
                    if (singleSendRemaining > 0) singleSendRemaining--;
                    retryDelay = retryDelayInitial;
                    ReportDroppedEntriesIfAny();
                    continue;
                }

                long code = request.responseCode;
                ConsoleLogManager.Instance.PrintToConsole($"Log Post Failed: {request.error}; {code}", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Error);

                if (code == 400)
                {
                    // The server rejected the payload itself. Retrying can
                    // never succeed, so the entry has to go.
                    if (toSend.Count == 1)
                    {
                        Debug.LogWarning($"GameLogger: server rejected a log entry (400), dropping it: {request.downloadHandler.text}");
                        RemoveFromQueue(1);
                        if (singleSendRemaining > 0) singleSendRemaining--;
                    }
                    else
                    {
                        // stratalog rejects a whole batch if any one entry is
                        // invalid. Resend this batch's entries one at a time so
                        // only the bad one is dropped.
                        singleSendRemaining = toSend.Count;
                    }
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
                        RemoveFromQueue(1);
                        if (singleSendRemaining > 0) singleSendRemaining--;
                    }
                    continue;
                }

                // Anything else (connection failure, 401/403, 5xx, timeout):
                // keep the entries and retry with backoff. The cache limits
                // above bound how much can pile up while this goes on.
                yield return new WaitForSeconds(retryDelay);
                retryDelay = Mathf.Min(retryDelay * 2f, retryDelayMax);
            }
        }

        isSendingLogs = false;
    }

    // Single entry: the entry itself. Several entries: stratalog's batch
    // envelope, { "game": ..., "entries": [ ... ] }.
    private string BuildRequestBody(List<Dictionary<string, object>> entries)
    {
        if (entries.Count == 1)
        {
            return JsonConvert.SerializeObject(WithDevice(entries[0]));
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
        return JsonConvert.SerializeObject(envelope);
    }

    // Device info is attached to a shallow copy at send time. The queued
    // entry (and therefore the cached copy) stays small.
    private Dictionary<string, object> WithDevice(Dictionary<string, object> entry)
    {
        var copy = new Dictionary<string, object>(entry);
        copy["device"] = cachedDeviceInfo;
        return copy;
    }

    private void RemoveFromQueue(int count)
    {
        lock (queueLock)
        {
            for (int i = 0; i < count && logQueue.Count > 0; i++)
            {
                logQueue.Dequeue();
            }
            MarkCacheDirty();
        }
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

    // Call with queueLock held.
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

        string json = JsonConvert.SerializeObject(new List<Dictionary<string, object>>(logQueue));
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
            json = JsonConvert.SerializeObject(new List<Dictionary<string, object>>(logQueue));
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
            var entries = ParseCache(json);
            lock (queueLock)
            {
                logQueue.Clear();
                foreach (var entry in entries)
                {
                    logQueue.Enqueue(entry);
                }
            }
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
            var entries = ParseCache(json);
            lock (queueLock)
            {
                logQueue.Clear();
                foreach (var entry in entries)
                {
                    logQueue.Enqueue(entry);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to load cached logs (Standalone): {e.Message}. Discarding the cache.");
            try { File.Delete(path); } catch { }
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
                var entry = JsonConvert.DeserializeObject<Dictionary<string, object>>(log);
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

    public void SetUserId(string id)
    {
        userId = id;
    }

    // Legacy cache format (pre-091626). Kept so ParseCache can read it.
    [Serializable]
    private class LogListWrapper
    {
        public List<string> logs;
        public LogListWrapper(List<string> logs) => this.logs = logs;
    }
}
