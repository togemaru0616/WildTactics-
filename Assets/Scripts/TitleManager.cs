using UnityEngine;
using UnityEngine.UI;

public class TitleManager : MonoBehaviour
{
    Canvas _canvas;
    GameObject _settingsPanel;
    GameObject _historyPanel;
    GameObject _onlinePanel;
    InputField _passwordField;
    InputField _playerNameField;
    Text _onlineStatusText;
    GameObject _connectingCanvas;

    // 設定パネル用
    Button[] _sizeButtons;
    Button[] _limitButtons;
    Button[] _timeButtons;

    string _lastOnlineStatus;

    // マッププレビュー用
    GameObject _mapPreviewPanel;
    int _previewSeed;
    Image _minimapImage;
    Text _seedText;
    Text _savedCountText;
    Button _saveToggleBtn;
    Texture2D _currentMinimapTex;

    static readonly Color ColSelected = new Color(0.20f, 0.55f, 0.20f, 1f);
    static readonly Color ColNormal = new Color(0.20f, 0.20f, 0.35f, 1f);

    static readonly GridManager.GridSizePreset[] SizePresetOrder = {
        GridManager.GridSizePreset.Small,
        GridManager.GridSizePreset.Medium,
        GridManager.GridSizePreset.Standard,
    };

    void Start()
    {
        SoundManager.Title();
        BuildUI();
    }

    void Update()
    {
#if UNITY_EDITOR
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.pKey.wasPressedThisFrame)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var name in new[] { "Online", "VsCpu", "Record", "Settings" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                sb.AppendLine($"  (\"{name}\", cx:{rt.anchoredPosition.x:F0}, cy:{rt.anchoredPosition.y:F0}, w:{rt.sizeDelta.x:F0}, h:{rt.sizeDelta.y:F0})");
            }
            Debug.Log("=== ボタン位置 ===\n" + sb);
        }
#endif
        // OnlineManager ステータスをパネルに反映（変更時のみ更新）
        if (_onlineStatusText != null && OnlineManager.Instance != null)
        {
            var s = OnlineManager.Instance.StatusText;
            if (s != _lastOnlineStatus)
            {
                _lastOnlineStatus = s;
                _onlineStatusText.text = s;
            }
        }
    }

    void BuildUI()
    {
        var cGo = new GameObject("TitleCanvas");
        _canvas = cGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 10;
        var scaler = cGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);
        cGo.AddComponent<GraphicRaycaster>();

        // 背景画像（Title.jpg 全画面）
        var bg = new GameObject("BG");
        bg.transform.SetParent(cGo.transform, false);
        var bgImg = bg.AddComponent<Image>();
        var tex = UIAssetTable.Instance.titleBackground;
        if (tex != null)
            bgImg.sprite = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f));
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        // 透明ボタン（絶対ピクセル値、キャンバス中心原点、Y上が正）
        MakeHitButton(cGo.transform, "OnlineCanvas", -5f, -148f, 470f, 77f, () => { CloseAllPanels(); _onlinePanel.SetActive(true); });
        MakeHitButton(cGo.transform, "VsCpu", -126f, -264f, 230f, 72f, () => StartVsCpu());
        MakeHitButton(cGo.transform, "Record", 123f, -263f, 230f, 72f, () => { CloseAllPanels(); OpenHistory(); });
        MakeHitButton(cGo.transform, "Settings", 0f, -380f, 470f, 77f, () => { CloseAllPanels(); _settingsPanel.SetActive(true); });

        // Photon attribution (required by PUN Free license)
        var photonTxt = MakeText(cGo.transform, "Powered by Photon", 22);
        photonTxt.color = new Color(1f, 1f, 1f, 0.6f);
        var photonRt = photonTxt.GetComponent<RectTransform>();
        photonRt.anchorMin = new Vector2(0f, 0f);
        photonRt.anchorMax = new Vector2(1f, 0f);
        photonRt.pivot = new Vector2(0.5f, 0f);
        photonRt.anchoredPosition = new Vector2(0f, 12f);
        photonRt.sizeDelta = new Vector2(0f, 36f);

        BuildSettingsPanel(cGo.transform);
        BuildMapPreviewPanel(cGo.transform);
        BuildHistoryPanel(cGo.transform);
        BuildOnlinePanel(cGo.transform);
    }

    void MakeHitButton(Transform parent, string name, float cx, float cy, float w, float h,
                       UnityEngine.Events.UnityAction action)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = Color.white;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor = new Color(0f, 0f, 0f, 0f);
        colors.highlightedColor = new Color(0f, 0f, 0f, 0.10f);
        colors.pressedColor = new Color(0f, 0f, 0f, 0.35f);
        colors.selectedColor = new Color(0f, 0f, 0f, 0f);
        colors.fadeDuration = 0.08f;
        btn.colors = colors;
        btn.onClick.AddListener(() => { SoundManager.Tap(); action?.Invoke(); });
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(cx, cy);
        rt.sizeDelta = new Vector2(w, h);
    }

    // ----------------------------------------------------------------
    //  設定パネル（新デザイン）
    // ----------------------------------------------------------------

    void BuildSettingsPanel(Transform parent)
    {
        _settingsPanel = MakePanel(parent, new Vector2(0.05f, 0.08f), new Vector2(0.95f, 0.92f),
                                   new Color(0.08f, 0.08f, 0.18f, 0.97f));

        MakeText(_settingsPanel.transform, "設定", 42).GetComponent<RectTransform>().Apply(
            new Vector2(0f, 0.880f), new Vector2(1f, 0.990f));

        // ═══ ゲームルール ════════════════════════════════
        MakeSectionHeader(_settingsPanel.transform, "ゲームルール",
            new Vector2(0.03f, 0.830f), new Vector2(0.97f, 0.870f));

        // ゲーム時間 ボタン
        MakeLabel(_settingsPanel.transform, "ゲーム時間",
            new Vector2(0.05f, 0.795f), new Vector2(0.55f, 0.825f));

        float[] timeSecs = { 300f, 600f, 0f };
        string[] timeLbls = { "5分", "10分", "制限なし" };
        _timeButtons = new Button[timeLbls.Length];
        for (int i = 0; i < timeLbls.Length; i++)
        {
            int idx = i;
            float x0 = 0.05f + i * 0.31f;
            _timeButtons[i] = MakeGridButton(_settingsPanel.transform, timeLbls[i],
                new Vector2(x0, 0.730f), new Vector2(x0 + 0.29f, 0.790f));
            _timeButtons[i].onClick.AddListener(() =>
            {
                GameManager.BattleTimeLimit = timeSecs[idx];
                RefreshTimeButtons();
            });
        }
        RefreshTimeButtons();

        // グリッドサイズ
        MakeLabel(_settingsPanel.transform, "グリッドサイズ",
            new Vector2(0.05f, 0.650f), new Vector2(0.75f, 0.680f));

        string[] sizeLbls = { "標準\n7×14", "大\n9×18", "特大\n13×26" };
        _sizeButtons = new Button[sizeLbls.Length];
        for (int i = 0; i < sizeLbls.Length; i++)
        {
            int idx = i;
            float x0 = 0.05f + i * 0.31f;
            _sizeButtons[i] = MakeGridButton(_settingsPanel.transform, sizeLbls[i],
                new Vector2(x0, 0.584f), new Vector2(x0 + 0.29f, 0.645f));
            _sizeButtons[i].onClick.AddListener(() => OnSizeButton(idx));
        }
        RefreshSizeButtons();

        // 同時移動上限
        MakeLabel(_settingsPanel.transform, "同時移動上限",
            new Vector2(0.05f, 0.553f), new Vector2(0.70f, 0.581f));

        var limitInfo = MakeText(_settingsPanel.transform, "同時に動かせる最大キャラ数", 20);
        limitInfo.color = new Color(0.55f, 0.55f, 0.70f, 1f);
        limitInfo.alignment = TextAnchor.MiddleLeft;
        var limitInfoRt = limitInfo.GetComponent<RectTransform>();
        limitInfoRt.anchorMin = new Vector2(0.05f, 0.523f);
        limitInfoRt.anchorMax = new Vector2(0.97f, 0.551f);
        limitInfoRt.offsetMin = new Vector2(10f, 0f);
        limitInfoRt.offsetMax = Vector2.zero;

        string[] limitLbls = { "1匹", "2匹", "無制限" };
        int[] limitVals = { 1, 2, 0 };
        _limitButtons = new Button[limitLbls.Length];
        for (int i = 0; i < limitLbls.Length; i++)
        {
            int idx = i;
            float x0 = 0.05f + i * 0.31f;
            _limitButtons[i] = MakeGridButton(_settingsPanel.transform, limitLbls[i],
                new Vector2(x0, 0.487f), new Vector2(x0 + 0.29f, 0.521f));
            _limitButtons[i].onClick.AddListener(() =>
            {
                UnityEngine.PlayerPrefs.SetInt("active_limit", limitVals[idx]);
                UnityEngine.PlayerPrefs.Save();
                RefreshLimitButtons();
            });
        }
        RefreshLimitButtons();

        // 霧の中の敵 トグル
        MakeToggleRow(_settingsPanel.transform, "霧の中の敵",
            new Vector2(0.05f, 0.430f), new Vector2(0.95f, 0.485f),
            FogManager.ShowEnemyInFog,
            v => { FogManager.ShowEnemyInFog = v; });

        // ═══ マップ ═══════════════════════════════════════
        MakeSectionHeader(_settingsPanel.transform, "マップ",
            new Vector2(0.03f, 0.385f), new Vector2(0.97f, 0.425f));

        MakeToggleRow(_settingsPanel.transform, "使用マップ固定",
            new Vector2(0.05f, 0.310f), new Vector2(0.95f, 0.380f),
            SavedMapsManager.UseSelectedMap,
            v => { SavedMapsManager.UseSelectedMap = v; });

        var mapBtn = MakeButton(_settingsPanel.transform, "マップを選ぶ", ColNormal,
            new Vector2(0.05f, 0.235f), new Vector2(0.63f, 0.305f));
        mapBtn.onClick.AddListener(OpenMapPreview);

        var clearMapsBtn = MakeButton(_settingsPanel.transform, "保存初期化",
            new Color(0.45f, 0.12f, 0.12f, 1f),
            new Vector2(0.65f, 0.235f), new Vector2(0.95f, 0.305f));
        clearMapsBtn.onClick.AddListener(() => SavedMapsManager.Clear());

        // ═══ オーディオ ═══════════════════════════════════
        MakeSectionHeader(_settingsPanel.transform, "オーディオ",
            new Vector2(0.03f, 0.188f), new Vector2(0.97f, 0.228f));

        MakeSlider(_settingsPanel.transform, "Music",
            new Vector2(0.02f, 0.133f), new Vector2(0.98f, 0.183f),
            SoundManager.BgmVolume, SoundManager.SetBgmVolume);
        MakeSlider(_settingsPanel.transform, "SFX",
            new Vector2(0.02f, 0.078f), new Vector2(0.98f, 0.128f),
            SoundManager.SeVolume, SoundManager.SetSeVolume);
        MakeSlider(_settingsPanel.transform, "Voice",
            new Vector2(0.02f, 0.023f), new Vector2(0.98f, 0.073f),
            SoundManager.AnimalVolume, SoundManager.SetAnimalVolume);

        MakeCloseButton(_settingsPanel.transform,
            new Vector2(0.76f, 0.918f), new Vector2(0.99f, 0.984f),
            () => _settingsPanel.SetActive(false));

        _settingsPanel.SetActive(false);
    }

    // ----------------------------------------------------------------
    //  マッププレビューパネル（変更なし）
    // ----------------------------------------------------------------

    void BuildMapPreviewPanel(Transform parent)
    {
        _mapPreviewPanel = MakePanel(parent, new Vector2(0.03f, 0.05f), new Vector2(0.97f, 0.95f),
                                     new Color(0.06f, 0.06f, 0.16f, 0.98f));

        MakeText(_mapPreviewPanel.transform, "マップを選ぶ", 38).GetComponent<RectTransform>().Apply(
            new Vector2(0f, 0.91f), new Vector2(1f, 0.99f));

        var mapGo = new GameObject("Minimap");
        mapGo.transform.SetParent(_mapPreviewPanel.transform, false);
        _minimapImage = mapGo.AddComponent<Image>();
        _minimapImage.preserveAspect = true;
        var mapRt = mapGo.GetComponent<RectTransform>();
        mapRt.anchorMin = new Vector2(0.10f, 0.30f); mapRt.anchorMax = new Vector2(0.90f, 0.89f);
        mapRt.offsetMin = mapRt.offsetMax = Vector2.zero;

        _seedText = MakeText(_mapPreviewPanel.transform, "", 24);
        _seedText.color = new Color(0.8f, 0.8f, 0.8f);
        _seedText.GetComponent<RectTransform>().Apply(new Vector2(0.05f, 0.24f), new Vector2(0.95f, 0.30f));

        _savedCountText = MakeText(_mapPreviewPanel.transform, "", 24);
        _savedCountText.color = new Color(0.6f, 1f, 0.6f);
        _savedCountText.GetComponent<RectTransform>().Apply(new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.24f));

        var nextBtn = MakeButton(_mapPreviewPanel.transform, "次のマップ", ColNormal,
            new Vector2(0.05f, 0.09f), new Vector2(0.47f, 0.17f));
        nextBtn.onClick.AddListener(GenerateNextPreviewMap);

        _saveToggleBtn = MakeButton(_mapPreviewPanel.transform, "保存", ColNormal,
            new Vector2(0.53f, 0.09f), new Vector2(0.95f, 0.17f));
        _saveToggleBtn.onClick.AddListener(ToggleSaveCurrentMap);

        MakeCloseButton(_mapPreviewPanel.transform, new Vector2(0.25f, 0.02f), new Vector2(0.75f, 0.08f),
            () => _mapPreviewPanel.SetActive(false));

        _mapPreviewPanel.SetActive(false);
    }

    void OpenMapPreview()
    {
        _mapPreviewPanel.SetActive(true);
        GenerateNextPreviewMap();
    }

    void GenerateNextPreviewMap()
    {
        _previewSeed = Random.Range(0, int.MaxValue);
        UpdateMinimapDisplay();
    }

    void UpdateMinimapDisplay()
    {
        if (_currentMinimapTex != null) Object.Destroy(_currentMinimapTex);
        _currentMinimapTex = BuildMinimap(_previewSeed);
        _minimapImage.sprite = Sprite.Create(_currentMinimapTex,
            new Rect(0, 0, _currentMinimapTex.width, _currentMinimapTex.height),
            new Vector2(0.5f, 0.5f));
        _seedText.text = $"シード: {_previewSeed}";
        RefreshSaveToggleButton();
        RefreshSavedCount();
    }

    void ToggleSaveCurrentMap()
    {
        if (SavedMapsManager.Contains(_previewSeed))
            SavedMapsManager.Remove(_previewSeed);
        else
            SavedMapsManager.Add(_previewSeed);
        RefreshSaveToggleButton();
        RefreshSavedCount();
    }

    void RefreshSaveToggleButton()
    {
        if (_saveToggleBtn == null) return;
        bool saved = SavedMapsManager.Contains(_previewSeed);
        _saveToggleBtn.GetComponent<Image>().color =
            saved ? new Color(0.45f, 0.12f, 0.12f, 1f) : ColSelected;
        _saveToggleBtn.GetComponentInChildren<Text>().text = saved ? "削除" : "保存";
    }

    void RefreshSavedCount()
    {
        if (_savedCountText == null) return;
        int n = SavedMapsManager.Seeds.Count;
        _savedCountText.text = n == 0
            ? "保存済み: なし（ゲームはランダム生成）"
            : $"保存済み: {n}個（ゲーム開始時にランダム選択）";
    }

    Texture2D BuildMinimap(int seed)
    {
        (int cols, int rows) = GridManager.SelectedPreset switch
        {
            GridManager.GridSizePreset.Small  => (7,  10),
            GridManager.GridSizePreset.Medium => (9,  13),
            _                                 => (13, 18),
        };
        TerrainGenerator.COLS = cols;
        TerrainGenerator.ROWS = rows;
        TerrainGenerator.BASE_ROWS = 2;
        TerrainGenerator.HOME_MIN_X = cols / 2 - 2;
        TerrainGenerator.HOME_MAX_X = cols / 2 + 2;

        var grid = TerrainGenerator.Generate(seed);
        const int S = 16;
        var tex = new Texture2D(cols * S, rows * S, TextureFormat.RGB24, false);
        tex.filterMode = FilterMode.Point;

        var tileTable   = TileAssetTable.Instance;
        var pixelCache  = new System.Collections.Generic.Dictionary<TerrainType, Color[]>();

        for (int x = 0; x < cols; x++)
            for (int z = 0; z < rows; z++)
            {
                var cell = grid[x, z];

                if (!pixelCache.TryGetValue(cell.Type, out var tilePixels))
                {
                    var tileTex = tileTable.GetTerrainTex(cell.Type);
                    if (tileTex != null)
                    {
                        tilePixels = SampleTileTexture(tileTex, S);
                    }
                    else
                    {
                        Color fb = cell.Type switch
                        {
                            TerrainType.Rocky  => new Color(0.62f, 0.58f, 0.52f),
                            TerrainType.Forest => new Color(0.10f, 0.42f, 0.10f),
                            TerrainType.River  => new Color(0.20f, 0.55f, 0.90f),
                            TerrainType.Bridge => new Color(0.92f, 0.78f, 0.42f),
                            TerrainType.Pond   => new Color(0.28f, 0.65f, 0.75f),
                            TerrainType.Sand   => new Color(0.88f, 0.80f, 0.52f),
                            _                  => new Color(0.45f, 0.78f, 0.30f),
                        };
                        tilePixels = new Color[S * S];
                        for (int i = 0; i < tilePixels.Length; i++) tilePixels[i] = fb;
                    }
                    pixelCache[cell.Type] = tilePixels;
                }

                for (int pz = 0; pz < S; pz++)
                    for (int px = 0; px < S; px++)
                    {
                        tex.SetPixel(x * S + px, z * S + pz, tilePixels[pz * S + px]);
                    }
            }
        tex.Apply();
        return tex;
    }

    static Color[] SampleTileTexture(Texture2D src, int S)
    {
        var rt   = RenderTexture.GetTemporary(S, S, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tmp = new Texture2D(S, S, TextureFormat.ARGB32, false);
        tmp.ReadPixels(new Rect(0, 0, S, S), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var pixels = tmp.GetPixels();
        Object.Destroy(tmp);
        return pixels;
    }

    // ----------------------------------------------------------------
    //  ボタン処理（変更なし）
    // ----------------------------------------------------------------

    void CloseAllPanels()
    {
        _settingsPanel?.SetActive(false);
        _onlinePanel?.SetActive(false);
        _historyPanel?.SetActive(false);
        _mapPreviewPanel?.SetActive(false);
    }

    void StartVsCpu()
    {
        PhaseController.Instance?.GoToGame(false);
    }

    void OpenHistory()
    {
        if (_historyPanel == null) BuildHistoryPanel(_canvas.transform);
        _historyPanel.SetActive(true);
    }

    void BuildHistoryPanel(Transform parent)
    {
        _historyPanel = MakePanel(parent, new Vector2(0.03f, 0.05f), new Vector2(0.97f, 0.95f),
                                  new Color(0.06f, 0.06f, 0.16f, 0.98f));

        MakeText(_historyPanel.transform, "マッチ履歴", 42).GetComponent<RectTransform>().Apply(
            new Vector2(0f, 0.91f), new Vector2(1f, 0.99f));

        var records = MatchHistoryManager.Load();

        if (records.Count == 0)
        {
            MakeText(_historyPanel.transform, "まだ記録がありません", 28).GetComponent<RectTransform>().Apply(
                new Vector2(0.05f, 0.45f), new Vector2(0.95f, 0.55f));
        }
        else
        {
            MakeLabel(_historyPanel.transform, "プレイヤー名", new Vector2(0.03f, 0.83f), new Vector2(0.50f, 0.90f));
            MakeLabel(_historyPanel.transform, "結果", new Vector2(0.50f, 0.83f), new Vector2(0.72f, 0.90f));
            MakeLabel(_historyPanel.transform, "日付", new Vector2(0.72f, 0.83f), new Vector2(0.97f, 0.90f));

            // 閉じるボタン上端(0.08f)の10px上を中枠下限とし、固定行高で上から埋める
            const float listTop = 0.82f;
            const float listBot = 0.08f;  // 閉じる上端(0.07f) + 約10px分
            float rowH = (listTop - listBot) / 12f;  // 最大12行で均等分割
            int show = Mathf.Min(records.Count, 12);
            for (int i = 0; i < show; i++)
            {
                var r = records[i];
                float y2 = listTop - i * rowH;
                float y1 = y2 - rowH;
                var bg = MakePanel(_historyPanel.transform,
                    new Vector2(0.02f, y1 + 0.005f), new Vector2(0.98f, y2 - 0.005f),
                    i % 2 == 0 ? new Color(0.12f, 0.12f, 0.22f, 1f)
                               : new Color(0.08f, 0.08f, 0.16f, 1f));

                MakeLabel(bg.transform, r.playerName, new Vector2(0.01f, 0f), new Vector2(0.50f, 1f));
                var resultTxt = MakeText(bg.transform, r.won ? "WIN" : "LOSE", 22);
                resultTxt.color = r.won
                    ? new Color(0.20f, 0.70f, 0.45f)
                    : new Color(0.60f, 0.10f, 0.15f);
                resultTxt.alignment = TextAnchor.MiddleLeft;
                resultTxt.GetComponent<RectTransform>().Apply(new Vector2(0.50f, 0f), new Vector2(0.72f, 1f));
                MakeLabel(bg.transform, r.date, new Vector2(0.72f, 0f), new Vector2(0.99f, 1f));
            }
        }

        MakeCloseButton(_historyPanel.transform, new Vector2(0.25f, 0.01f), new Vector2(0.75f, 0.07f),
            () => _historyPanel.SetActive(false));

        _historyPanel.SetActive(false);
    }

    void OnSizeButton(int idx)
    {
        GridManager.SelectedPreset = SizePresetOrder[idx];
        RefreshSizeButtons();
    }

    void RefreshSizeButtons()
    {
        if (_sizeButtons == null) return;
        for (int i = 0; i < _sizeButtons.Length; i++)
            _sizeButtons[i].GetComponent<Image>().color =
                GridManager.SelectedPreset == SizePresetOrder[i] ? ColSelected : ColNormal;
    }

    void RefreshLimitButtons()
    {
        if (_limitButtons == null) return;
        int[] vals = { 1, 2, 0 };
        int cur = UnityEngine.PlayerPrefs.GetInt("active_limit", 1);
        for (int i = 0; i < _limitButtons.Length; i++)
            _limitButtons[i].GetComponent<Image>().color = cur == vals[i] ? ColSelected : ColNormal;
    }

    void RefreshTimeButtons()
    {
        if (_timeButtons == null) return;
        float[] vals = { 300f, 600f, 0f };
        for (int i = 0; i < _timeButtons.Length; i++)
            _timeButtons[i].GetComponent<Image>().color =
                Mathf.Approximately(GameManager.BattleTimeLimit, vals[i]) ? ColSelected : ColNormal;
    }

    // ----------------------------------------------------------------
    //  オンラインパネル
    // ----------------------------------------------------------------

    void BuildOnlinePanel(Transform parent)
    {
        _onlinePanel = MakePanel(parent,
            new Vector2(0.05f, 0.55f),
            new Vector2(0.95f, 0.92f),
            new Color(0.08f, 0.08f, 0.18f, 0.97f));
        _onlinePanel.name = "OnlinePanel";

        // タイトル
        PlaceTop(MakeText(_onlinePanel.transform, "オンライン対戦", 42)
                     .GetComponent<RectTransform>(),
                 0.03f, 0.60f, -48f, 68f);

        // 連勝記録
        int streak = MatchHistoryManager.GetWinStreak();
        var streakTxt = MakeText(_onlinePanel.transform,
            streak > 0 ? $"🔥{streak}連勝" : "連勝なし", 28);
        streakTxt.color = streak > 0
            ? new Color(1f, 0.75f, 0.2f, 1f)
            : new Color(0.55f, 0.55f, 0.70f, 1f);
        streakTxt.alignment = TextAnchor.MiddleRight;
        PlaceTop(streakTxt.GetComponent<RectTransform>(), 0.62f, 0.97f, -48f, 68f);

        // パスワードラベル
        var pwLbl = MakeText(_onlinePanel.transform, "パスワード", 26);
        pwLbl.alignment = TextAnchor.MiddleLeft;
        PlaceTop(pwLbl.GetComponent<RectTransform>(), 0.05f, 0.97f, -108f, 28f);

        // キーワード入力（左）+ 接続ボタン（右）
        _passwordField = MakeInputFieldTop(_onlinePanel.transform,
            "キーワードを入力...", 0.04f, 0.62f, -162f, 68f);

        var connectBtn = MakeButtonTop(_onlinePanel.transform, "接続",
            new Color(0.18f, 0.55f, 0.18f, 1f), 0.65f, 0.96f, -162f, 68f);
        connectBtn.onClick.AddListener(() =>
        {
            string kw = _passwordField != null ? _passwordField.text.Trim() : "";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[Online] 接続ボタン押下 kw='{kw}'");
#endif
            if (string.IsNullOrEmpty(kw)) return;
            string nm = _playerNameField != null ? _playerNameField.text.Trim() : "";
            if (nm.Length > 10) nm = nm[..10];
            PlayerPrefs.SetString("PlayerName", nm);
            EnsureOnlineObjects();
            OnlineManager.Instance.Connect(kw, nm);
            ShowConnecting();
            OnlineManager.Instance.OnConnectionFailed += OnConnectFailed;
            OnlineManager.Instance.OnBothConnected    += HideConnecting;
        });

        // ステータステキスト
        _onlineStatusText = MakeText(_onlinePanel.transform, "", 24);
        _onlineStatusText.color = new Color(0.55f, 0.75f, 0.55f, 1f);
        _onlineStatusText.alignment = TextAnchor.MiddleCenter;
        PlaceTop(_onlineStatusText.GetComponent<RectTransform>(), 0.04f, 0.96f, -215f, 26f);

        // プレイヤー名ヘッダー
        var nameHdr = MakeText(_onlinePanel.transform, "プレイヤー名を入力", 34);
        nameHdr.alignment = TextAnchor.MiddleLeft;
        PlaceTop(nameHdr.GetComponent<RectTransform>(), 0.05f, 0.97f, -262f, 40f);

        // プレイヤー名入力（PlayerPrefs から復元）
        _playerNameField = MakeInputFieldTop(_onlinePanel.transform,
            "名前を入力...", 0.04f, 0.96f, -326f, 68f);
        _playerNameField.characterLimit = 10;
        _playerNameField.text = PlayerPrefs.GetString("PlayerName", "");

        // 閉じるボタン（赤）
        var closeBtn = MakeButtonTop(_onlinePanel.transform, "✕ 閉じる",
            new Color(0.45f, 0.12f, 0.12f, 1f), 0.25f, 0.75f, -428f, 60f);
        closeBtn.onClick.RemoveAllListeners();
        closeBtn.onClick.AddListener(SoundManager.Cancel);
        closeBtn.onClick.AddListener(() =>
        {
            if (OnlineManager.Instance != null) OnlineManager.Instance.Disconnect();
            _onlinePanel.SetActive(false);
        });

        _onlinePanel.SetActive(false);
    }

    void ShowConnecting()
    {
        if (_connectingCanvas != null) return;
        _connectingCanvas = new GameObject("ConnectingCanvas");
        var canvas = _connectingCanvas.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = _connectingCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);
        _connectingCanvas.AddComponent<GraphicRaycaster>();

        var imgGo = new GameObject("LoadingImage");
        imgGo.transform.SetParent(_connectingCanvas.transform, false);
        var raw = imgGo.AddComponent<RawImage>();
        var tex = UIAssetTable.Instance.loadingScreen;
        if (tex != null) raw.texture = tex;
        else             raw.color   = new Color(0f, 0f, 0f, 0.85f);
        var rt = imgGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var cancelGo = new GameObject("CancelBtn");
        cancelGo.transform.SetParent(_connectingCanvas.transform, false);
        var cancelImg = cancelGo.AddComponent<Image>();
        cancelImg.color = new Color(0.45f, 0.12f, 0.12f, 1f);
        var cancelRt = cancelGo.GetComponent<RectTransform>();
        cancelRt.anchorMin = new Vector2(0.5f, 0f);
        cancelRt.anchorMax = new Vector2(0.5f, 0f);
        cancelRt.pivot     = new Vector2(0.5f, 0f);
        cancelRt.anchoredPosition = new Vector2(0f, 60f);
        cancelRt.sizeDelta        = new Vector2(200f, 56f);
        var cancelBtn = cancelGo.AddComponent<Button>();
        cancelBtn.targetGraphic = cancelImg;
        cancelBtn.onClick.AddListener(SoundManager.Cancel);
        cancelBtn.onClick.AddListener(() =>
        {
            if (OnlineManager.Instance != null) OnlineManager.Instance.Disconnect();
            if (_onlineStatusText != null) _onlineStatusText.text = "";
            HideConnecting();
        });
        MakeText(cancelGo.transform, "キャンセル", 30);
    }

    void HideConnecting()
    {
        if (_connectingCanvas == null) return;
        Destroy(_connectingCanvas);
        _connectingCanvas = null;
        if (OnlineManager.Instance != null)
        {
            OnlineManager.Instance.OnConnectionFailed -= OnConnectFailed;
            OnlineManager.Instance.OnBothConnected    -= HideConnecting;
        }
    }

    void OnConnectFailed(string msg)
    {
        HideConnecting();
    }

    void EnsureOnlineObjects()
    {
        if (OnlineManager.Instance == null)
        {
            var go = new GameObject("OnlineManager");
            go.AddComponent<OnlineManager>();
            go.AddComponent<OnlineGame>();
        }
    }

    static void PlaceTop(RectTransform rt, float x0, float x1, float posY, float h)
    {
        rt.anchorMin = new Vector2(x0, 1f);
        rt.anchorMax = new Vector2(x1, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, posY);
        rt.sizeDelta = new Vector2(0f, h);
    }

    Button MakeButtonTop(Transform parent, string label, Color color,
                         float x0, float x1, float posY, float h)
    {
        var go = new GameObject(label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        PlaceTop(go.GetComponent<RectTransform>(), x0, x1, posY, h);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(SoundManager.Tap);
        MakeText(go.transform, label, 28);
        return btn;
    }

    InputField MakeInputFieldTop(Transform parent, string placeholder,
                                  float x0, float x1, float posY, float h)
    {
        var go = new GameObject("InputField");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = ColNormal;
        PlaceTop(go.GetComponent<RectTransform>(), x0, x1, posY, h);
        var field = go.AddComponent<InputField>();
        field.targetGraphic = img;

        var phGo = new GameObject("Placeholder");
        phGo.transform.SetParent(go.transform, false);
        var ph = phGo.AddComponent<Text>();
        ph.font = UIAssetTable.Instance.font;
        ph.text = placeholder;
        ph.fontSize = 26;
        ph.color = new Color(0.55f, 0.55f, 0.70f, 1f);
        ph.alignment = TextAnchor.MiddleLeft;
        var phRt = phGo.GetComponent<RectTransform>();
        phRt.anchorMin = Vector2.zero; phRt.anchorMax = Vector2.one;
        phRt.offsetMin = new Vector2(15f, 0f); phRt.offsetMax = Vector2.zero;
        field.placeholder = ph;

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform, false);
        var txt = txtGo.AddComponent<Text>();
        txt.font = UIAssetTable.Instance.font;
        txt.fontSize = 28;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleLeft;
        var txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(15f, 0f); txtRt.offsetMax = Vector2.zero;
        field.textComponent = txt;

        return field;
    }

    // ----------------------------------------------------------------
    //  UI ヘルパー（新規追加分）
    // ----------------------------------------------------------------

    void MakeSectionHeader(Transform parent, string text, Vector2 ancMin, Vector2 ancMax)
    {
        var t = MakeText(parent, text, 24);
        t.color = new Color(0.65f, 0.72f, 0.88f, 1f);
        t.alignment = TextAnchor.MiddleLeft;
        var rt = t.GetComponent<RectTransform>();
        rt.Apply(ancMin, ancMax);
        rt.offsetMin = new Vector2(10f, 0f);
    }

    Slider MakeFullSlider(Transform parent, Vector2 ancMin, Vector2 ancMax,
                          float value, UnityEngine.Events.UnityAction<float> onChange)
    {
        var row = new GameObject("FullSlider");
        row.transform.SetParent(parent, false);
        var rowRt = row.AddComponent<RectTransform>();
        rowRt.anchorMin = ancMin; rowRt.anchorMax = ancMax;
        rowRt.offsetMin = rowRt.offsetMax = Vector2.zero;

        var sliderGo = new GameObject("Slider");
        sliderGo.transform.SetParent(row.transform, false);
        var sliderRt = sliderGo.AddComponent<RectTransform>();
        sliderRt.anchorMin = new Vector2(0f, 0.10f); sliderRt.anchorMax = new Vector2(1f, 0.90f);
        sliderRt.offsetMin = new Vector2(10f, 0f); sliderRt.offsetMax = new Vector2(-10f, 0f);
        var slider = sliderGo.AddComponent<Slider>();

        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(sliderGo.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.25f, 1f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.30f); bgRt.anchorMax = new Vector2(1f, 0.70f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var fillAreaGo = new GameObject("Fill Area");
        fillAreaGo.transform.SetParent(sliderGo.transform, false);
        var fillAreaRt = fillAreaGo.AddComponent<RectTransform>();
        fillAreaRt.anchorMin = new Vector2(0f, 0.30f); fillAreaRt.anchorMax = new Vector2(1f, 0.70f);
        fillAreaRt.offsetMin = new Vector2(5f, 0f); fillAreaRt.offsetMax = new Vector2(-15f, 0f);

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(fillAreaGo.transform, false);
        fillGo.AddComponent<Image>().color = new Color(0.25f, 0.65f, 0.35f, 1f);
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
        slider.fillRect = fillRt;

        var handleAreaGo = new GameObject("Handle Area");
        handleAreaGo.transform.SetParent(sliderGo.transform, false);
        var handleAreaRt = handleAreaGo.AddComponent<RectTransform>();
        handleAreaRt.anchorMin = Vector2.zero; handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(10f, 0f); handleAreaRt.offsetMax = new Vector2(-10f, 0f);

        var handleGo = new GameObject("Handle");
        handleGo.transform.SetParent(handleAreaGo.transform, false);
        handleGo.AddComponent<Image>().color = Color.white;
        var handleRt = handleGo.GetComponent<RectTransform>();
        handleRt.anchorMin = new Vector2(0f, 0f); handleRt.anchorMax = new Vector2(0f, 1f);
        handleRt.sizeDelta = new Vector2(20f, 0f);
        slider.handleRect = handleRt;

        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = value;
        if (onChange != null) slider.onValueChanged.AddListener(onChange);
        return slider;
    }

    void MakeToggleRow(Transform parent, string labelText, Vector2 ancMin, Vector2 ancMax,
                       bool initialValue, UnityEngine.Events.UnityAction<bool> onChange)
    {
        var row = MakePanel(parent, ancMin, ancMax, new Color(0f, 0f, 0f, 0f));

        var lbl = MakeText(row.transform, labelText, 28);
        lbl.alignment = TextAnchor.MiddleLeft;
        var lblRt = lbl.GetComponent<RectTransform>();
        lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = new Vector2(0.55f, 1f);
        lblRt.offsetMin = new Vector2(10f, 0f); lblRt.offsetMax = Vector2.zero;

        var bgGo = new GameObject("ToggleBG");
        bgGo.transform.SetParent(row.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = initialValue ? new Color(0.20f, 0.65f, 0.25f, 1f) : new Color(0.22f, 0.22f, 0.32f, 1f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.58f, 0.20f);
        bgRt.anchorMax = new Vector2(0.82f, 0.80f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var hGo = new GameObject("Handle");
        hGo.transform.SetParent(bgGo.transform, false);
        hGo.AddComponent<Image>().color = Color.white;
        var hRt = hGo.GetComponent<RectTransform>();
        hRt.anchorMin = initialValue ? new Vector2(0.50f, 0.10f) : new Vector2(0.05f, 0.10f);
        hRt.anchorMax = initialValue ? new Vector2(0.95f, 0.90f) : new Vector2(0.50f, 0.90f);
        hRt.offsetMin = hRt.offsetMax = Vector2.zero;

        var stTxt = MakeText(row.transform, initialValue ? "ON" : "OFF", 26);
        stTxt.alignment = TextAnchor.MiddleLeft;
        var stRt = stTxt.GetComponent<RectTransform>();
        stRt.anchorMin = new Vector2(0.84f, 0f); stRt.anchorMax = Vector2.one;
        stRt.offsetMin = stRt.offsetMax = Vector2.zero;

        bool cur = initialValue;
        var btn = bgGo.AddComponent<Button>();
        btn.targetGraphic = bgImg;
        btn.onClick.AddListener(SoundManager.Tap);
        btn.onClick.AddListener(() =>
        {
            cur = !cur;
            bgImg.color = cur ? new Color(0.20f, 0.65f, 0.25f, 1f) : new Color(0.22f, 0.22f, 0.32f, 1f);
            hRt.anchorMin = cur ? new Vector2(0.50f, 0.10f) : new Vector2(0.05f, 0.10f);
            hRt.anchorMax = cur ? new Vector2(0.95f, 0.90f) : new Vector2(0.50f, 0.90f);
            stTxt.text = cur ? "ON" : "OFF";
            onChange?.Invoke(cur);
        });
    }

    Button MakeGridButton(Transform parent, string label, Vector2 ancMin, Vector2 ancMax)
    {
        var go = MakePanel(parent, ancMin, ancMax, ColNormal);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = go.GetComponent<Image>();
        btn.onClick.AddListener(SoundManager.Tap);

        var t = MakeText(go.transform, label, 24);
        t.GetComponent<RectTransform>().Apply(Vector2.zero, Vector2.one);

        return btn;
    }

    // ----------------------------------------------------------------
    //  UI ヘルパー（変更なし）
    // ----------------------------------------------------------------

    GameObject MakePanel(Transform parent, Vector2 ancMin, Vector2 ancMax, Color color)
    {
        var go = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return go;
    }

    void MakeCloseButton(Transform parent, Vector2 ancMin, Vector2 ancMax,
                         UnityEngine.Events.UnityAction action)
    {
        var btn = MakeButton(parent, "✕ 閉じる", new Color(0.45f, 0.12f, 0.12f, 1f), ancMin, ancMax);
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(SoundManager.Cancel);
        btn.onClick.AddListener(action);
    }

    Button MakeButton(Transform parent, string label, Color color, Vector2 ancMin, Vector2 ancMax)
    {
        var go = MakePanel(parent, ancMin, ancMax, color);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = go.GetComponent<Image>();
        btn.onClick.AddListener(SoundManager.Tap);
        MakeText(go.transform, label, 28);
        return btn;
    }

    Text MakeText(Transform parent, string txt, int size)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        var f = UIAssetTable.Instance.font;
        t.font = f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.text = txt;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return t;
    }

    void MakeLabel(Transform parent, string txt, Vector2 ancMin, Vector2 ancMax)
    {
        var t = MakeText(parent, txt, 26);
        t.alignment = TextAnchor.MiddleLeft;
        var rt = t.GetComponent<RectTransform>();
        rt.Apply(ancMin, ancMax);
        rt.offsetMin = new Vector2(10f, 0f);
    }

    void MakeSlider(Transform parent, string label, Vector2 ancMin, Vector2 ancMax,
                    float initValue, UnityEngine.Events.UnityAction<float> onChange)
    {
        var row = new GameObject("SliderRow_" + label);
        row.transform.SetParent(parent, false);
        var rowRt = row.AddComponent<RectTransform>();
        rowRt.anchorMin = ancMin; rowRt.anchorMax = ancMax;
        rowRt.offsetMin = rowRt.offsetMax = Vector2.zero;

        var lbl = MakeText(row.transform, label, 24);
        lbl.alignment = TextAnchor.MiddleLeft;
        var lblRt = lbl.GetComponent<RectTransform>();
        lblRt.anchorMin = new Vector2(0f, 0f); lblRt.anchorMax = new Vector2(0.30f, 1f);
        lblRt.offsetMin = new Vector2(10f, 0f); lblRt.offsetMax = Vector2.zero;

        var sliderGo = new GameObject("Slider");
        sliderGo.transform.SetParent(row.transform, false);
        var sliderRt = sliderGo.AddComponent<RectTransform>();
        sliderRt.anchorMin = new Vector2(0.31f, 0.10f); sliderRt.anchorMax = new Vector2(0.98f, 0.90f);
        sliderRt.offsetMin = sliderRt.offsetMax = Vector2.zero;
        var slider = sliderGo.AddComponent<UnityEngine.UI.Slider>();

        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(sliderGo.transform, false);
        bgGo.AddComponent<UnityEngine.UI.Image>().color = new Color(0.15f, 0.15f, 0.25f, 1f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.30f); bgRt.anchorMax = new Vector2(1f, 0.70f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var fillAreaGo = new GameObject("Fill Area");
        fillAreaGo.transform.SetParent(sliderGo.transform, false);
        var fillAreaRt = fillAreaGo.AddComponent<RectTransform>();
        fillAreaRt.anchorMin = new Vector2(0f, 0.30f); fillAreaRt.anchorMax = new Vector2(1f, 0.70f);
        fillAreaRt.offsetMin = new Vector2(5f, 0f); fillAreaRt.offsetMax = new Vector2(-15f, 0f);

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(fillAreaGo.transform, false);
        fillGo.AddComponent<UnityEngine.UI.Image>().color = new Color(0.25f, 0.65f, 0.35f, 1f);
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
        slider.fillRect = fillRt;

        var handleAreaGo = new GameObject("Handle Slide Area");
        handleAreaGo.transform.SetParent(sliderGo.transform, false);
        var handleAreaRt = handleAreaGo.AddComponent<RectTransform>();
        handleAreaRt.anchorMin = Vector2.zero; handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(10f, 0f); handleAreaRt.offsetMax = new Vector2(-10f, 0f);

        var handleGo = new GameObject("Handle");
        handleGo.transform.SetParent(handleAreaGo.transform, false);
        handleGo.AddComponent<UnityEngine.UI.Image>().color = Color.white;
        var handleRt = handleGo.GetComponent<RectTransform>();
        handleRt.anchorMin = new Vector2(0f, 0.15f); handleRt.anchorMax = new Vector2(0f, 0.85f);
        handleRt.sizeDelta = new Vector2(16f, 0f);
        slider.handleRect = handleRt;

        slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = initValue;
        if (onChange != null) slider.onValueChanged.AddListener(onChange);
    }
}

static class RectTransformExt
{
    public static void Apply(this RectTransform rt, Vector2 ancMin, Vector2 ancMax)
    {
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
