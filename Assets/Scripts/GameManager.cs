using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using System.Collections;

[DefaultExecutionOrder(-99)]
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    public GamePhase Phase      { get; private set; } = GamePhase.Placement;
    public int        LastWinner       { get; private set; } = -1;
    public AnimalType LastFoxKillerType { get; private set; } = AnimalType.Fox;

    public static float BattleTimeLimit = 300f;

    GameObject _placementCamera;
    GameObject _battleCamera;

    GameObject _battleTimerGo;
    Text       _battleTimerText;

    void Awake() => Instance = this;

    public void SetCameras(GameObject placement, GameObject battle)
    {
        _placementCamera = placement;
        _battleCamera    = battle;
    }

    public void StartBattle()
    {
        SoundManager.Explore();
        Phase = GamePhase.Battle;
        PlacementManager.Instance.HideUI();
        _placementCamera.tag = "Untagged";
        _placementCamera.SetActive(false);
        _battleCamera.tag = "MainCamera";
        _battleCamera.SetActive(true);
        StartCoroutine(BattleTimer());
        if (!AnimalUnit.SimMode) CreateTimerUI();
    }

    void CreateTimerUI()
    {
        // バトルタイマー（常時表示）
        _battleTimerGo = new GameObject("BattleTimerCanvas");
        var btCanvas = _battleTimerGo.AddComponent<Canvas>();
        btCanvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        btCanvas.sortingOrder = 58;
        var btScaler = _battleTimerGo.AddComponent<CanvasScaler>();
        btScaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        btScaler.referenceResolution = new Vector2(750f, 1334f);

        var btBg = new GameObject("BG");
        btBg.transform.SetParent(_battleTimerGo.transform, false);
        btBg.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.60f);
        var btBgRt = btBg.GetComponent<RectTransform>();
        btBgRt.anchorMin = new Vector2(0.38f, 0.93f); btBgRt.anchorMax = new Vector2(0.62f, 0.99f);
        btBgRt.offsetMin = btBgRt.offsetMax = Vector2.zero;

        var btTGo = new GameObject("BattleTimerText");
        btTGo.transform.SetParent(_battleTimerGo.transform, false);
        _battleTimerText = btTGo.AddComponent<Text>();
        _battleTimerText.font      = UIAssetTable.Instance.font;
        _battleTimerText.fontSize  = 44;
        _battleTimerText.color     = Color.white;
        _battleTimerText.alignment = TextAnchor.MiddleCenter;
        var btTRt = btTGo.GetComponent<RectTransform>();
        btTRt.anchorMin = new Vector2(0.38f, 0.93f); btTRt.anchorMax = new Vector2(0.62f, 0.99f);
        btTRt.offsetMin = btTRt.offsetMax = Vector2.zero;
    }

    // シミュレーション専用（カメラ・UIなし）
    public void StartBattleSimMode()
    {
        LastWinner = -1;
        Phase      = GamePhase.Battle;
        StartCoroutine(BattleTimer());
    }

    // ---- 勝敗判定 ----

    public void NotifyFoxDeath(int losingOwner, AnimalType killerType, Vector3 foxWorldPos)
    {
        if (Phase != GamePhase.Battle) return;
        LastFoxKillerType = killerType;
        int winner = losingOwner == 1 ? 2 : 1;
        if (OnlineManager.IsOnline) OnlineGame.Instance?.SendGameEnd(winner);
        StartCoroutine(FoxDeathSequence(winner, $"P{losingOwner} の狐が倒されました", foxWorldPos));
    }

    IEnumerator FoxDeathSequence(int winner, string reason, Vector3 foxPos)
    {
        // バックグラウンドでビデオをダウンロード開始（timeScaleに影響されない）
        string videoPath = WebUtils.StreamingAssetsUrl + "/Videos/" + LastFoxKillerType + ".mp4";
        var preloadVp = gameObject.AddComponent<VideoPlayer>();
        preloadVp.url = videoPath;
        preloadVp.Prepare();

        // スロー演出＋カメラズーム
        Time.timeScale = 0.15f;
        var cam = FindAnyObjectByType<GameCamera>();
        if (cam != null) StartCoroutine(cam.ZoomTo(foxPos, cam.OrthoSize * 0.35f, 3f));

        // 実時間で3秒待つ（この間にビデオのバッファリングが完了する）
        yield return new WaitForSecondsRealtime(3f);

        Time.timeScale = 1f;
        Destroy(preloadVp);
        EndGame(winner, reason);
    }

    // 相手端末からのゲーム終了通知
    public void NotifyRemoteGameEnd(int winner, string reason)
    {
        if (Phase != GamePhase.Battle) return;
        EndGame(winner, reason);
    }

    IEnumerator BattleTimer()
    {
        if (AnimalUnit.SimMode) yield break;

        if (BattleTimeLimit <= 0f)
        {
            if (_battleTimerText != null) _battleTimerText.text = "∞";
            yield break;
        }

        float remaining = BattleTimeLimit;
        int lastSec = -1;
        while (remaining > 0f && Phase == GamePhase.Battle)
        {
            if (_battleTimerText != null)
            {
                int sec = Mathf.CeilToInt(Mathf.Max(0f, remaining));
                if (sec != lastSec)
                {
                    lastSec = sec;
                    _battleTimerText.text  = $"{sec / 60}:{sec % 60:D2}";
                    _battleTimerText.color = remaining <= 30f ? new Color(1f, 0.3f, 0.3f) : Color.white;
                }
            }
            remaining -= Time.deltaTime;
            yield return null;
        }

        if (_battleTimerGo != null) _battleTimerGo.SetActive(false);
        if (Phase != GamePhase.Battle) yield break;

        // VS CPU: 時間切れ即敗北（P2/AI勝利）
        if (!OnlineManager.IsOnline && AICommander.Instance != null)
        {
            EndGame(2, "時間切れ → CPU勝利");
            yield break;
        }

        // オンライン・ローカル2人: HP合計で勝者決定
        int hp1 = 0, hp2 = 0;
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(1))
            if (u != null && !u.IsDead) hp1 += u.CurrentHP;
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(2))
            if (u != null && !u.IsDead) hp2 += u.CurrentHP;

        int winner = hp1 > hp2 ? 1 : hp2 > hp1 ? 2 : 0;
        EndGame(winner, $"時間切れ（P1:{hp1} vs P2:{hp2}）");
    }

    void EndGame(int winner, string reason)
    {
        LastWinner = winner;
        Phase      = GamePhase.Placement;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[Game] {reason} → {(winner == 0 ? "引き分け" : $"P{winner} 勝利")}");
#endif
        SoundManager.Defeat();
        if (OnlineManager.IsOnline && winner != 0)
        {
            int    local = OnlineManager.LocalPlayer;
            string name  = UnityEngine.PlayerPrefs.GetString("player_name", "Player");
            bool   won   = winner == local;
            MatchHistoryManager.Save(new MatchRecord(name, won, System.DateTime.Now.ToString("yyyy/MM/dd")));
        }
        if (!AnimalUnit.SimMode) ShowResult(winner, reason);
    }

    // ---- 結果画面 ----

    void ShowResult(int winner, string reason)
    {
        var cGo = new GameObject("ResultCanvas");
        var canvas = cGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = cGo.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode         = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);
        cGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        // ① 黒背景（最背面）
        var blackBg = new GameObject("BlackBG");
        blackBg.transform.SetParent(cGo.transform, false);
        blackBg.AddComponent<Image>().color = Color.black;
        var blackRt = blackBg.GetComponent<RectTransform>();
        blackRt.anchorMin = Vector2.zero; blackRt.anchorMax = Vector2.one;
        blackRt.offsetMin = blackRt.offsetMax = Vector2.zero;

        bool localWon = OnlineManager.IsOnline
            ? winner == OnlineManager.LocalPlayer
            : winner == 1;

        // ② 動画（黒背景の上・画像の下）
        // Top=60px: 中心原点で center.y = 667-60-380/2 = 417, width=675(90%), height=380(16:9)
        string videoPath = WebUtils.StreamingAssetsUrl + "/Videos/" + LastFoxKillerType.ToString() + ".mp4";
        var videoGo = new GameObject("ResultVideo");
        videoGo.transform.SetParent(cGo.transform, false);
        var rawImg  = videoGo.AddComponent<RawImage>();
        var videoRt = videoGo.GetComponent<RectTransform>();
        videoRt.anchorMin        = new Vector2(0.5f, 0.5f);
        videoRt.anchorMax        = new Vector2(0.5f, 0.5f);
        videoRt.pivot            = new Vector2(0.5f, 0.5f);
        videoRt.anchoredPosition = new Vector2(0f, localWon ? 20f : 9f);
        videoRt.sizeDelta        = new Vector2(675f, 0f);
        var arf = videoGo.AddComponent<AspectRatioFitter>();
        arf.aspectMode  = AspectRatioFitter.AspectMode.WidthControlsHeight;
        arf.aspectRatio = 16f / 9f;
        var renderTex = new RenderTexture(1920, 1080, 0);
        rawImg.texture = renderTex;
        var vp = videoGo.AddComponent<VideoPlayer>();
        vp.renderMode    = VideoRenderMode.RenderTexture;
        vp.targetTexture = renderTex;
        vp.playOnAwake   = false;
        vp.isLooping     = false;
        vp.url = videoPath;
        vp.prepareCompleted += p =>
        {
            if (p.audioTrackCount > 0) SoundManager.MuteBgm(true);
            p.Play();
        };
        vp.loopPointReached += p =>
        {
            p.Pause();
            SoundManager.MuteBgm(false);
        };
        vp.Prepare();

        // ③ VICTORY / DEFEAT 画像（フレーム部分が透明→動画が透けて見える）
        var overlay = new GameObject("Overlay");
        overlay.transform.SetParent(cGo.transform, false);
        var bgTex = localWon ? UIAssetTable.Instance.victory : UIAssetTable.Instance.defeat;
        if (bgTex != null)
        {
            var bgRaw = overlay.AddComponent<RawImage>();
            bgRaw.texture = bgTex;
        }
        else
        {
            overlay.AddComponent<Image>().color = Color.clear;
        }
        var overlayRt = overlay.GetComponent<RectTransform>();
        overlayRt.anchorMin = Vector2.zero; overlayRt.anchorMax = Vector2.one;
        overlayRt.offsetMin = overlayRt.offsetMax = Vector2.zero;

        // ④ ボタン（最前面、絶対ピクセル値・中心原点）
        var retryBtn = MakeResultButton(cGo.transform, "Retry", -188f, -540f, 315f, 120f, Rematch);
        MakeResultButton(cGo.transform, "Title", 188f, -540f, 315f, 120f, ReturnToTitle);

        // ⑤ オンライン時：相手状態の監視
        if (OnlineManager.IsOnline)
        {
            // 無効時に Button の Image が暗転するよう disabledColor を設定
            var retryColors = retryBtn.colors;
            retryColors.disabledColor = new Color(0f, 0f, 0f, 0.65f);
            retryBtn.colors = retryColors;

            var notifGo = new GameObject("RematchNotif");
            notifGo.transform.SetParent(cGo.transform, false);
            var notifTxt = notifGo.AddComponent<Text>();
            notifTxt.font               = UIAssetTable.Instance.font;
            notifTxt.fontSize           = 30;
            notifTxt.color              = new Color(1f, 0.9f, 0.3f, 1f);
            notifTxt.alignment          = TextAnchor.MiddleCenter;
            notifTxt.horizontalOverflow = HorizontalWrapMode.Wrap;
            var notifRt = notifGo.GetComponent<RectTransform>();
            notifRt.anchorMin        = new Vector2(0.05f, 0.5f);
            notifRt.anchorMax        = new Vector2(0.95f, 0.5f);
            notifRt.pivot            = new Vector2(0.5f, 1f);
            notifRt.anchoredPosition = new Vector2(0f, -410f);
            notifRt.sizeDelta        = new Vector2(0f, 60f);
            StartCoroutine(WatchRemoteRematch(notifTxt, retryBtn));
        }
    }

    IEnumerator WatchRemoteRematch(Text notifText, Button retryBtn)
    {
        while (notifText != null)
        {
            if (OnlineGame.RemoteGoesToTitle || !OnlineManager.IsConnected)
            {
                // 相手がTitleに戻った or 切断 → Retryを無効化
                retryBtn.interactable = false;
                notifText.text = "相手がタイトルに戻りました";
            }
            else if (OnlineGame.RemoteWantsRematch)
            {
                // 相手がRetryを押した → 通知表示（こちらのRetryは既に有効）
                notifText.text = "相手がリマッチを希望しています";
            }
            yield return new WaitForSeconds(0.3f);
        }
    }

    Button MakeResultButton(Transform parent, string name, float cx, float cy, float w, float h,
                            UnityEngine.Events.UnityAction action)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = Color.white;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor      = Color.clear;
        colors.highlightedColor = new Color(0f, 0f, 0f, 0.10f);
        colors.pressedColor     = new Color(0f, 0f, 0f, 0.35f);
        colors.selectedColor    = Color.clear;
        colors.fadeDuration     = 0.08f;
        btn.colors = colors;
        btn.onClick.AddListener(() => { SoundManager.Tap(); action?.Invoke(); });
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(cx, cy);
        rt.sizeDelta        = new Vector2(w, h);
        return btn;
    }

    void Rematch()
    {
        if (OnlineManager.IsOnline)
            OnlineGame.Instance?.RequestRematch();
        else
            PhaseController.Instance?.GoToGame(isWifi: false);
    }

    void MakeText(Transform parent, string txt, int size, Vector2 ancMin, Vector2 ancMax)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font      = UIAssetTable.Instance.font;
        t.text      = txt;
        t.fontSize  = size;
        t.color     = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    void ReturnToTitle()
    {
        if (OnlineManager.IsOnline)
            OnlineGame.Instance?.RequestGoToTitle();
        else
            PhaseController.Instance?.GoToTitle();
    }
}
