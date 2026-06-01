using UnityEngine;

// シーンに何も置かなくてよい。ゲーム起動時に自動で全 GameObject を生成する
public class SceneSetup : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<TitleManager>() != null) return;

        if (FindFirstObjectByType<SoundManager>() == null)
            new GameObject("SoundManager").AddComponent<SoundManager>();

        if (FindFirstObjectByType<PhaseController>() == null)
            new GameObject("PhaseController").AddComponent<PhaseController>();

        // EventSystem
        var esGo = new GameObject("EventSystem");
        esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
        esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

        // タイトル画面カメラ
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            Object.Destroy(cam.gameObject);
        var titleCam = new GameObject("TitleCamera");
        titleCam.tag = "MainCamera";
        titleCam.AddComponent<Camera>();
        titleCam.AddComponent<AudioListener>();

        // タイトルマネージャー
        var titleGo = new GameObject("TitleManager");
        titleGo.AddComponent<TitleManager>();
    }
}
