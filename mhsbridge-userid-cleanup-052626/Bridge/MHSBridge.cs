using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Bridge between the Unity game and its hosting environment.
/// Attach this script to a GameObject named "MHSBridge" in the first scene that loads.
///
/// Identity, service endpoints, and navigation configuration come from the host page
/// via window.__mhsBridgeConfig. The host page sets this before Unity starts.
///
/// Two operating modes:
///   PWA mode  - StrataHub manages unit transitions. The game signals completion
///               and the host page handles navigation, progress tracking, and downloads.
///   URL mode  - The game manages its own navigation using relative URLs or a unitMap.
///
/// Mode is determined automatically: if the host page calls
/// SendMessage('MHSBridge', 'OnPWAReady', '...'), the game is in PWA mode.
/// Otherwise it's in URL mode.
///
/// If window.__mhsBridgeConfig is absent (old host page), identity and service
/// config are not available from the bridge. The game should use its existing mechanisms.
/// </summary>
public class MHSBridge : MonoBehaviour
{
    // --- JS function imports (from MHSBridge.jslib) ---

    [DllImport("__Internal")]
    private static extern IntPtr MHSBridge_GetConfig();

    [DllImport("__Internal")]
    private static extern void MHSBridge_Free(IntPtr ptr);

    [DllImport("__Internal")]
    private static extern void MHSBridge_NotifyUnitComplete(string unitId);

    [DllImport("__Internal")]
    private static extern void MHSBridge_EndGame();

    [DllImport("__Internal")]
    private static extern void MHSBridge_NavigateToUnit(string url);

    [DllImport("__Internal")]
    private static extern IntPtr MHSBridge_GetUnitURL(string unitName);

    // --- State ---

    private bool _isPWA = false;
    private bool _hasConfig = false;
    private string _userId = null;
    private string _userName = null;
    private BridgeConfig _config = null;

    /// <summary>True if the game is running inside the StrataHub PWA.</summary>
    public bool IsPWA => _isPWA;

    /// <summary>True if window.__mhsBridgeConfig was present and parsed.</summary>
    public bool HasConfig => _hasConfig;

    // --- Singleton ---

    public static MHSBridge Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Load config from host page on startup
        LoadConfig();
    }

    // --- Config loading ---

    /// <summary>
    /// Loads the bridge configuration from window.__mhsBridgeConfig.
    /// Called once during Awake(). In Editor, development defaults are used.
    /// </summary>
    private void LoadConfig()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = MHSBridge_GetConfig();
            if (ptr == IntPtr.Zero)
                return;

            string json = PtrToStringUTF8(ptr);
            if (string.IsNullOrEmpty(json))
                return;

            _config = JsonUtility.FromJson<BridgeConfig>(json);
            if (_config != null)
            {
                _hasConfig = true;
                if (_config.identity != null)
                {
                    _userId = _config.identity.user_id;
                    _userName = _config.identity.name;
                }
                Debug.Log("MHSBridge: Config loaded from host page");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("MHSBridge: Failed to parse config: " + e.Message);
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                MHSBridge_Free(ptr);
        }
#else
        // Editor mode: use development defaults so devs can log and save during testing.
        //
        // The sentinel user_id "000000000000000000000001" matches the host page's
        // local-development fallback (mhs-updates/build-automation-update-051226/
        // MHS-Bridge-index.html). For round-tripping editor builds against the live
        // services (log.adroit.games / save.adroit.games), a stratahub.users
        // document with _id = ObjectId("000000000000000000000001") must exist in
        // the shared adroit.games stratahub. See DIRECTIONS.md.
        _userId = "000000000000000000000001";
        _userName = "MHS Developer";
        _config = new BridgeConfig
        {
            identity = new BridgeIdentity { user_id = "000000000000000000000001", name = "MHS Developer" },
            services = new BridgeServices
            {
                log_submit    = new ServiceConfig { url = "https://log.adroit.games/api/log/submit",       auth = "Bearer LEARN_FAST" },
                state_save    = new ServiceConfig { url = "https://save.adroit.games/api/state/save",      auth = "Bearer LEARN_FAST" },
                state_load    = new ServiceConfig { url = "https://save.adroit.games/api/state/load",      auth = "Bearer LEARN_FAST" },
                settings_save = new ServiceConfig { url = "https://save.adroit.games/api/settings/save",   auth = "Bearer LEARN_FAST" },
                settings_load = new ServiceConfig { url = "https://save.adroit.games/api/settings/load",   auth = "Bearer LEARN_FAST" }
            }
        };
        _hasConfig = true;
        Debug.Log("MHSBridge: Editor mode — using development defaults (user_id=000000000000000000000001)");
#endif
    }

    // --- Called by the PWA host page via SendMessage ---

    /// <summary>
    /// Called by the PWA host page after Unity finishes loading.
    /// Signals that the game is running inside the managed PWA environment.
    ///
    /// Parameter is a JSON string with identity: { "user_id": "...", "name": "..." }
    /// An empty string is also accepted (identity comes from __mhsBridgeConfig instead).
    /// </summary>
    public void OnPWAReady(string identityJson)
    {
        _isPWA = true;

        // Parse identity from the parameter if provided
        if (!string.IsNullOrEmpty(identityJson))
        {
            try
            {
                var identity = JsonUtility.FromJson<BridgeIdentity>(identityJson);
                if (identity != null)
                {
                    if (!string.IsNullOrEmpty(identity.user_id))
                        _userId = identity.user_id;
                    if (!string.IsNullOrEmpty(identity.name))
                        _userName = identity.name;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("MHSBridge: Failed to parse OnPWAReady identity: " + e.Message);
            }
        }

        Debug.Log("MHSBridge: PWA mode activated" +
            (_userId != null ? ", user_id=" + _userId : ""));
    }

    // --- Called by game code: Identity ---

    /// <summary>
    /// Returns the user_id (24-character lowercase hex of stratahub.users._id).
    ///
    /// This is the canonical identifier sent to stratalog, stratasave, and mhsgrader.
    /// It is set during Awake (from __mhsBridgeConfig or editor defaults) and can be
    /// overridden by OnPWAReady. Returns empty string if no identity is available.
    /// </summary>
    public string GetUserID()
    {
        return _userId ?? string.Empty;
    }

    /// <summary>
    /// Returns the user's display name, or empty string if not available.
    /// </summary>
    public string GetUserName()
    {
        return _userName ?? string.Empty;
    }

    // --- Called by game code: Service configuration ---

    /// <summary>
    /// Returns the log submit endpoint config (URL and auth header), or null if not configured.
    /// URL is a full endpoint (e.g., "https://log.adroit.games/api/log/submit").
    /// When null, the game should fall back to its hardcoded log service settings.
    /// </summary>
    public ServiceConfig GetLogSubmitConfig()
    {
        if (_config?.services?.log_submit != null &&
            !string.IsNullOrEmpty(_config.services.log_submit.url))
        {
            return _config.services.log_submit;
        }
        return null;
    }

    /// <summary>
    /// Returns the state save endpoint config (URL and auth header), or null if not configured.
    /// </summary>
    public ServiceConfig GetStateSaveConfig()
    {
        if (_config?.services?.state_save != null &&
            !string.IsNullOrEmpty(_config.services.state_save.url))
        {
            return _config.services.state_save;
        }
        return null;
    }

    /// <summary>
    /// Returns the state load endpoint config (URL and auth header), or null if not configured.
    /// </summary>
    public ServiceConfig GetStateLoadConfig()
    {
        if (_config?.services?.state_load != null &&
            !string.IsNullOrEmpty(_config.services.state_load.url))
        {
            return _config.services.state_load;
        }
        return null;
    }

    /// <summary>
    /// Returns the settings save endpoint config (URL and auth header), or null if not configured.
    /// </summary>
    public ServiceConfig GetSettingsSaveConfig()
    {
        if (_config?.services?.settings_save != null &&
            !string.IsNullOrEmpty(_config.services.settings_save.url))
        {
            return _config.services.settings_save;
        }
        return null;
    }

    /// <summary>
    /// Returns the settings load endpoint config (URL and auth header), or null if not configured.
    /// </summary>
    public ServiceConfig GetSettingsLoadConfig()
    {
        if (_config?.services?.settings_load != null &&
            !string.IsNullOrEmpty(_config.services.settings_load.url))
        {
            return _config.services.settings_load;
        }
        return null;
    }

    // --- Called by game code: Navigation ---

    /// <summary>
    /// Returns the URL for a unit by name.
    ///
    /// If a unitMap is configured, looks up the unit there.
    /// If no unitMap exists, returns a relative URL (e.g., "../unit2/index.html").
    ///
    /// Return values:
    /// - URL string: unit is available, navigate to this URL
    /// - null with isLocked=true: unit is explicitly locked (not yet accessible)
    /// </summary>
    public string GetUnitURL(string unitName, out bool isLocked)
    {
        isLocked = false;

#if UNITY_WEBGL && !UNITY_EDITOR
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = MHSBridge_GetUnitURL(unitName);
            if (ptr == IntPtr.Zero)
                return null;

            string result = PtrToStringUTF8(ptr);
            if (result == "LOCKED")
            {
                isLocked = true;
                return null;
            }
            if (string.IsNullOrEmpty(result))
                return null;

            return result;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                MHSBridge_Free(ptr);
        }
#else
        return null;
#endif
    }

    /// <summary>
    /// Call this when the current unit is complete.
    /// In PWA mode: notifies the host page, which handles the transition to the next unit.
    /// In URL mode: navigates to the next unit using the provided relative URL.
    ///              Passing empty or null for nextUnitRelativeUrl is a no-op.
    ///
    /// This should be called for EVERY unit completion, including the last unit.
    /// StrataHub handles determining what the next unit is.
    /// For the end of the game (after the game's end-of-game screen), use EndGame() instead.
    /// </summary>
    /// <param name="currentUnitId">The unit that was just completed, e.g., "unit3"</param>
    /// <param name="nextUnitRelativeUrl">
    /// Relative URL to the next unit, e.g., "../unit4/index.html".
    /// Ignored in PWA mode. Pass null or empty for the final unit in URL mode.
    /// </param>
    public void CompleteUnit(string currentUnitId, string nextUnitRelativeUrl)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (_isPWA)
        {
            // PWA mode: notify the host page. It handles the transition.
            MHSBridge_NotifyUnitComplete(currentUnitId);
        }
        else
        {
            // URL mode: navigate to the next unit directly.
            if (!string.IsNullOrEmpty(nextUnitRelativeUrl))
            {
                MHSBridge_NavigateToUnit(nextUnitRelativeUrl);
            }
        }
#else
        Debug.Log($"MHSBridge: CompleteUnit(\"{currentUnitId}\") ignored in Editor");
#endif
    }

    /// <summary>
    /// Call this when the user has finished the game and is ready to exit.
    /// This should be called from the game's end-of-game screen (e.g., when the
    /// user clicks "Continue" or "Return" after the final congratulatory screen).
    ///
    /// Behavior depends on the hosting environment:
    /// - PWA mode: navigates back to the Mission HydroSci units page in StrataHub
    /// - Browser tab: attempts to close the tab; if blocked, shows "you can close this tab"
    /// - Editor: logs a message (no-op)
    ///
    /// This is separate from CompleteUnit. CompleteUnit signals that a unit's gameplay
    /// is done and triggers the transition to the next unit. EndGame signals that the
    /// user is done with the entire game and wants to leave.
    /// </summary>
    public void EndGame()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        Debug.Log("MHSBridge: EndGame called");
        MHSBridge_EndGame();
#else
        Debug.Log("MHSBridge: EndGame() ignored in Editor");
#endif
    }

    /// <summary>
    /// Navigates to a unit by name (e.g., "unit1", "unit3").
    /// Used by the loader to send the user to their current unit.
    ///
    /// Uses GetUnitURL to resolve the URL (unitMap if available, relative otherwise).
    /// If the unit is locked, logs a warning and does nothing.
    ///
    /// No-ops in PWA mode — StrataHub handles navigation directly.
    /// </summary>
    public void NavigateToUnit(string unitName)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (_isPWA)
        {
            Debug.LogWarning("MHSBridge: NavigateToUnit ignored in PWA mode");
            return;
        }
        if (string.IsNullOrEmpty(unitName))
        {
            Debug.LogError("MHSBridge: NavigateToUnit called with null or empty unitName");
            return;
        }

        bool isLocked;
        string unitURL = GetUnitURL(unitName, out isLocked);

        if (isLocked)
        {
            Debug.LogWarning("MHSBridge: Unit \"" + unitName + "\" is locked");
            return;
        }

        if (unitURL != null)
        {
            MHSBridge_NavigateToUnit(unitURL);
        }
        else
        {
            Debug.LogWarning("MHSBridge: No URL found for unit \"" + unitName + "\"");
        }
#endif
    }

    // --- JSON data classes ---

    [Serializable]
    public class BridgeConfig
    {
        public BridgeIdentity identity;
        public BridgeServices services;
        public BridgeNavigation navigation;
    }

    [Serializable]
    public class BridgeIdentity
    {
        // Field name is snake_case to match the JSON shape the host page sets
        // in window.__mhsBridgeConfig.identity (and the JSON shape stratalog/
        // stratasave accept). JsonUtility.FromJson maps the JSON key directly
        // to this field name.
        public string user_id;
        public string name;
    }

    [Serializable]
    public class BridgeServices
    {
        public ServiceConfig log_submit;
        public ServiceConfig state_save;
        public ServiceConfig state_load;
        public ServiceConfig settings_save;
        public ServiceConfig settings_load;
    }

    [Serializable]
    public class ServiceConfig
    {
        public string url;
        public string auth;
    }

    [Serializable]
    public class BridgeNavigation
    {
        // Note: JsonUtility cannot deserialize a Dictionary/Map.
        // The unitMap is read via jslib (MHSBridge_GetUnitURL) instead.
    }

    // --- Helpers ---

    /// <summary>
    /// Reads a null-terminated UTF-8 string from an unmanaged pointer.
    /// Compatible with all Unity .NET profiles (does not require .NET Standard 2.1).
    /// </summary>
    private static string PtrToStringUTF8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        if (len == 0) return string.Empty;
        byte[] bytes = new byte[len];
        Marshal.Copy(ptr, bytes, 0, len);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
