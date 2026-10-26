using MHS;
using Obvious.Soap;
using PixelCrushers;
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// The save transport: one HTTP request per call to the save service's state
// endpoints, and the answer classified for the policy in DatabaseSaver.
//
// Rules this file keeps (mhs-updates/gamesave-fix-102426, 2026-10-24):
//  - The endpoint and the auth value come from the host page through MHSBridge
//    on every request (no hard-coded host, no built-in key). Without a config
//    there is nowhere to send; the caller waits and asks again.
//  - Every request has a timeout, so a stalled connection is a failure that
//    can be retried, never a save that stays "in progress" for ever.
//  - The auth value is never written to the console. The URL may be.
//  - The answer is classified, not just "success or not": the service saying
//    "no saves" is different from the request failing, and a failure says
//    whether retrying can help (network, timeout, 5xx, refused key) or not
//    (the payload itself was rejected).
//  - A response body the game cannot read is a failed request, never an
//    exception that ends the coroutine and leaves the caller waiting.
//  - This class sends and parses; it does not retry, queue or decide. That is
//    DatabaseSaver's job, so the two can be read on their own.
public class GameSaveManager : MonoBehaviour
{
    [SerializeField] private BoolVariable enableDebugLogs;

    // A request that gets no answer in this time is a failure (the caller
    // backs off and retries), not a hang. Settable so tests can shorten it.
    public static int requestTimeoutSeconds = 30;

    // Where the endpoint configs come from. Tests point these at a local
    // server; the game leaves them null and uses the host page's config.
    public static Func<MHSBridge.ServiceConfig> saveConfigOverride;
    public static Func<MHSBridge.ServiceConfig> loadConfigOverride;

    public MHSBridge.ServiceConfig SaveConfig =>
        saveConfigOverride != null ? saveConfigOverride() :
        (MHSBridge.Instance != null ? MHSBridge.Instance.GetStateSaveConfig() : null);

    public MHSBridge.ServiceConfig LoadConfig =>
        loadConfigOverride != null ? loadConfigOverride() :
        (MHSBridge.Instance != null ? MHSBridge.Instance.GetStateLoadConfig() : null);

    // ---- Wire types ----

    [Serializable]
    public class GameSave
    {
        public string id;
        public string user_id;
        public string game;
        public string timestamp;
        public SavedGameData save_data;
    }

    [Serializable]
    private class GameSaveList
    {
        public GameSave[] saves;
    }

    // Written by the game into save_data as "mhs_meta" (the service stores
    // save_data as it is, and SavedGameData ignores the extra key when it is
    // read back). It lets a device compare its own copy with the service's,
    // and a later service change order saves by it.
    [Serializable]
    public class SaveMeta
    {
        public long seq;          // per launch of the game, counts up
        public string saved_at;   // device clock, UTC, ISO 8601 round-trip form
        public string session_id; // one per launch of the game
        public string version;    // Application.version of the build that saved
        public string scene;      // the scene the save was made in
        public int checkpoint;    // the scene checkpoint value in the save, -1 if none

        public bool HasTime => !string.IsNullOrEmpty(saved_at);
    }

    [Serializable]
    private class SaveDataMetaOnly
    {
        public SaveMeta mhs_meta;
    }

    [Serializable]
    private class GameSaveMetaOnly
    {
        public SaveDataMetaOnly save_data;
    }

    [Serializable]
    private class GameSaveMetaList
    {
        public GameSaveMetaOnly[] saves;
    }

    [Serializable]
    public class GameSettings
    {
        public string id;
        public string user_id;
        public string game;
        public string timestamp;
        public SavedGameData settings_data;
    }

    // ---- Results ----

    public enum Outcome
    {
        Accepted,   // the service stored the save (save) or returned saves (load)
        NoSaves,    // load only: the service answered and there is nothing stored
        Failed      // the request did not get a usable answer; see retryable
    }

    public class RequestResult
    {
        public Outcome outcome;
        public long statusCode;
        public string error;            // transport error text, if any
        public string body;             // response body (may be empty)
        public bool retryable;          // true: the same request may succeed later
        public float retryAfterSeconds; // > 0 when the service asked for a pause
        public string reason;           // short, student-safe reason for the status line

        public bool Accepted => outcome == Outcome.Accepted;
    }

    public class LoadResult : RequestResult
    {
        public GameSave[] saves = new GameSave[0];
        public SaveMeta[] metas = new SaveMeta[0];

        public GameSave Newest => saves != null && saves.Length > 0 ? saves[0] : null;
        public SaveMeta NewestMeta => metas != null && metas.Length > 0 ? metas[0] : null;
        // JsonUtility creates an empty SavedGameData for a missing save_data, so a
        // real save is one with a scene name.
        public static bool IsASave(GameSave s) => s != null && s.save_data != null && !string.IsNullOrEmpty(s.save_data.sceneName);
        public bool NewestIsASave => IsASave(Newest);

        // The newest document that reads as a save, and where it sits in the list
        // (0 normally; higher when newer documents could not be read).
        public int ReadableIndex
        {
            get
            {
                for (int i = 0; saves != null && i < saves.Length; i++) if (IsASave(saves[i])) return i;
                return -1;
            }
        }
        public bool HasReadable => ReadableIndex >= 0;
        public GameSave Readable => HasReadable ? saves[ReadableIndex] : null;
        public SaveMeta ReadableMeta
        {
            get
            {
                int i = ReadableIndex;
                return i >= 0 && metas != null && i < metas.Length ? metas[i] : null;
            }
        }
    }

    // ---- Legacy API (kept so existing callers compile; one request, no retry) ----

    /// <summary>
    /// Save game data with callback to handle success or failure. One request;
    /// the retry policy lives in DatabaseSaver.
    /// </summary>
    public void SaveGame(string userId, string gameName, SavedGameData data, Action<bool, string> onSaved = null)
    {
        SaveGame(userId, gameName, null, data, onSaved);
    }

    public void SaveGame(string userId, string gameName, string api, SavedGameData data, Action<bool, string> onSaved = null)
    {
        string json;
        try
        {
            json = SaveSystem.Serialize(data);
        }
        catch (Exception e)
        {
            Debug.LogError($"GameSaveManager: the save data could not be serialized ({e.Message}).");
            Safe(() => onSaved?.Invoke(false, "serialization failed"));
            return;
        }
        SaveJson(userId, gameName, json, r => Safe(() => onSaved?.Invoke(r.Accepted, r.Accepted ? r.body : (r.error ?? r.reason))), api);
    }

    /// <summary>
    /// Load game data with callback providing loaded saves, or null when the
    /// request failed or there are no saves (the legacy contract). New code
    /// uses LoadLatest, which tells the two apart.
    /// </summary>
    public void LoadGame(string userId, string gameName, int numberOfSavesToLoad, Action<GameSave[]> onLoaded)
    {
        LoadGame(userId, gameName, null, numberOfSavesToLoad, onLoaded);
    }

    public void LoadGame(string userId, string gameName, string api, int numberOfSavesToLoad, Action<GameSave[]> onLoaded)
    {
        LoadLatest(userId, gameName, numberOfSavesToLoad, r => Safe(() => onLoaded?.Invoke(r.Accepted ? r.saves : null)), api);
    }

    public IEnumerator LoadCoroutine(string userId, string gameName, int limit, Action<GameSave[]> onLoaded)
    {
        bool done = false;
        LoadGame(userId, gameName, limit, saves => { done = true; onLoaded?.Invoke(saves); });
        while (!done) yield return null;
    }

    // ---- New API ----

    /// <summary>
    /// One save request. saveDataJson is the JsonUtility form of a
    /// SavedGameData (see WithMeta for the metadata the game adds to it).
    /// onDone always runs exactly once, on the main thread.
    /// </summary>
    public void SaveJson(string userId, string gameName, string saveDataJson, Action<RequestResult> onDone, string urlOverride = null)
    {
        var config = SaveConfig;
        string url = !string.IsNullOrEmpty(urlOverride) ? urlOverride : config?.url;
        if (string.IsNullOrEmpty(url))
        {
            onDone?.Invoke(new RequestResult { outcome = Outcome.Failed, retryable = true, reason = "the save service is not configured", error = "no state_save config" });
            return;
        }
        if (!IsValidUserId(userId))
        {
            onDone?.Invoke(new RequestResult { outcome = Outcome.Failed, retryable = true, reason = "waiting for your sign-in", error = "no valid user id" });
            return;
        }
        if (string.IsNullOrEmpty(saveDataJson))
        {
            onDone?.Invoke(new RequestResult { outcome = Outcome.Failed, retryable = false, reason = "nothing to save", error = "empty save data" });
            return;
        }
        string body = BuildSavePayload(userId, gameName, saveDataJson);
        StartCoroutine(SendCoroutine(url, config?.auth, body, false, r => onDone?.Invoke(r)));
    }

    /// <summary>
    /// One load request for the newest saves of a player. The result says
    /// whether the service answered with saves, with none, or did not answer.
    /// onDone always runs exactly once, on the main thread.
    /// </summary>
    public void LoadLatest(string userId, string gameName, int limit, Action<LoadResult> onDone, string urlOverride = null)
    {
        var config = LoadConfig;
        string url = !string.IsNullOrEmpty(urlOverride) ? urlOverride : config?.url;
        if (string.IsNullOrEmpty(url))
        {
            onDone?.Invoke(new LoadResult { outcome = Outcome.Failed, retryable = true, reason = "the save service is not configured", error = "no state_load config" });
            return;
        }
        if (!IsValidUserId(userId))
        {
            onDone?.Invoke(new LoadResult { outcome = Outcome.Failed, retryable = true, reason = "waiting for your sign-in", error = "no valid user id" });
            return;
        }
        string body = "{\"user_id\":" + JsonString(userId) + ",\"game\":" + JsonString(gameName) + ",\"limit\":" + Math.Max(1, limit) + "}";
        StartCoroutine(SendCoroutine(url, config?.auth, body, true, r => onDone?.Invoke((LoadResult)r)));
    }

    // ---- Payload helpers (pure; covered by the PlayMode tests) ----

    /// <summary>The request body of a save: user id, game, and the save data as given.</summary>
    public static string BuildSavePayload(string userId, string gameName, string saveDataJson)
    {
        var sb = new StringBuilder(saveDataJson.Length + 96);
        sb.Append("{\"user_id\":").Append(JsonString(userId));
        sb.Append(",\"game\":").Append(JsonString(gameName));
        sb.Append(",\"save_data\":").Append(saveDataJson);
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Adds "mhs_meta" to the JSON object of a SavedGameData. The service keeps
    /// it inside save_data; JsonUtility ignores it when the save is read back
    /// as a SavedGameData, so builds without this code are unaffected.
    /// </summary>
    public static string WithMeta(string saveDataJson, SaveMeta meta)
    {
        if (string.IsNullOrEmpty(saveDataJson) || meta == null) return saveDataJson;
        string trimmed = saveDataJson.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return saveDataJson;
        string metaJson = JsonUtility.ToJson(meta);
        int brace = saveDataJson.IndexOf('{');
        bool empty = saveDataJson.Substring(brace + 1).TrimStart().StartsWith("}");
        return saveDataJson.Substring(0, brace + 1) + "\"mhs_meta\":" + metaJson + (empty ? "" : ",") + saveDataJson.Substring(brace + 1);
    }

    /// <summary>The "mhs_meta" of a save_data JSON object, or null when it has none.</summary>
    public static SaveMeta ReadMeta(string saveDataJson)
    {
        if (string.IsNullOrEmpty(saveDataJson) || saveDataJson.IndexOf("mhs_meta", StringComparison.Ordinal) < 0) return null;
        try
        {
            var m = JsonUtility.FromJson<SaveDataMetaOnly>(saveDataJson);
            return m != null && m.mhs_meta != null && m.mhs_meta.HasTime ? m.mhs_meta : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a load response. The service answers with a JSON array of saves,
    /// newest first, and [] when there are none. Anything else is unreadable:
    /// an empty body (a filter, a redirect, a misrouted endpoint) is not the
    /// service saying "no saves", and must not start a new game.
    /// </summary>
    public static bool TryParseLoadBody(string body, out GameSave[] saves, out SaveMeta[] metas, out string error)
    {
        saves = new GameSave[0];
        metas = new SaveMeta[0];
        error = null;
        string t = (body ?? "").Trim();
        if (t.Length == 0)
        {
            error = "the response is empty";
            return false;
        }

        string wrapped;
        if (t[0] == '[') wrapped = "{\"saves\":" + t + "}";
        else if (t[0] == '{' && t.IndexOf("\"saves\"", StringComparison.Ordinal) >= 0) wrapped = t;
        else
        {
            error = "the response is not a list of saves";
            return false;
        }

        try
        {
            var list = JsonUtility.FromJson<GameSaveList>(wrapped);
            saves = list?.saves ?? new GameSave[0];
        }
        catch (Exception e)
        {
            error = "the response could not be read (" + e.Message + ")";
            return false;
        }

        metas = new SaveMeta[saves.Length];
        try
        {
            var ml = JsonUtility.FromJson<GameSaveMetaList>(wrapped);
            if (ml?.saves != null)
            {
                for (int i = 0; i < saves.Length && i < ml.saves.Length; i++)
                {
                    var m = ml.saves[i]?.save_data?.mhs_meta;
                    metas[i] = m != null && m.HasTime ? m : null;
                }
            }
        }
        catch (Exception)
        {
            // Metadata is optional; the saves themselves were read.
        }
        return true;
    }

    public static bool IsValidUserId(string id)
    {
        if (id == null || id.Length != 24) return false;
        foreach (char c in id)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex) return false;
        }
        return true;
    }

    public static string JsonString(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ---- The request ----

    private IEnumerator SendCoroutine(string url, string auth, string body, bool isLoad, Action<RequestResult> onDone)
    {
        UnityWebRequest www = null;
        try
        {
            www = new UnityWebRequest(url, "POST");
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(auth)) www.SetRequestHeader("Authorization", auth);
            www.timeout = Math.Max(1, requestTimeoutSeconds);
        }
        catch (Exception e)
        {
            // A bad value from the host page (an auth header with a line break,
            // say) must not throw out of the caller's coroutine.
            www?.Dispose();
            Debug.LogWarning($"GameSaveManager: could not build the {(isLoad ? "load" : "save")} request for {url} ({e.Message}).");
            var bad = isLoad ? new LoadResult() : new RequestResult();
            bad.outcome = Outcome.Failed; bad.retryable = true; bad.error = e.Message; bad.reason = "the save service settings are not usable";
            Safe(() => onDone?.Invoke(bad));
            yield break;
        }

        using (www)
        {
            Print($"{(isLoad ? "Load" : "Save")} request to {url} ({body.Length} chars)");
            yield return www.SendWebRequest();

            RequestResult result = Classify(www, isLoad);
            if (result.Accepted) Print($"{(isLoad ? "Load" : "Save")} accepted: {result.statusCode}");
            else Print($"{(isLoad ? "Load" : "Save")} failed: {result.statusCode} {result.error} ({result.reason}; retryable {result.retryable})", ConsoleLogManager.ConsoleLogType.Warning);
            Safe(() => onDone?.Invoke(result));
        }
    }

    private static RequestResult Classify(UnityWebRequest www, bool isLoad)
    {
        RequestResult r = isLoad ? new LoadResult() : new RequestResult();
        r.statusCode = www.responseCode;
        r.error = www.error;
        r.body = www.downloadHandler != null ? www.downloadHandler.text : null;

        switch (www.result)
        {
            case UnityWebRequest.Result.Success:
                if (!isLoad)
                {
                    // The service answers a save with 201 and a JSON object (the stored
                    // document). Anything else with a 2xx (a captive portal's page, a
                    // filter's block page after a redirect) is not an acceptance: calling
                    // it one would mark the device copy as sent and lose the save.
                    string tb = (r.body ?? "").TrimStart();
                    if (tb.StartsWith("{") && tb.IndexOf("\"id\"", StringComparison.Ordinal) >= 0)
                    {
                        r.outcome = Outcome.Accepted;
                        return r;
                    }
                    r.outcome = Outcome.Failed;
                    r.retryable = true;
                    r.error = "unexpected response body";
                    r.reason = "the save service answered with something unreadable";
                    return r;
                }
                var lr = (LoadResult)r;
                if (TryParseLoadBody(r.body, out var saves, out var metas, out var parseError))
                {
                    lr.saves = saves;
                    lr.metas = metas;
                    lr.outcome = saves.Length > 0 ? Outcome.Accepted : Outcome.NoSaves;
                }
                else
                {
                    // A 200 with an HTML page from a proxy or a filter, for example.
                    lr.outcome = Outcome.Failed;
                    lr.retryable = true;
                    lr.error = parseError;
                    lr.reason = "the save service answered with something unreadable";
                }
                return r;

            case UnityWebRequest.Result.ConnectionError:
                r.outcome = Outcome.Failed;
                r.retryable = true;
                r.reason = (www.error ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "the save service did not answer in time"
                    : "no connection to the save service";
                return r;

            case UnityWebRequest.Result.DataProcessingError:
                r.outcome = Outcome.Failed;
                r.retryable = true;
                r.reason = "the save service's answer could not be read";
                return r;

            default: // ProtocolError: the service answered with an error status
                r.outcome = Outcome.Failed;
                long code = www.responseCode;
                if (code == 429)
                {
                    r.retryable = true;
                    r.reason = "the save service asked us to wait";
                    string ra = www.GetResponseHeader("Retry-After");
                    if (!string.IsNullOrEmpty(ra) && int.TryParse(ra.Trim(), out int seconds)) r.retryAfterSeconds = Mathf.Clamp(seconds, 1f, 300f);
                }
                else if (code == 401 || code == 403)
                {
                    r.retryable = true;
                    r.reason = "the save service refused this game's access key";
                }
                else if (code == 408 || code >= 500)
                {
                    r.retryable = true;
                    r.reason = "the save service had an error";
                }
                else if (code == 404 || code == 405)
                {
                    r.retryable = true;
                    r.reason = "the save service address is not answering";
                }
                else if (code == 413)
                {
                    r.retryable = false;
                    r.reason = "the save is too large for the save service";
                }
                else
                {
                    // 400 and other client errors: the request itself was rejected;
                    // sending the same thing again cannot succeed.
                    r.retryable = false;
                    r.reason = "the save service rejected the request";
                }
                return r;
        }
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    private bool DebugLogsEnabled => enableDebugLogs != null && enableDebugLogs.Value;

    private void Print(string message, ConsoleLogManager.ConsoleLogType type = ConsoleLogManager.ConsoleLogType.Standard)
    {
        var console = ConsoleLogManager.Instance;
        if (console != null) console.PrintToConsole("GameSaveManager: " + message, DebugLogsEnabled, type);
    }
}
