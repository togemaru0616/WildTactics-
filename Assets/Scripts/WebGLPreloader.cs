using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

// WebGL 起動時にアセットテーブル・Prefab・シェーダー・動画を先読みする。
// [RuntimeInitializeOnLoadMethod] によりシーンロード前に実行される。
public static class WebGLPreloader
{
#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Preload()
    {
        // ScriptableObject テーブルを全て即時ロード
        // （参照しているテクスチャ・スプライト・シェーダー資産も同時にロードされる）
        _ = UIAssetTable.Instance;
        _ = TileAssetTable.Instance;
        _ = SoundAssetTable.Instance;
        _ = CardAssetTable.Instance;

        // 動物テーブルと全Prefabを先読み
        var animals = AnimalAssetTable.Instance;
        if (animals != null)
            foreach (var entry in animals.entries)
                _ = entry.prefab;

        // StreamingAssets の動画をバックグラウンドでプリフェッチ
        var go = new GameObject("VideoPreloader");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<VideoPreloadHelper>().StartPreload();
    }
#endif
}

// 動画の HTTP プリフェッチ（ブラウザキャッシュに乗せるだけでOK）
public class VideoPreloadHelper : MonoBehaviour
{
    static readonly string[] VideoNames =
    {
        "Bear","Boar","Chimpanzee","Elephant","Fox","Giraffe","Gorilla",
        "Hippo","Horse","Lion","Panda","Reindeer","Rhino","Snake","Tiger","Wolf"
    };

    public void StartPreload() => StartCoroutine(PreloadAll());

    IEnumerator PreloadAll()
    {
        foreach (var name in VideoNames)
        {
            string url = WebUtils.StreamingAssetsUrl + "/Videos/" + name + ".mp4";
            using var req = UnityWebRequest.Head(url);
            yield return req.SendWebRequest();
        }
        Destroy(gameObject);
    }
}
