using UnityEngine;
using System;
using System.Collections;
using MHS;
using Obvious.Soap;
using System.Runtime.InteropServices;

[System.Serializable]
public class UserIdentity
{
    public bool isAuthenticated;
    public string name;
    // 24-char lowercase hex of the stratahub user's ObjectID.
    // Field name is snake_case to match the JSON shape returned by
    // stratahub's /api/user endpoint and the host page bridge config.
    public string user_id;
}

public class IdentityBridge : MonoBehaviour
{
    // This is used only on non-WebGL platforms
    private const string UserApiUrl = "https://adroit.games/api/user";
    UserIdentity editorUser;

    [SerializeField] private BoolVariable enableDebugLogs;
    [SerializeField] private BoolVariable enableDebugAuth;


    /// <summary>
    /// Fetches user identity asynchronously.
    ///
    /// Calls `callback(user)` if the user is actually authenticated.
    /// Otherwise calls `callback(null)`.
    ///
    /// Usage: StartCoroutine(FetchUserIdentityAsync(OnUserIdentityFetched));
    /// </summary>
    public IEnumerator FetchUserIdentityAsync(Action<UserIdentity> callback)
    {
#if UNITY_EDITOR
        //----------------------------
        // 1) Running in the Unity Editor => return dummy data
        //----------------------------
        ConsoleLogManager.Instance.PrintToConsole("Running in Editor: returning dummy data.", enableDebugLogs.Value);

        if (editorUser == null)
        {
            editorUser = new UserIdentity
            {
                isAuthenticated = true,
                name = MHSBridge.Instance.GetUserName(),
                user_id = MHSBridge.Instance.GetUserID()
            };
        }
        callback?.Invoke(editorUser);
        yield break;

#elif UNITY_WEBGL && !UNITY_EDITOR
        //----------------------------
        // 2) Running in WebGL => identity comes from MHSBridge
        //    (loaded from window.__mhsBridgeConfig at startup)
        //----------------------------
        ConsoleLogManager.Instance.PrintToConsole("Running in WebGL build: reading UserID from MHSBridge...", enableDebugLogs.Value);

        if (enableDebugAuth.Value)
        {
            ConsoleLogManager.Instance.PrintToConsole("DebugAuth Enabled: returning dummy data.", enableDebugLogs.Value);

            if (editorUser == null)
            {
                editorUser = new UserIdentity
                {
                    isAuthenticated = true,
                    name = "TestUser",
                    user_id = "000000000000000000000001"
                };
            }
            callback?.Invoke(editorUser);
            yield break;
        }

        string userID = MHSBridge.Instance.GetUserID();

        if (string.IsNullOrEmpty(userID))
        {
            ConsoleLogManager.Instance.PrintToConsole("MHSBridge returned no user_id (host page may be missing __mhsBridgeConfig).", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
            callback?.Invoke(null);
            yield break;
        }

        UserIdentity webGLUser = new UserIdentity
        {
            isAuthenticated = true,
            name = MHSBridge.Instance.GetUserName(),
            user_id = userID
        };

        // If isAuthenticated is false or name/user_id are empty, treat as not logged in
        if (webGLUser == null || !webGLUser.isAuthenticated)
        {
            ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] WebGL user is null or not authenticated.", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
            callback?.Invoke(null);
            yield break;
        }

        // Enforce non-empty name
        if (string.IsNullOrEmpty(webGLUser.name))
        {
            ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] WebGL user lacks a name.", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
            callback?.Invoke(null);
            yield break;
        }

        // At this point, the user is authenticated in WebGL
        callback?.Invoke(webGLUser);
        yield break;

#else
        //----------------------------
        // 3) Non-WebGL build => do a direct UnityWebRequest
        //----------------------------
        ConsoleLogManager.Instance.PrintToConsole("Running on non-WebGL platform, making web request to: " + UserApiUrl, enableDebugLogs.Value);

        using (UnityWebRequest request = UnityWebRequest.Get(UserApiUrl))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ProtocolError ||
                request.result == UnityWebRequest.Result.ConnectionError)
            {
                ConsoleLogManager.Instance.PrintToConsole($"[IdentityBridge] HTTP/Network error: {request.error} (Status: {request.responseCode})", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            if (request.responseCode != 200)
            {
                ConsoleLogManager.Instance.PrintToConsole($"[IdentityBridge] Non-200 status code: {request.responseCode}", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            string json = request.downloadHandler.text;
            if (string.IsNullOrEmpty(json))
            {
                ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] No data returned (empty JSON).", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            UserIdentity user = null;
            try
            {
                // /api/user returns { "isAuthenticated": ..., "name": ..., "user_id": ... }
                user = JsonUtility.FromJson<UserIdentity>(json);
            }
            catch (Exception e)
            {
                ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] JSON parsing error: " + e.Message, enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            if (user == null || !user.isAuthenticated)
            {
                ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] JSON parsed but user is not authenticated or is null.", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            if (string.IsNullOrEmpty(user.name))
            {
                ConsoleLogManager.Instance.PrintToConsole("[IdentityBridge] Authenticated user missing 'name' field.", enableDebugLogs.Value, ConsoleLogManager.ConsoleLogType.Warning);
                callback?.Invoke(null);
                yield break;
            }

            // Success => user is authenticated
            callback?.Invoke(user);
        }
#endif
    }
}
