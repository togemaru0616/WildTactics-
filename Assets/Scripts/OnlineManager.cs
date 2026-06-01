using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

// Photon PUN2 接続管理
// 合言葉（keyword）で JoinOrCreateRoom → 先に入った方が MasterClient（ゲームロジック上のホスト）
// IsOnline / LocalPlayer は他スクリプトから static 参照
[DefaultExecutionOrder(-95)]
public class OnlineManager : MonoBehaviourPunCallbacks
{
    public static OnlineManager Instance { get; private set; }

    // --- 他スクリプトから参照される static 状態 ---
    public static bool IsOnline    = false;
    public static bool IsHost      = false;
    public static int  LocalPlayer = 1;   // 1 = MasterClient, 2 = 相手

    // P1の設定をP2に同期するための値（-1 = 未設定、ローカル設定を使用）
    public static int OnlineActiveLimit = -1;

    static float _savedBattleTime    = 0f;
    static float _savedPlacementTime = 0f;

    // --- UI 表示用 ---
    public string StatusText { get; private set; } = "";
    public bool   IsConnected { get; private set; } = false;

    // --- TitleManager が購読するイベント ---
    public event System.Action OnBothConnected;
    public event System.Action<string> OnConnectionFailed;

    string _pendingKeyword = "";
    Coroutine _keepAlive;

    // -------------------------------------------------------

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // -------------------------------------------------------
    //  公開 API
    // -------------------------------------------------------

    // keyword   : 合言葉（ルーム名になる）
    // playerName: Photon ニックネーム
    public void Connect(string keyword, string playerName)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;

        _pendingKeyword        = keyword.Trim().ToUpper();
        PhotonNetwork.NickName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        IsOnline               = true;
        IsConnected            = false;
        StatusText             = "接続中...";

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[OnlineManager] Connect kw={_pendingKeyword} IsConnected={PhotonNetwork.IsConnected} InRoom={PhotonNetwork.InRoom} Server={PhotonNetwork.Server}");
#endif

        if (!PhotonNetwork.IsConnected)
        {
            PhotonNetwork.ConnectUsingSettings();
        }
        else if (PhotonNetwork.InRoom)
        {
            StatusText = "前のルームを退出中...";
            PhotonNetwork.LeaveRoom();
        }
        else
        {
            PhotonNetwork.JoinLobby();
        }
    }

    public void Disconnect()
    {
        IsOnline    = false;
        IsConnected = false;
        IsHost      = false;
        LocalPlayer = 1;
        StatusText  = "";
        _pendingKeyword = "";

        // P2に適用した設定を元に戻す
        OnlineActiveLimit = -1;
        if (_savedBattleTime    > 0f) { GameManager.BattleTimeLimit = _savedBattleTime;    _savedBattleTime    = 0f; }
        if (_savedPlacementTime > 0f) { PlacementManager.TotalTime  = _savedPlacementTime; _savedPlacementTime = 0f; }

        if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect();
    }

    // -------------------------------------------------------
    //  Photon コールバック
    // -------------------------------------------------------

    public override void OnConnectedToMaster()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[OnlineManager] OnConnectedToMaster IsOnline={IsOnline}");
#endif
        if (!IsOnline) return;
        StatusText = "ロビーに接続中...";
        PhotonNetwork.JoinLobby();
    }

    public override void OnJoinedLobby()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[OnlineManager] OnJoinedLobby");
#endif
        JoinOrCreate();
    }

    public override void OnJoinedRoom()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[OnlineManager] OnJoinedRoom IsMasterClient={PhotonNetwork.IsMasterClient} PlayerCount={PhotonNetwork.CurrentRoom.PlayerCount}");
#endif
        IsHost      = PhotonNetwork.IsMasterClient;
        LocalPlayer = IsHost ? 1 : 2;

        if (IsHost)
        {
            // P1: テレインシードと自分の設定をルームプロパティに保存（P2が入室後に読む）
            int seed = Random.Range(1, int.MaxValue);
            GridManager.TerrainSeed = seed;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
            {
                ["seed"]           = seed,
                ["active_limit"]   = PlayerPrefs.GetInt("active_limit", 1),
                ["battle_time"]    = GameManager.BattleTimeLimit,
                ["placement_time"] = PlacementManager.TotalTime,
            });
            StatusText = "相手の接続を待っています...";
            _keepAlive = StartCoroutine(KeepAlive());
        }
        else
        {
            // P2: P1の設定を読み込んで適用
            ApplyRoomSettings();
            IsConnected = true;
            StatusText  = "接続済み！";
            OnBothConnected?.Invoke();
            PhaseController.Instance?.GoToGame(isWifi: true);
        }
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        StopKeepAlive();
        IsConnected = true;
        StatusText  = "接続済み！";
        OnBothConnected?.Invoke();
        PhaseController.Instance?.GoToGame(isWifi: true);
    }

    public override void OnRoomPropertiesUpdate(Hashtable changed)
    {
        // P2が入室前にプロパティが届く場合への備え（通常は OnJoinedRoom で取得できる）
        if (!IsHost) ApplyRoomSettings();
    }

    void ApplyRoomSettings()
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (props.TryGetValue("seed", out var s))
            GridManager.TerrainSeed = (int)s;
        if (props.TryGetValue("active_limit", out var al))
            OnlineActiveLimit = (int)al;
        if (props.TryGetValue("battle_time", out var bt))
        {
            _savedBattleTime        = GameManager.BattleTimeLimit;
            GameManager.BattleTimeLimit = (float)bt;
        }
        if (props.TryGetValue("placement_time", out var pt))
        {
            _savedPlacementTime    = PlacementManager.TotalTime;
            PlacementManager.TotalTime = (float)pt;
        }
    }

    public override void OnLeftRoom()
    {
        // GameServer → MasterServer に戻る → OnConnectedToMaster が続きを処理
        if (IsOnline) StatusText = "接続中...";
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (!IsConnected) return;
        IsConnected = false;
        StatusText  = "相手が切断しました";
    }

    public override void OnJoinRoomFailed(short returnCode, string message)
    {
        StatusText = $"接続失敗: {message}";
        IsConnected = false;
        IsOnline    = false;
        OnConnectionFailed?.Invoke(StatusText);
    }

    public override void OnCreateRoomFailed(short returnCode, string message)
    {
        StatusText = $"ルーム作成失敗: {message}";
        IsOnline   = false;
        OnConnectionFailed?.Invoke(StatusText);
    }

    public override void OnDisconnected(DisconnectCause cause)
    {
        StopKeepAlive();
        IsConnected = false;
        IsOnline    = false;
        if (cause != DisconnectCause.DisconnectByClientLogic)
            StatusText = $"切断: {cause}";
    }

    // -------------------------------------------------------
    //  内部ヘルパー
    // -------------------------------------------------------

    void StopKeepAlive()
    {
        if (_keepAlive == null) return;
        StopCoroutine(_keepAlive);
        _keepAlive = null;
    }

    IEnumerator KeepAlive()
    {
        var wait = new WaitForSeconds(5f);
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
        while (PhotonNetwork.InRoom)
        {
            PhotonNetwork.RaiseEvent(99, null, opts, SendOptions.SendUnreliable);
            yield return wait;
        }
    }

    void JoinOrCreate()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[OnlineManager] JoinOrCreate kw={_pendingKeyword}");
#endif
        StatusText = $"ルーム [{_pendingKeyword}] を検索中...";
        var opts = new RoomOptions { MaxPlayers = 2, IsVisible = false };
        PhotonNetwork.JoinOrCreateRoom(_pendingKeyword, opts, TypedLobby.Default);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
