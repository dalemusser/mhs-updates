using Obvious.Soap;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Lerps the value of the attached CanvasGroup.Alpha over the set length to fade in/out UI objects on command
[RequireComponent(typeof(CanvasGroup))]
public class UICanvasGroupFadeInOut : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float _fadeLength;

    [Header("Events")]
    [SerializeField] private UnityEvent _onFadeInComplete;
    [SerializeField] private UnityEvent _onFadeOutComplete;
    [SerializeField] private ScriptableEventNoParam _fadeInEvent;
    [SerializeField] private ScriptableEventNoParam _fadeOutEvent;
    [SerializeField] private ScriptableEventFloat _setOpacityEvent;
    [SerializeField] private bool fadeOnSceneLoad = true;


    private CanvasGroup _canvasGroup;

    private void Awake()
    {
        TryGetReferences();
        _fadeInEvent.OnRaised += FadeThisIn;
        _fadeOutEvent.OnRaised += FadeThisOut;
        _setOpacityEvent.OnRaised += SetCanvasGroupAlpha;

        SceneManager.sceneLoaded += SceneLoadFadeOut;
        SceneManager.sceneUnloaded += SceneUnloadFadeIn;
    }

    private void OnDestroy()
    {
        _fadeInEvent.OnRaised -= FadeThisIn;
        _fadeOutEvent.OnRaised -= FadeThisOut;
        _setOpacityEvent.OnRaised -= SetCanvasGroupAlpha;

        SceneManager.sceneLoaded -= SceneLoadFadeOut;
        SceneManager.sceneUnloaded -= SceneUnloadFadeIn;
    }

    private void SceneLoadFadeOut(Scene sceneName, LoadSceneMode sceneMode)
    {
        if (!fadeOnSceneLoad)
        {
            return;
        }
        if (sceneMode == LoadSceneMode.Additive)
        {
            return;
        }

        if (sceneName.path.Contains("Lesson"))
        {
            return;
        }

        // Don't want to disable image anymore - Ken
        // Image faderImage;
        // if (TryGetComponent<Image>(out faderImage))
        // {
        //     faderImage.enabled = true;
        // }

        Invoke(nameof(FadeThisOut), 1f);
    }

    private void SceneUnloadFadeIn(Scene sceneName)
    {
        if (!fadeOnSceneLoad)
        {
            return;
        }
        if (sceneName.path.Contains("Lesson"))
        {
            return;
        }

        // Image faderImage;
        // if (TryGetComponent<Image>(out faderImage))
        // {
        //     faderImage.enabled = true;
        // }

        FadeThisIn();
    }



    private void TryGetReferences()
    {
        if (_canvasGroup == null)
        {
            _canvasGroup = GetComponent<CanvasGroup>();
        }
    }

    [ContextMenu("Fade In")]
    public void FadeThisIn()
    {
        TryGetReferences();
        StopAllCoroutines();
        if (this.gameObject.activeInHierarchy)
        {
            StartCoroutine(LerpCanvasGroupAlpha(0, 1, _onFadeInComplete));
        }
    }

    [ContextMenu("Fade Out")]
    public void FadeThisOut()
    {
        TryGetReferences();
        StopAllCoroutines();
        if (this.gameObject.activeInHierarchy)
        {
            StartCoroutine(LerpCanvasGroupAlpha(1, 0, _onFadeOutComplete));
        }
    }

    // private async void CleanupWhileFaded()
    // {
    //     await Resources.UnloadUnusedAssets();
    // }

    private void SetCanvasGroupAlpha(float f)
    {
        //Debug.Log($"Setting canvas group alpha to {f}");
        if (!Mathf.Approximately(_canvasGroup.alpha, f))
        {
            _canvasGroup.alpha = f;
        }
    }

    private IEnumerator LerpCanvasGroupAlpha(float startAlpha, float endAlpha, UnityEvent unityEvent)
    {
        SetCanvasGroupAlpha(startAlpha); // Set start value for consistency - K

        float time = 0;

        while (time < _fadeLength)
        {
            SetCanvasGroupAlpha(Mathf.Lerp(startAlpha, endAlpha, time / _fadeLength));

            // Real time: a line set while the game is paused (Time.timeScale = 0) must
            // still appear (mhs-updates/gamesave-fix-102426).
            time += Time.unscaledDeltaTime;

            yield return null;
        }

        SetCanvasGroupAlpha(endAlpha);
        unityEvent?.Invoke();

        // This class probably doesn't need to handle this - K
        // if (Mathf.Approximately(endAlpha, 1))
        // {
        //     CleanupWhileFaded();
        // }
    }

}
