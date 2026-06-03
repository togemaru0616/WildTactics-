using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.IO;

public static class GameSetup
{
    static void SetupCamera(GameObject go)
    {
        var cam = go.GetComponent<Camera>();
        cam.allowMSAA        = false;
        cam.allowHDR         = false;
        cam.depthTextureMode = DepthTextureMode.None;

        var d = go.GetComponent<UniversalAdditionalCameraData>()
               ?? go.AddComponent<UniversalAdditionalCameraData>();
        d.renderShadows        = false;
        d.requiresDepthTexture = false;
        d.requiresColorTexture = false;
        d.renderPostProcessing = false;
        d.antialiasing         = AntialiasingMode.None;
        d.stopNaN              = false;
        d.dithering            = false;
        d.volumeLayerMask      = 0;
    }

    public static AIWeightSet LoadBestWeights()
    {
        // 学習済み重みをローカルから優先ロード（エディタ・開発用）
        string path = Path.Combine(Application.persistentDataPath, "best_weights.json");
        if (File.Exists(path))
            return JsonUtility.FromJson<AIWeightSet>(File.ReadAllText(path));

        // WebGL等でローカルファイルが存在しない場合はビルド同梱版を使用
        var asset = Resources.Load<TextAsset>("best_weights");
        if (asset != null)
            return JsonUtility.FromJson<AIWeightSet>(asset.text);

        return new AIWeightSet();
    }

    public static void StartGame(bool isWifi = false)
    {
        Time.timeScale = 1f;

        if (isWifi)
        {
            PlacementManager.PlacingOwner = OnlineManager.LocalPlayer;
            OnlineGame.Instance?.Reset();
        }
        else
        {
            PlacementManager.PlacingOwner = 1;
            GridManager.TerrainSeed = (SavedMapsManager.UseSelectedMap && SavedMapsManager.Seeds.Count > 0)
                ? SavedMapsManager.GetRandom()
                : -1;
        }

        // シーンに残っている旧インスタンスを先に破棄（OnlineManager/OnlineGame は残す）
        foreach (var m in Object.FindObjectsByType<GridManager>     (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<GameManager>     (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<UnitManager>     (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<FogManager>      (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<OutpostManager>  (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<PlacementManager>(FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<InputHandler>    (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);
        foreach (var m in Object.FindObjectsByType<AICommander>     (FindObjectsSortMode.None)) Object.Destroy(m.gameObject);

        var gmGo = new GameObject("GameManager");
        gmGo.AddComponent<GameManager>();

        var gridGo = new GameObject("GridManager");
        gridGo.AddComponent<GridManager>();

        var unitGo = new GameObject("UnitManager");
        unitGo.AddComponent<UnitManager>();

        var fogGo = new GameObject("FogManager");
        var fog   = fogGo.AddComponent<FogManager>();
        if (isWifi) fog.ViewingPlayer = OnlineManager.LocalPlayer;

        var outpostGo = new GameObject("OutpostManager");
        outpostGo.AddComponent<OutpostManager>();

        var placementGo = new GameObject("PlacementManager");
        placementGo.AddComponent<PlacementManager>();
        SoundManager.Placement();

        var inputGo = new GameObject("InputHandler");
        inputGo.AddComponent<InputHandler>();

        // WiFiモードではAIを使わない（P2は人間）
        if (!isWifi)
        {
            var aiGo = new GameObject("AICommander");
            var ai   = aiGo.AddComponent<AICommander>();
            ai.Init(2, LoadBestWeights());
        }

        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            Object.Destroy(cam.gameObject);

        var placementCamGo = new GameObject("PlacementCamera");
        placementCamGo.tag = "MainCamera";
        placementCamGo.AddComponent<Camera>();
        placementCamGo.AddComponent<AudioListener>();
        bool isP2 = isWifi && !OnlineManager.IsHost;
        placementCamGo.AddComponent<GameCamera>().Init(isP2 ? 0f : 0.22f, isP2 ? 0.22f : 0f);
        SetupCamera(placementCamGo);

        var battleCamGo = new GameObject("BattleCamera");
        battleCamGo.AddComponent<Camera>();
        battleCamGo.AddComponent<GameCamera>().Init(0f, 0f);
        SetupCamera(battleCamGo);
        battleCamGo.SetActive(false);

        gmGo.GetComponent<GameManager>().SetCameras(placementCamGo, battleCamGo);

    }
}
