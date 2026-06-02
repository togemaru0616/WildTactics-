using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

// バトルフェーズの同期を担当。Photon RaiseEvent を使う（PhotonView 不要）。
// OnlineManager と同じ GameObject に AddComponent する → DontDestroyOnLoad で永続。
public class OnlineGame : MonoBehaviour, IOnEventCallback
{
    public static OnlineGame Instance { get; private set; }

    // イベントコード
    const byte EV_PLACEMENT       = 1;   // 自軍配置データ（JSON）
    const byte EV_MOVE            = 2;   // int[3]: netId, x, z
    const byte EV_LOCK            = 3;   // int[2]: unitNetId, targetNetId
    const byte EV_GAME_END        = 4;   // int: winner (1 or 2)
    const byte EV_REMATCH         = 5;   // int: 新テレインシード（P1→P2、確定後）
    const byte EV_DAMAGE          = 6;   // int[3]: targetNetId, damage, attackerNetId(-1=null)
    const byte EV_POISON          = 7;   // int[2]: targetNetId, turns
    const byte EV_REMATCH_REQUEST = 8;   // リマッチ希望を相手に通知
    const byte EV_GO_TITLE        = 9;   // タイトルへ帰還（相手を強制追従）

    static readonly RaiseEventOptions ToOthers = new() { Receivers = ReceiverGroup.Others };
    static readonly SendOptions       Reliable  = SendOptions.SendReliable;

    bool   _localReady;
    bool   _remoteReady;
    string _remotePlacementJson;

    bool _localRematchReady;
    bool _remoteRematchReady;
    Coroutine _keepAlive;

    // 相手がリマッチを希望しているか（結果画面で表示用）
    public static bool RemoteWantsRematch  { get; private set; }
    // 相手がタイトルに戻ったか（結果画面でRetryを無効化するため）
    public static bool RemoteGoesToTitle   { get; private set; }

    GameObject _loadingCanvas;
    System.Action _loadingCancelAction;

    // ---- Unity lifecycle ----

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnEnable()  => PhotonNetwork.AddCallbackTarget(this);
    void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

    // ---- ゲーム開始時にリセット ----

    public void Reset()
    {
        _localReady          = false;
        _remoteReady         = false;
        _remotePlacementJson = null;
        _localRematchReady   = false;
        _remoteRematchReady  = false;
        RemoteWantsRematch   = false;
        RemoteGoesToTitle    = false;
        HideLoading();

        // 配置フェーズ中のキープアライブ（OnlineManagerのキープアライブが止まった後を引き継ぐ）
        if (_keepAlive != null) StopCoroutine(_keepAlive);
        _keepAlive = StartCoroutine(PlacementKeepAlive());
    }

    // ---- 配置フェーズ ----

    // PlacementManager.OnStartBattle() から呼ぶ
    public void LocalReady()
    {
        _localReady = true;
        Debug.Log($"[OnlineGame] LocalReady local={_localReady} remote={_remoteReady}");
        ShowLoading();

        // 自軍ユニット（Fox含む）の配置データをJSON送信
        int local = OnlineManager.LocalPlayer;
        var entries = new List<PlacementEntry>();
        foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(local))
        {
            entries.Add(new PlacementEntry
            {
                netId = unit.NetworkId,
                type  = (int)unit.AnimalType,
                x     = unit.GridPos.x,
                z     = unit.GridPos.y
            });
        }
        string json = JsonUtility.ToJson(new PlacementWrapper { entries = entries });
        PhotonNetwork.RaiseEvent(EV_PLACEMENT, json, ToOthers, Reliable);

        CheckBothReady();
    }

    // ---- バトルフェーズ ----

    public void SendMoveTo(int netId, int x, int z)
        => PhotonNetwork.RaiseEvent(EV_MOVE, new int[] { netId, x, z }, ToOthers, Reliable);

    public void SendLockTarget(int unitNetId, int targetNetId)
        => PhotonNetwork.RaiseEvent(EV_LOCK, new int[] { unitNetId, targetNetId }, ToOthers, Reliable);

    public void SendGameEnd(int winner)
        => PhotonNetwork.RaiseEvent(EV_GAME_END, winner, ToOthers, Reliable);

    // P1 がリマッチを開始するときに呼ぶ（新シードを P2 へ送信）
    public void SendRematch(int seed)
        => PhotonNetwork.RaiseEvent(EV_REMATCH, seed, ToOthers, Reliable);

    // 結果画面で「Retry」ボタンを押したとき（両プレイヤー共通）
    public void RequestRematch()
    {
        _localRematchReady = true;
        PhotonNetwork.RaiseEvent(EV_REMATCH_REQUEST, null, ToOthers, Reliable);
        ShowLoading(CancelRematch);
        TryStartRematch();
    }

    // リマッチ待機中にキャンセルを押したとき
    public void CancelRematch()
    {
        _localRematchReady  = false;
        _remoteRematchReady = false;
        PhotonNetwork.RaiseEvent(EV_GO_TITLE, null, ToOthers, Reliable);
        HideLoading();
        PhaseController.Instance?.GoToTitle();
    }

    // 「Title」ボタンを押したとき（相手も強制タイトル帰還）
    public void RequestGoToTitle()
    {
        PhotonNetwork.RaiseEvent(EV_GO_TITLE, null, ToOthers, Reliable);
        HideLoading();
        PhaseController.Instance?.GoToTitle();
    }

    // P1 のみ: 両者 Ready になったらリマッチ開始
    void TryStartRematch()
    {
        if (!OnlineManager.IsHost || !_localRematchReady || !_remoteRematchReady) return;
        int seed = UnityEngine.Random.Range(1, int.MaxValue);
        GridManager.TerrainSeed = seed;
        SendRematch(seed);
        HideLoading();
        PhaseController.Instance?.GoToGame(isWifi: true);
    }

    // attackerNetId = -1 なら null attacker
    public void SendDamage(int targetNetId, int damage, int attackerNetId = -1)
        => PhotonNetwork.RaiseEvent(EV_DAMAGE, new int[] { targetNetId, damage, attackerNetId }, ToOthers, Reliable);

    public void SendPoison(int targetNetId, int turns)
        => PhotonNetwork.RaiseEvent(EV_POISON, new int[] { targetNetId, turns }, ToOthers, Reliable);

    // ---- Photon イベント受信 ----

    public void OnEvent(EventData ev)
    {
        if (ev.Code == 99) return; // keepalive ping

        switch (ev.Code)
        {
            case EV_PLACEMENT:
                _remotePlacementJson = (string)ev.CustomData;
                _remoteReady = true;
                Debug.Log($"[OnlineGame] EV_PLACEMENT received local={_localReady} remote={_remoteReady}");
                CheckBothReady();
                break;

            case EV_MOVE:
                var ma = (int[])ev.CustomData;
                UnitManager.Instance?.GetUnitByNetId(ma[0])?.MoveTo(new Vector2Int(ma[1], ma[2]));
                break;

            case EV_LOCK:
                var la = (int[])ev.CustomData;
                var u  = UnitManager.Instance?.GetUnitByNetId(la[0]);
                var t  = UnitManager.Instance?.GetUnitByNetId(la[1]);
                if (u != null && t != null) u.LockTarget(t);
                break;

            case EV_GAME_END:
                GameManager.Instance?.NotifyRemoteGameEnd((int)ev.CustomData, "相手端末でゲーム終了");
                break;

            case EV_REMATCH:
                GridManager.TerrainSeed = (int)ev.CustomData;
                HideLoading();
                PhaseController.Instance?.GoToGame(isWifi: true);
                break;

            case EV_REMATCH_REQUEST:
                _remoteRematchReady = true;
                RemoteWantsRematch  = true;
                TryStartRematch();
                break;

            case EV_GO_TITLE:
                HideLoading();
                if (_localRematchReady)
                {
                    // こちらがRetryを押して待機中 → 即タイトルへ
                    _localRematchReady = false;
                    PhaseController.Instance?.GoToTitle();
                }
                else
                {
                    // まだRetryを押していない → Retryを無効化するだけ（強制移動なし）
                    RemoteGoesToTitle = true;
                }
                break;

            case EV_DAMAGE:
                var da  = (int[])ev.CustomData;
                var tgt = UnitManager.Instance?.GetUnitByNetId(da[0]);
                var atk = da[2] >= 0 ? UnitManager.Instance?.GetUnitByNetId(da[2]) : null;
                if (atk != null) atk.PlayRemoteAttack(tgt, da[1]);
                else             tgt?.TakeDamage(da[1], null);
                break;

            case EV_POISON:
                var pa = (int[])ev.CustomData;
                UnitManager.Instance?.GetUnitByNetId(pa[0])?.ApplyPoison(pa[1]);
                break;
        }
    }

    // ---- 内部 ----

    IEnumerator ForcedTitleCountdown()
    {
        var cGo = new GameObject("ForcedTitleCanvas");
        DontDestroyOnLoad(cGo);
        var canvas = cGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 201;
        var scaler = cGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);

        var bgGo = new GameObject("Bg");
        bgGo.transform.SetParent(cGo.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.78f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var txtGo = new GameObject("Txt");
        txtGo.transform.SetParent(cGo.transform, false);
        var txt = txtGo.AddComponent<Text>();
        txt.font               = UIAssetTable.Instance.font;
        txt.fontSize           = 38;
        txt.color              = Color.white;
        txt.alignment          = TextAnchor.MiddleCenter;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        var txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.anchorMin = new Vector2(0.05f, 0.35f);
        txtRt.anchorMax = new Vector2(0.95f, 0.65f);
        txtRt.offsetMin = txtRt.offsetMax = Vector2.zero;

        for (int i = 5; i > 0; i--)
        {
            txt.text = $"相手がタイトルに戻りました\n{i}秒後にタイトルへ移動します";
            yield return new WaitForSeconds(1f);
        }

        Destroy(cGo);
        PhaseController.Instance?.GoToTitle();
    }

    IEnumerator PlacementKeepAlive()
    {
        var wait = new WaitForSeconds(5f);
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
        while (!_localReady || !_remoteReady)
        {
            PhotonNetwork.RaiseEvent(99, null, opts, SendOptions.SendUnreliable);
            yield return wait;
        }
        _keepAlive = null;
    }

    void CheckBothReady()
    {
        Debug.Log($"[OnlineGame] CheckBothReady local={_localReady} remote={_remoteReady} GM={GameManager.Instance != null}");
        if (!_localReady || !_remoteReady) return;

        if (_keepAlive != null) { StopCoroutine(_keepAlive); _keepAlive = null; }
        HideLoading();
        SpawnRemoteUnits(_remotePlacementJson);
        GameManager.Instance?.StartBattle();
    }

    void SpawnRemoteUnits(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        var wrapper    = JsonUtility.FromJson<PlacementWrapper>(json);
        int remoteOwner = OnlineManager.LocalPlayer == 1 ? 2 : 1;
        foreach (var e in wrapper.entries)
        {
            // Fox は自機が PreplaceFox() で既に配置済み → 重複スキップ
            var existingFox = UnitManager.Instance.GetUnitByNetId(e.netId);
            if (existingFox != null) continue;

            UnitManager.Instance.SpawnUnit(
                (AnimalType)e.type, remoteOwner, new Vector2Int(e.x, e.z), e.netId);
        }
    }

    // ---- ローディング画面 ----

    void ShowLoading(System.Action onCancel = null)
    {
        if (_loadingCanvas != null) return;
        _loadingCancelAction = onCancel;

        _loadingCanvas = new GameObject("OnlineLoadingCanvas");
        DontDestroyOnLoad(_loadingCanvas);
        var canvas = _loadingCanvas.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = _loadingCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);

        var imgGo = new GameObject("LoadingImage");
        imgGo.transform.SetParent(_loadingCanvas.transform, false);
        var raw = imgGo.AddComponent<RawImage>();
        var tex = UIAssetTable.Instance.loadingScreen;
        if (tex != null) raw.texture = tex;
        else             raw.color   = new Color(0f, 0f, 0f, 0.85f);
        var rt = imgGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        if (onCancel != null)
        {
            var cancelGo  = new GameObject("CancelBtn");
            cancelGo.transform.SetParent(_loadingCanvas.transform, false);
            var cancelImg = cancelGo.AddComponent<Image>();
            cancelImg.color = new Color(0.45f, 0.12f, 0.12f, 1f);
            var cancelRt  = cancelGo.GetComponent<RectTransform>();
            cancelRt.anchorMin        = new Vector2(0.5f, 0f);
            cancelRt.anchorMax        = new Vector2(0.5f, 0f);
            cancelRt.pivot            = new Vector2(0.5f, 0f);
            cancelRt.anchoredPosition = new Vector2(0f, 60f);
            cancelRt.sizeDelta        = new Vector2(200f, 56f);
            var cancelBtn = cancelGo.AddComponent<Button>();
            cancelBtn.targetGraphic = cancelImg;
            cancelBtn.onClick.AddListener(() => _loadingCancelAction?.Invoke());
            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(cancelGo.transform, false);
            var txt = txtGo.AddComponent<Text>();
            txt.font      = UIAssetTable.Instance.font;
            txt.text      = "キャンセル";
            txt.fontSize  = 30;
            txt.color     = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = txtRt.offsetMax = Vector2.zero;
        }
    }

    void HideLoading()
    {
        if (_loadingCanvas == null) return;
        Destroy(_loadingCanvas);
        _loadingCanvas = null;
    }

    // ---- データ型 ----

    [System.Serializable]
    public struct PlacementEntry
    {
        public int netId;
        public int type;
        public int x;
        public int z;
    }

    [System.Serializable]
    public class PlacementWrapper
    {
        public List<PlacementEntry> entries;
    }
}
