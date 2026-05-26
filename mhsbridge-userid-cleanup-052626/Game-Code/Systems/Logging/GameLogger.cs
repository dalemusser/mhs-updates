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

    private static GameLogger _instance;
    [SerializeField] private Queue<Dictionary<string, object>> logQueue = new();
    private static bool isSendingLogs = false;
    private static object queueLock = new object(); // Protect logQueue with a lock
    private Dictionary<string, object> cachedDeviceInfo;
    private static string userId;

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

    public void SendToServer(Dictionary<string, object> logData)
    {
        lock (queueLock)
        {
            logQueue.Enqueue(logData);
            SaveCachedLogs();

            if (!isSendingLogs)
            {
                StartCoroutine(SendQueuedLogs());
            }
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

        while (true)
        {
            Dictionary<string, object> logData = new();

            lock (queueLock)
            {
                if (logQueue.Count > 0)
                {
                    logData = logQueue.Peek(); // Get the first log safely
                }
            }

            if (logData.Count == 0) break; // Exit loop if no logs are available

            if (!IsNetworkAvailable())
            {
                yield return new WaitForSeconds(5);
                continue;
            }

            logData["device"] = cachedDeviceInfo;
            string jsonData = JsonConvert.SerializeObject(logData);

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
                    ConsoleLogManager.Instance.PrintToConsole($"Log Sent: {request.downloadHandler.text}", enableDebugLogs.Value);

                    lock (queueLock)
                    {
                        if (logQueue.Count > 0) logQueue.Dequeue(); // Prevent out-of-range error
                        SaveCachedLogs();
                    }
                }
                else
                {
                    ConsoleLogManager.Instance.PrintToConsole($"Log Post Failed: {request.error}; {request.responseCode}", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Error);
                    if (request.responseCode == 400)
                    {
                        logQueue.Dequeue();
                        continue;
                    }
                    yield return new WaitForSeconds(10); // Retry after a delay
                }
            }
        }

        isSendingLogs = false;
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
#if UNITY_WEBGL
        SaveCachedLogsWebGL();
#else
        SaveCachedLogsStandalone();
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

    private void SaveCachedLogsWebGL()
    {
        try
        {
            List<string> logList = new List<string>();
            foreach (var log in logQueue)
            {
                logList.Add(JsonConvert.SerializeObject(log));
            }
            string json = JsonUtility.ToJson(new LogListWrapper(logList));
            PlayerPrefs.SetString(logCacheFile, json);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save cached logs (WebGL): {e.Message}");
        }
    }

    private void SaveCachedLogsStandalone()
    {
        string path = Path.Combine(Application.persistentDataPath, logCacheFile);
        try
        {
            List<string> logList = new List<string>();
            foreach (var log in logQueue)
            {
                logList.Add(JsonConvert.SerializeObject(log));
            }
            string json = JsonUtility.ToJson(new LogListWrapper(logList));
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save cached logs (Standalone): {e.Message}");
        }
    }

    private void LoadCachedLogsWebGL()
    {
        if (PlayerPrefs.HasKey(logCacheFile))
        {
            try
            {
                string json = PlayerPrefs.GetString(logCacheFile);
                LogListWrapper wrapper = JsonUtility.FromJson<LogListWrapper>(json);
                if (wrapper?.logs != null)
                {
                    lock (queueLock)
                    {
                        logQueue.Clear();
                        foreach (string log in wrapper.logs)
                        {
                            logQueue.Enqueue(JsonConvert.DeserializeObject<Dictionary<string, object>>(log));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load cached logs (WebGL): {e.Message}");
            }
        }
    }

    private void LoadCachedLogsStandalone()
    {
        string path = Path.Combine(Application.persistentDataPath, logCacheFile);
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                LogListWrapper wrapper = JsonUtility.FromJson<LogListWrapper>(json);
                if (wrapper?.logs != null)
                {
                    lock (queueLock)
                    {
                        logQueue.Clear();
                        foreach (string log in wrapper.logs)
                        {
                            logQueue.Enqueue(JsonConvert.DeserializeObject<Dictionary<string, object>>(log));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load cached logs (Standalone): {e.Message}");
            }
        }
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

    [Serializable]
    private class LogListWrapper
    {
        public List<string> logs;
        public LogListWrapper(List<string> logs) => this.logs = logs;
    }
}
