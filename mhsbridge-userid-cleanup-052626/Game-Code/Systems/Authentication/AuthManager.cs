using UnityEngine;
using TMPro;
using Obvious.Soap;
using UnityEngine.SceneManagement;

namespace MHS
{
    public class AuthManager : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI statusText;  // Drag/assign in Inspector

        // NOTE on naming: the two BindableVariable assets referenced below are
        // still named "playerId" and "playerName" in the Unity project. The
        // string VALUES they carry are now the user_id (24-char hex) and the
        // user's display name. Renaming the ScriptableObject assets is a
        // follow-up — see the top-level DIRECTIONS.md.
        [SerializeField] private StringVariable playerId;
        [SerializeField] private StringVariable playerName;

        [Tooltip("If set, this will override the user ID with a specific value.\nMust be a 24-char lowercase hex string (a stratahub.users ObjectID).\nLeave blank if you want to start clean in U1.")]
        [SerializeField] public string playerOverrideId;

        [SerializeField] private BoolVariable printConsoleLogs;

        [SerializeField] private ScriptableEventNoParam loadSettings;

        private IdentityBridge identityBridge;

        private DatabaseSaver dbSaver;

        // How often to check user identity (seconds)
        [SerializeField]
        private float checkInterval = 5f;

        private void Start()
        {
            identityBridge = FindFirstObjectByType<IdentityBridge>();
            dbSaver = FindFirstObjectByType<DatabaseSaver>();

            if (identityBridge == null)
            {
                ConsoleLogManager.Instance.PrintToConsole("No IdentityBridge found in the scene!", printConsoleLogs.Value);
                if (statusText != null) statusText.text = "No IdentityBridge found!";
                return;
            }

            InvokeRepeating(nameof(CheckUserIdentityRepeatedly), 0f, checkInterval);
        }

        private void CheckUserIdentityRepeatedly()
        {
#if UNITY_EDITOR
            if (playerOverrideId != "")
            {
                ConsoleLogManager.Instance.PrintToConsole("Using user override ID: " + playerOverrideId, printConsoleLogs.Value);
                playerName.Value = "Test Player";
                playerId.Value = playerOverrideId;
                GameLogger.Instance.SetUserId(playerOverrideId);
                if (statusText != null)
                {
                    statusText.text = $"Authenticated\n\nName: {playerName.Value}\nUser ID: {playerId.Value}";
                }
                return;
            }
#endif
            StartCoroutine(identityBridge.FetchUserIdentityAsync(OnUserIdentityFetched));
        }

        private void OnUserIdentityFetched(UserIdentity user)
        {
            if (user != null)
            {
                var oldPlayerId = playerId.Value;
                ConsoleLogManager.Instance.PrintToConsole("User is authenticated", printConsoleLogs.Value);
                ConsoleLogManager.Instance.PrintToConsole("User Name: " + user.name, printConsoleLogs.Value);
                ConsoleLogManager.Instance.PrintToConsole("User ID: " + user.user_id, printConsoleLogs.Value);
                playerName.Value = user.name;
                playerId.Value = user.user_id;
                GameLogger.Instance.SetUserId(user.user_id);

                if (SceneManager.GetActiveScene().name != SceneNameReference.CharacterCustomization)
                    loadSettings.Raise();
                // Only apply saved progress once per user authentication
                if (dbSaver != null && oldPlayerId != playerId.Value)
                {
                    dbSaver.InitializeDataAfterSceneLoaded(SceneManager.GetActiveScene());
                }

                if (statusText != null)
                {
                    statusText.text = $"Authenticated\n\nName: {user.name}\nUser ID: {user.user_id}";
                }
            }
            else
            {
                ConsoleLogManager.Instance.PrintToConsole("User is not authenticated or an error occurred.", printConsoleLogs.Value);

                if (statusText != null)
                {
                    statusText.text = "User is not authenticated (error, 404, etc.)";
                }
            }
        }
    }
}
