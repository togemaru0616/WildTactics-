using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using System.Collections;

// Central orchestrator for all phase transitions.
// Owns the cleanup-then-initialize lifecycle so every transition is equivalent
// to a fresh boot: full destroy sweep → one frame gap → fresh init.
public class PhaseController : MonoBehaviour
{
    public static PhaseController Instance { get; private set; }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ---- public API ----

    public void GoToGame(bool isWifi = false)
    {
        StopAllCoroutines();
        StartCoroutine(DoTransition(isWifi, toTitle: false));
    }

    public void GoToTitle()
    {
        StopAllCoroutines();
        StartCoroutine(DoTransition(isWifi: false, toTitle: true));
    }

    // ---- transition coroutine ----

    IEnumerator DoTransition(bool isWifi, bool toTitle)
    {
        Time.timeScale = 1f;
        if (!isWifi && OnlineManager.Instance != null)
            OnlineManager.Instance.Disconnect();
        CleanupAll(keepOnline: isWifi);
        yield return null;   // wait one frame for Object.Destroy to flush

        EnsureEventSystem();

        if (toTitle)
        {
            var camGo = new GameObject("TitleCamera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            new GameObject("TitleManager").AddComponent<TitleManager>();
        }
        else
        {
            GameSetup.StartGame(isWifi);
        }
    }

    // ---- helpers ----

    void CleanupAll(bool keepOnline)
    {
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go == null || go == gameObject) continue;
            if (go.GetComponent<SoundManager>()        != null) continue;
            if (go.GetComponent<Photon.Pun.PhotonHandler>() != null) continue;
            if (keepOnline && go.GetComponent<OnlineManager>() != null) continue;
            Object.Destroy(go);
        }
    }

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var esGo = new GameObject("EventSystem");
        esGo.AddComponent<EventSystem>();
        esGo.AddComponent<InputSystemUIInputModule>();
    }
}
