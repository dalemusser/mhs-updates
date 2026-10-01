using System;
using System.Collections;
using System.Text;
using Obvious.Soap;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Networking;

namespace MHS
{
    [Serializable]
    public class SettingsSaveRequest
    {
        public string user_id;
        public string game;
        public SettingsSaver.SettingsValues settings_data;
    }

    [Serializable]
    public class SettingsLoadRequest
    {
        public string user_id;
        public string game;
    }

    [Serializable]
    public class SettingsResponse
    {
        public string id;
        public string user_id;
        public string game;
        public SettingsSaver.SettingsValues settings_data;
        public string timestamp;
    }


    public class SettingsSaveManager : MonoBehaviour
    {
        [SerializeField] private StringVariable playerId;
        [SerializeField] private BoolVariable enableDebugLogs;
        [SerializeField] private ScriptableEventNoParam OnSaveSettings;
        [SerializeField] private ScriptableEventNoParam OnLoadSettings;

        private const string _apiBaseUrl = "https://save.adroit.games";
        private const string _apiKey = "LEARN_FAST";
        private const string _gameId = "mhs";

        private SettingsSaver.SettingsValues _currentSettings;

        public static SettingsSaveManager Instance { get; private set; }

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnEnable()
        {
            Assert.IsNotNull(OnSaveSettings, $"SOAP Variable {OnSaveSettings} cannot be null");
            Assert.IsNotNull(OnLoadSettings, $"SOAP Variable {OnLoadSettings} cannot be null");
            Assert.IsNotNull(playerId, $"SOAP Variable {playerId} cannot be null");
            Assert.IsNotNull(enableDebugLogs, $"SOAP Variable {enableDebugLogs} cannot be null");

            OnSaveSettings.OnRaised += SaveSettingsToDatabase;
            OnLoadSettings.OnRaised += LoadSettingsFromDatabase;
        }

        private void OnDisable()
        {

            OnSaveSettings.OnRaised -= SaveSettingsToDatabase;
            OnLoadSettings.OnRaised -= LoadSettingsFromDatabase;
        }

        public void SaveSettingsToDatabase()
        {
            Instance.SaveSettings(success =>
            {
                if (success)
                    ConsoleLogManager.Instance.PrintToConsole("Settings saved!", enableDebugLogs.Value);
                else
                    ConsoleLogManager.Instance.PrintToConsole("Failed to save settings", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
            });
        }

        public void LoadSettingsFromDatabase()
        {
            Instance.LoadSettings(success =>
            {
                if (success)
                    ConsoleLogManager.Instance.PrintToConsole("Settings loaded!", enableDebugLogs.Value);
                else
                    ConsoleLogManager.Instance.PrintToConsole("Failed to load settings", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
            });
        }

        // Save settings to server
        public void SaveSettings(Action<bool> callback = null)
        {
            if (playerId == null || string.IsNullOrEmpty(playerId.Value))
            {
                ConsoleLogManager.Instance.PrintToConsole("[SettingsSaveManager] SaveSettings skipped: playerId is empty. Caller should wait " +
                                 "until auth has set playerId.Value before raising OnSaveSettings.",
                                 enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(false);
                return;
            }
            SettingsSaver settingsSaver = GetComponent<SettingsSaver>();
            if (settingsSaver == null) return;
            _currentSettings = JsonUtility.FromJson<SettingsSaver.SettingsValues>(settingsSaver.RecordData());
            StartCoroutine(SaveSettingsCoroutine(callback));
        }

        private IEnumerator SaveSettingsCoroutine(Action<bool> callback)
        {
            ConsoleLogManager.Instance.PrintToConsole("Settings Save Started...", enableDebugLogs.Value);

            var saveConfig = MHSBridge.Instance.GetSettingsSaveConfig();
            string saveUrl;
            string saveAuth;

            if (saveConfig != null)
            {
                saveUrl = saveConfig.url;
                saveAuth = saveConfig.auth;
            }
            else
            {
                // Fall back to hardcoded values
                saveUrl = _apiBaseUrl + "/api/settings/save";
                saveAuth = "Bearer " + _apiKey;
            }

            var request = new SettingsSaveRequest
            {
                user_id = playerId,
                game = _gameId,
                settings_data = _currentSettings
            };

            string json = JsonUtility.ToJson(request);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using (var www = new UnityWebRequest(saveUrl, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Authorization", saveAuth);

                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    ConsoleLogManager.Instance.PrintToConsole("Settings saved successfully!", enableDebugLogs.Value);
                    callback?.Invoke(true);
                }
                else
                {
                    ConsoleLogManager.Instance.PrintToConsole($"Failed to save settings: {www.error}", enableDebugLogs.Value);
                    callback?.Invoke(false);
                }
            }
        }

        // Load settings from server
        public void LoadSettings(Action<bool> callback)
        {
            if (playerId == null || string.IsNullOrEmpty(playerId.Value))
            {
                ConsoleLogManager.Instance.PrintToConsole("[SettingsSaveManager] LoadSettings skipped: playerId " +
                                                          "is empty. AuthManager will retry on its next identity-fetch " +
                                                          "tick once auth has populated playerId.Value.",
                                        enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(false);
                return;
            }
            StartCoroutine(LoadSettingsCoroutine(callback));
        }

        string json;
        byte[] bodyRaw;
        // UploadHandlerRaw myUploadHandler;
        SettingsResponse response;
        private IEnumerator LoadSettingsCoroutine(Action<bool> callback)
        {
            ConsoleLogManager.Instance.PrintToConsole("Settings Load Started...", enableDebugLogs.Value);

            var loadConfig = MHSBridge.Instance.GetSettingsLoadConfig();
            string loadUrl;
            string loadAuth;

            if (loadConfig != null)
            {
                loadUrl = loadConfig.url;
                loadAuth = loadConfig.auth;
            }
            else
            {
                // Fall back to hardcoded values
                loadUrl = _apiBaseUrl + "/api/settings/load";
                loadAuth = "Bearer " + _apiKey;
            }
            var request = new SettingsLoadRequest
            {
                user_id = playerId,
                game = _gameId
            };

            json = JsonUtility.ToJson(request);
            bodyRaw = Encoding.UTF8.GetBytes(json);

            using (var www = new UnityWebRequest(loadUrl, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Authorization", loadAuth);

                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    // The service answers 200 with the body "null" when the player has
                    // no saved settings. JsonUtility.FromJson throws on anything that is
                    // not a JSON object ("null", "[]", ...), which killed this coroutine
                    // on every scene load for such players (mhs-updates/gamelogger-fix-093026).
                    // An empty or null body means "no settings"; any other unparsable
                    // body is treated as a failed load, not an exception.
                    string body = www.downloadHandler.text;
                    response = null;
                    if (!string.IsNullOrWhiteSpace(body) && body.Trim() != "null")
                    {
                        try
                        {
                            response = JsonUtility.FromJson<SettingsResponse>(body);
                        }
                        catch (Exception e)
                        {
                            ConsoleLogManager.Instance.PrintToConsole($"Settings load returned an unreadable body ({e.Message}): {body.Substring(0, Math.Min(body.Length, 120))}", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                            callback?.Invoke(false);
                            yield break;
                        }
                    }
                    if (response != null)
                    {
                        _currentSettings = response.settings_data;
                        ApplySettings();
                        ConsoleLogManager.Instance.PrintToConsole("Settings loaded successfully!", enableDebugLogs.Value);
                        callback?.Invoke(true);
                    }
                    else
                    {
                        ConsoleLogManager.Instance.PrintToConsole("No settings found, using defaults", enableDebugLogs.Value);
                        callback?.Invoke(false);
                    }
                }
                else
                {
                    ConsoleLogManager.Instance.PrintToConsole($"Failed to load settings: {www.error}", enableDebugLogs.Value);
                    callback?.Invoke(false);
                }
            }
        }

        // Apply settings to the game
        private void ApplySettings()
        {
            SettingsSaver settingsSaver = GetComponent<SettingsSaver>();
            if (settingsSaver != null)
            {
                var settingsString = JsonUtility.ToJson(_currentSettings);
                settingsSaver.ApplyData(settingsString);
            }
            // Apply other settings as needed...
        }
    }
}
