using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;
using Photon.Pun;

[DefaultExecutionOrder(-47)]
public class PlacementManager : MonoBehaviour
{
    public static PlacementManager Instance { get; private set; }

    const int Budget = 130;
    const int MaxUnits = 10;
    public static float TotalTime = 120f;
    static int HomeMinX => TerrainGenerator.HOME_MIN_X;
    static int HomeMaxX => TerrainGenerator.HOME_MAX_X;

    public static int PlacingOwner = 1;

    const float CardTrayFrac = 0.18f;
    const float DividerBotFrac = 0.36f;
    const float DividerMidFrac = 0.38f;
    const float TopRowTopFrac = 0.94f;
    const float DividerTopFrac = 0.96f;

    static Vector2Int FoxPos1 => new(TerrainGenerator.COLS / 2, 0);
    static Vector2Int FoxPos2 => new(TerrainGenerator.COLS / 2, TerrainGenerator.ROWS - 1);

    int _placedCount;
    int _spentCost;
    bool _battleStarted;
    int _lastDisplayedSec = -1;

    readonly HashSet<Vector2Int> _occupied = new();
    readonly Dictionary<AnimalType, int> _typeCount = new();
    readonly List<(AnimalType type, Text txt)> _cardCounters = new();

    // drag
    AnimalType _dragAnimal;
    bool _dragging;
    RectTransform _ghostRect;
    Vector2Int _hoveredTile = new(-1, -1);

    // overview camera only (zoom cam removed)
    Camera _overviewCam;
    RenderTexture _overviewRT;

    // Movement pattern (5x5 grid)
    const int PatternGrid = 5;
    readonly Image[] _patternCells = new Image[PatternGrid * PatternGrid];

    // Placement slot UI (replaces zoom camera)
    readonly List<RectTransform> _slotRects = new();
    readonly List<Image> _slotBorderImages = new();

    static readonly Color SlotBorderNormal = new(0.78f, 0.50f, 0.18f, 1.0f); // #C7802D
    static readonly Color SlotBorderHover = new(1.00f, 0.75f, 0.30f, 1.0f);
    static readonly Color SlotBorderOccupied = new(0.45f, 0.30f, 0.10f, 1.0f);

    Canvas _canvas;
    Text _costText;
    Text _timerText;
    Text _descText;
    float _timeLeft;
    AnimalType _previewAnimal;

    readonly Dictionary<AnimalType, CardView> _trayCardViews = new();

    GameObject _previewCardGo;
    CardView _previewCardView;

    void Awake() { Instance = this; _timeLeft = TotalTime; }

    void Start()
    {
        BuildCameras();
        BuildUI();
        PreplaceFox();
        PreviewAnimal(AnimalType.Fox);
    }

    void Update()
    {
        if (_battleStarted) return;
        _timeLeft -= Time.deltaTime;
        if (_timeLeft <= 0f) { _timeLeft = 0f; OnStartBattle(); return; }
        int s = Mathf.CeilToInt(_timeLeft);
        if (_timerText != null && s != _lastDisplayedSec)
        {
            _lastDisplayedSec = s;
            _timerText.text = $"{s / 60}:{s % 60:D2}";
        }
    }

    // ---- Cameras (overview only) ----

    void BuildCameras()
    {
        float sp = GridManager.Instance.TileSize + GridManager.Instance.TileGap;
        float tileSize = GridManager.Instance.TileSize;
        float totalW = (TerrainGenerator.COLS - 1) * sp;
        float totalH = (TerrainGenerator.ROWS - 1) * sp;
        float contentW = totalW + 2f * tileSize;
        float contentH = totalH + 2f * tileSize;

        int rtH = 512;
        int rtW = Mathf.RoundToInt(rtH * contentW / contentH);
        _overviewRT = MakeRT(rtW, rtH);

        var ovGo = new GameObject("PlacementOverviewCam");
        _overviewCam = ovGo.AddComponent<Camera>();
        _overviewCam.orthographic = true;
        _overviewCam.orthographicSize = contentH / 2f;
        _overviewCam.transform.position = new Vector3(totalW * 0.5f, 20f, totalH * 0.5f);
        // P2 は自陣(+Z側)が画面下になるよう 180° 回転
        _overviewCam.transform.rotation = Quaternion.Euler(90f, PlacingOwner == 1 ? 0f : 180f, 0f);
        _overviewCam.targetTexture = _overviewRT;
        _overviewCam.clearFlags = CameraClearFlags.SolidColor;
        _overviewCam.backgroundColor = Color.black;
        _overviewCam.depth = -20f;
        SetupURPCamera(ovGo);
    }

    // ---- Card drag ----

    public void BeginCardDrag(AnimalType animal, Vector2 screenPos)
    {
        _dragging = true;
        _dragAnimal = animal;
        _ghostRect = CreateGhost(animal);
        MoveGhost(screenPos);
        RefreshMovePattern(animal);
    }

    public void UpdateCardDrag(Vector2 screenPos)
    {
        if (!_dragging) return;
        MoveGhost(screenPos);

        var newHover = ScreenToTile(screenPos);
        if (newHover == _hoveredTile) return;

        if (_hoveredTile.x >= 0)
            RefreshSlotBorder(_hoveredTile);

        _hoveredTile = newHover;

        if (_hoveredTile.x >= 0)
        {
            int idx = TileToSlotIndex(_hoveredTile);
            if (idx >= 0 && idx < _slotBorderImages.Count && _slotBorderImages[idx] != null)
                _slotBorderImages[idx].color = SlotBorderHover;
        }
    }

    public void EndCardDrag(Vector2 screenPos)
    {
        if (!_dragging) return;
        _dragging = false;

        if (_ghostRect != null) { Destroy(_ghostRect.gameObject); _ghostRect = null; }

        if (_hoveredTile.x >= 0)
            RefreshSlotBorder(_hoveredTile);
        _hoveredTile = new(-1, -1);

        TryPlace(_dragAnimal, ScreenToTile(screenPos));
    }

    void MoveGhost(Vector2 screenPos)
    {
        if (_ghostRect == null) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _canvas.transform as RectTransform, screenPos, null, out var lp);
        _ghostRect.anchoredPosition = lp;
    }

    // ScreenSpaceOverlay なので camera = null でスロットをヒットテスト
    Vector2Int ScreenToTile(Vector2 screenPos)
    {
        int numCols = HomeMaxX - HomeMinX + 1;
        int minZ = PlacingOwner == 1 ? 0 : TerrainGenerator.ROWS - TerrainGenerator.BASE_ROWS;

        for (int i = 0; i < _slotRects.Count; i++)
        {
            if (_slotRects[i] == null) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(_slotRects[i], screenPos, null))
            {
                int col = i % numCols;
                int row = i / numCols;

                // P1: UI上段=前列(z大)、UI下段=後列(z小)、左右そのまま
                // P2: 180°回転。z軸はP2目線で上段=前列(minZ)のまま、x軸は左右反転
                int boardX = PlacingOwner == 1
                    ? HomeMinX + col
                    : HomeMaxX - col;
                int boardZ = PlacingOwner == 1
                    ? minZ + (TerrainGenerator.BASE_ROWS - 1 - row)
                    : minZ + row;

                return new Vector2Int(boardX, boardZ);
            }
        }
        return new(-1, -1);
    }

    int TileToSlotIndex(Vector2Int tilePos)
    {
        int numCols = HomeMaxX - HomeMinX + 1;
        int minZ = PlacingOwner == 1 ? 0 : TerrainGenerator.ROWS - TerrainGenerator.BASE_ROWS;

        int col = PlacingOwner == 1
            ? tilePos.x - HomeMinX
            : HomeMaxX - tilePos.x;
        int row = PlacingOwner == 1
            ? TerrainGenerator.BASE_ROWS - 1 - (tilePos.y - minZ)
            : tilePos.y - minZ;

        if (col < 0 || col >= numCols || row < 0 || row >= TerrainGenerator.BASE_ROWS) return -1;
        return row * numCols + col;
    }

    void RefreshSlotBorder(Vector2Int tilePos)
    {
        int idx = TileToSlotIndex(tilePos);
        if (idx < 0 || idx >= _slotBorderImages.Count || _slotBorderImages[idx] == null) return;
        _slotBorderImages[idx].color = _occupied.Contains(tilePos) ? SlotBorderOccupied : SlotBorderNormal;
    }

    // ---- Placement ----

    void TryPlace(AnimalType animal, Vector2Int pos)
    {
        if (TileToSlotIndex(pos) < 0) return;
        if (_placedCount >= MaxUnits && !_occupied.Contains(pos)) return;

        int cost = AnimalDefinitions.GetCost(animal);

        if (_occupied.Contains(pos))
        {
            var existing = UnitManager.Instance.GetUnitAt(pos);
            if (existing == null || existing.Owner != PlacingOwner) return;
            if (existing.AnimalType == AnimalType.Fox) return;
            if (existing.AnimalType == animal) return;

            int refund = AnimalDefinitions.GetCost(existing.AnimalType);
            _spentCost -= refund;
            _placedCount--;
            _typeCount[existing.AnimalType] = Mathf.Max(0, _typeCount.GetValueOrDefault(existing.AnimalType, 0) - 1);
            UnitManager.Instance.RemoveUnit(existing);
            Destroy(existing.gameObject);
            _occupied.Remove(pos);
        }

        if (_spentCost + cost > Budget) { UpdateCostText(); RefreshCardCounters(); return; }

        int limit = TypeLimit(AnimalDefinitions.GetRank(animal));
        int current = _typeCount.GetValueOrDefault(animal, 0);
        if (current >= limit) return;

        UnitManager.Instance.SpawnUnit(animal, PlacingOwner, pos);
        _occupied.Add(pos);
        _placedCount++;
        _spentCost += cost;
        _typeCount[animal] = current + 1;

        RefreshSlotBorder(pos);
        UpdateCostText();
        RefreshCardCounters();
    }

    static int TypeLimit(AnimalRank rank) => rank switch
    {
        AnimalRank.S => 1,
        AnimalRank.A => 2,
        _ => 3,
    };

    void UpdateCostText()
    {
        if (_costText != null)
            _costText.text = $"残: {Budget - _spentCost}pt  {_placedCount}/{MaxUnits}";
    }

    void RefreshCardCounters()
    {
        foreach (var (type, txt) in _cardCounters)
        {
            int limit = TypeLimit(AnimalDefinitions.GetRank(type));
            int current = _typeCount.GetValueOrDefault(type, 0);
            txt.text = $"{current}/{limit}";
            txt.color = current >= limit ? new Color(1f, 0.4f, 0.4f) : Color.white;
        }

        UpdatePreviewCount(_previewAnimal);
    }

    // ---- Movement pattern panel ----

    void RefreshMovePattern(AnimalType animal)
    {
        var offsets = AnimalDefinitions.GetMoveOffsets(animal);
        var offsetSet = new HashSet<Vector2Int>(offsets);
        var attackOnly = AnimalDefinitions.GetAttackOnlyOffsets(animal);
        var attackOnlySet = new HashSet<Vector2Int>(attackOnly);
        int half = PatternGrid / 2;

        for (int r = 0; r < PatternGrid; r++)
        {
            for (int c = 0; c < PatternGrid; c++)
            {
                int dz = half - r;
                int dx = c - half;
                var img = _patternCells[r * PatternGrid + c];
                if (img == null) continue;

                if (dx == 0 && dz == 0)
                    img.color = new Color(0.90f, 0.90f, 0.90f, 1f);
                else if (attackOnlySet.Contains(new Vector2Int(dx, dz)))
                    img.color = new Color(1.00f, 0.70f, 0.10f, 1f);
                else if (offsetSet.Contains(new Vector2Int(dx, dz)))
                    img.color = new Color(0.15f, 0.70f, 0.15f, 1f);
                else
                    img.color = new Color(0.10f, 0.10f, 0.18f, 1f);
            }
        }

        if (_descText != null)
            _descText.text = GetAnimalDescription(animal);
    }

    static string GetAnimalDescription(AnimalType a) => a switch
    {
        AnimalType.Fox => "隊のリーダー。倒されると即敗北。\n8方向の高機動だが攻撃は低め。\n最後まで守り抜くことが最優先。",
        AnimalType.Bear => "地形を選ばないオールラウンダー。\n6方向移動・体力攻撃ともにトップ。\n岩・森・水場も速度を維持。",
        AnimalType.Gorilla => "密林の絶対王者。体力・攻撃最高峰。\n独特な斜め動きで森を制圧。\n水場不可。移動は遅め。",
        AnimalType.Snake => "奇襲特化の毒使い。攻撃力は高め。\nHPは少なめ。\n一撃必殺を狙う隠密の刺客。",
        AnimalType.Lion => "機動力抜群の8方向突破型。\n平地・砂地で真価を発揮する。\n水場・岩場は渡れない。",
        AnimalType.Tiger => "読めない軌跡の暗殺者。\n桂馬跳び＋斜め4方向に移動＆攻撃。\n森でも速度が衰えない。",
        AnimalType.Chimpanzee => "森の偵察王。森内の視界・速度は全動物随一。\n移動不可な3マスへの中距離攻撃が唯一可能。",
        AnimalType.Wolf => "低コストの速攻役。\n前進しつつ斜め後ろへの退路も確保。\n森での速度が落ちない。",
        AnimalType.Hippo => "水場最強の守護者。\n川・池を速度落とさず移動できる。\n4方向のみ。体力は高いが動きは遅め。",
        AnimalType.Giraffe => "視界7マス。全動物中最大。\n霧の中でも遠くの敵を捉え続ける。\n広大な戦場を見渡す監視塔。",
        AnimalType.Reindeer => "俊足の偵察役。速度トップクラス。\n斜め4方向＋前進。岩場が得意。\n灯台確保・陣地争いに向く。",
        AnimalType.Rhino => "前方突破の特攻隊長。\n前方2マス突進で大ダメージを与え、敵を後退させる。\n視界は狭く岩場・水場は不得手。",
        AnimalType.Horse => "戦場最速のランナー。\n前方2マスの直線突破で戦線を切る。\n岩場・水場は渡れないが森は走れるが遅い。",
        AnimalType.Boar => "縦横2マスに突進する奇襲型。\n前後左右すべてに2マス移動可能。\n突進ダメージあり。森でも速度維持。",
        AnimalType.Elephant => "最高の体力と攻撃力を誇る鉄槌。\n4方向移動。平地での正面衝突は最強。\n森には入れない。",
        AnimalType.Panda => "粘り強い中衛型。\n6方向移動で細かな位置調整が可能。\n体力は高めだが水場は苦手。",
        _ => "",
    };

    public void PreviewAnimal(AnimalType animal)
    {
        _previewAnimal = animal;
        RefreshMovePattern(animal);
        ShowSelectedCard(animal);
    }

    void ShowSelectedCard(AnimalType animal)
    {
        if (_previewCardGo == null) return;
        _previewCardGo.SetActive(true);

        if (_previewCardView != null)
        {
            var rank = AnimalDefinitions.GetRank(animal);
            var cards = CardAssetTable.Instance;
            var blocked = AnimalDefinitions.GetBlockedTerrain(animal);

            if (_previewCardView.animalImage != null)
            {
                var sprite = cards.GetAnimalSprite(animal);
                _previewCardView.animalImage.sprite = sprite;
                _previewCardView.animalImage.enabled = sprite != null;
            }
            if (_previewCardView.rankBadge != null)
                _previewCardView.rankBadge.sprite = cards.GetRankBadge(rank);
            if (_previewCardView.costBadge != null)
                _previewCardView.costBadge.sprite = cards.GetCoinBadge(rank);

            if (_previewCardView.terrainIcons != null && _previewCardView.terrainIcons.Length >= 3)
            {
                if (_previewCardView.terrainIcons[0] != null)
                    _previewCardView.terrainIcons[0].sprite = cards.GetTerrainSprite(TerrainType.Rocky, blocked.Contains(TerrainType.Rocky));
                if (_previewCardView.terrainIcons[1] != null)
                    _previewCardView.terrainIcons[1].sprite = cards.GetTerrainSprite(TerrainType.Forest, blocked.Contains(TerrainType.Forest));
                if (_previewCardView.terrainIcons[2] != null)
                    _previewCardView.terrainIcons[2].sprite = cards.GetTerrainSprite(TerrainType.River, blocked.Contains(TerrainType.River) || blocked.Contains(TerrainType.Pond));
            }

            if (_previewCardView.nameText != null)
                _previewCardView.nameText.text = animal.ToString();
        }

        UpdatePreviewCount(animal);
    }

    void UpdatePreviewCount(AnimalType animal)
    {
        if (_previewCardView == null || _previewCardView.countText == null) return;
        int limit = TypeLimit(AnimalDefinitions.GetRank(animal));
        int current = _typeCount.GetValueOrDefault(animal, 0);
        _previewCardView.countText.text = $"{current}/{limit}";
        _previewCardView.countText.color = current >= limit ? new Color(1f, 0.4f, 0.4f) : Color.white;
    }

    // ---- UI ----

    void BuildUI()
    {
        var cGo = new GameObject("PlacementCanvas");
        _canvas = cGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 10;
        var scaler = cGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(750f, 1334f);
        cGo.AddComponent<GraphicRaycaster>();

        // 黒ベース
        MakePanel(cGo.transform, Vector2.zero, Vector2.one, new Color(0.10f, 0.10f, 0.10f, 1f));

        // 全セクションを10px内側に収めるルート
        var contentRoot = new GameObject("ContentRoot");
        contentRoot.transform.SetParent(cGo.transform, false);
        var crRt = contentRoot.AddComponent<RectTransform>();
        crRt.anchorMin = Vector2.zero;
        crRt.anchorMax = Vector2.one;
        crRt.offsetMin = new Vector2(10f, 10f);
        crRt.offsetMax = new Vector2(-10f, -10f);

        BuildTopBar(contentRoot.transform);
        BuildTopDivider(contentRoot.transform);
        BuildTopSection(contentRoot.transform);
        BuildDivider(contentRoot.transform);
        BuildPlacementGrid(contentRoot.transform);
        BuildCardTray(contentRoot.transform);
        UpdateCostText();
    }

    void BuildTopBar(Transform parent)
    {
        var bar = MakePanel(parent,
            new Vector2(0f, DividerTopFrac), Vector2.one,
            new Color(0.17f, 0.17f, 0.17f, 1f)); // #2B2B2B
        bar.name = "TopBar";

        var costPanel = MakePanel(bar.transform, Vector2.zero, new Vector2(0.42f, 1f), Color.clear);
        _costText = MakeText(costPanel.transform, "", 22);
        _costText.alignment = TextAnchor.MiddleLeft;
        _costText.color = new Color(1f, 0.85f, 0.10f);
        (_costText.transform as RectTransform).offsetMin = new Vector2(10f, 0f);

        var timerPanel = MakePanel(bar.transform, new Vector2(0.38f, 0f), new Vector2(0.68f, 1f), Color.clear);
        _timerText = MakeText(timerPanel.transform, "2:00", 28);
        _timerText.fontStyle = FontStyle.Bold;

        // バトル開始ボタン: Vermilion Red #D95841
        var btnPanel = MakePanel(bar.transform, new Vector2(0.70f, 0.06f), new Vector2(0.98f, 0.94f),
                                 new Color(0.85f, 0.35f, 0.25f, 0.95f));
        var btn = btnPanel.AddComponent<Button>();
        btn.targetGraphic = btnPanel.GetComponent<Image>();
        btn.onClick.AddListener(OnStartBattle);
        MakeText(btnPanel.transform, "バトル開始", 22);
    }

    void BuildDivider(Transform parent)
    {
        var go = new GameObject("Divider");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, DividerBotFrac);
        rt.anchorMax = new Vector2(1f, DividerMidFrac);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.10f, 1f);
    }

    void BuildTopDivider(Transform parent)
    {
        var go = new GameObject("TopDivider");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, TopRowTopFrac);
        rt.anchorMax = new Vector2(1f, DividerTopFrac);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.10f, 1f);
    }

    void BuildTopSection(Transform parent)
    {
        var row = MakePanel(parent,
            new Vector2(0f, DividerMidFrac), new Vector2(1f, TopRowTopFrac),
            new Color(0.17f, 0.17f, 0.17f, 1f)); // #2B2B2B
        row.name = "TopRow";

        // 左60%: 全体マップ
        var leftBg = MakePanel(row.transform, Vector2.zero, new Vector2(0.60f, 1f),
                               new Color(0.10f, 0.10f, 0.10f, 1f));
        leftBg.name = "OverviewBg";

        // Quest Log パネル（上部の隙間）
        BuildQuestLogPanel(leftBg.transform);

        // マップ概要（元のまま）
        var ovGo = new GameObject("OverviewImage");
        ovGo.transform.SetParent(leftBg.transform, false);
        var ovRt = ovGo.AddComponent<RectTransform>();
        ovRt.anchorMin = new Vector2(0f, 0f);
        ovRt.anchorMax = new Vector2(1f, 0f);
        ovRt.pivot = new Vector2(0.5f, 0f);
        ovRt.offsetMin = new Vector2(10f, 0f);
        ovRt.offsetMax = new Vector2(-10f, 0f);

        var ovArf = ovGo.AddComponent<AspectRatioFitter>();
        ovArf.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
        ovArf.aspectRatio = (float)_overviewRT.width / _overviewRT.height;

        ovGo.AddComponent<RawImage>().texture = _overviewRT;

        // 右40%: パターン・説明・カードプレビュー
        var rightPanel = MakePanel(row.transform,
            new Vector2(0.60f, 0f), Vector2.one,
            new Color(0.17f, 0.17f, 0.17f, 1f)); // #2B2B2B
        rightPanel.name = "InfoPanel";

        // 移動パターン（上部40%）
        var patternArea = MakePanel(rightPanel.transform,
            new Vector2(0f, 0.60f), Vector2.one, Color.clear);
        patternArea.name = "PatternArea";

        var patLabel = MakeText(patternArea.transform, "移動・攻撃方法", 20);
        var pLrt = patLabel.GetComponent<RectTransform>();
        pLrt.anchorMin = new Vector2(0f, 0.80f);
        pLrt.anchorMax = Vector2.one;
        pLrt.offsetMin = pLrt.offsetMax = Vector2.zero;
        patLabel.color = new Color(0.6f, 0.6f, 0.6f, 1f);

        BuildMovePatternPanel(patternArea.transform);

        // 説明文（中央10%）
        var descArea = MakePanel(rightPanel.transform,
            new Vector2(0f, 0.50f), new Vector2(1f, 0.60f), Color.clear);
        descArea.name = "DescArea";

        _descText = MakeText(descArea.transform, "", 11);
        var drt = _descText.GetComponent<RectTransform>();
        drt.anchorMin = Vector2.zero;
        drt.anchorMax = Vector2.one;
        drt.offsetMin = new Vector2(6f, 4f);
        drt.offsetMax = new Vector2(-4f, -4f);
        _descText.alignment = TextAnchor.UpperCenter;
        _descText.color = new Color(0.85f, 0.85f, 0.85f, 1f);
        _descText.resizeTextForBestFit = true;

        // カードプレビュー（下部50%）
        var cardSpace = MakePanel(rightPanel.transform,
            Vector2.zero, new Vector2(1f, 0.50f), Color.clear);
        cardSpace.name = "SelectedCardSpace";

        BuildCardPreviewArea(cardSpace.transform);
    }

    void BuildCardPreviewArea(Transform parent)
    {
        var containerGo = new GameObject("PreviewCardContainer");
        containerGo.transform.SetParent(parent, false);
        var containerRt = containerGo.AddComponent<RectTransform>();
        containerRt.anchorMin = new Vector2(0.5f, 0f);
        containerRt.anchorMax = new Vector2(0.5f, 1f);
        containerRt.pivot = new Vector2(0.5f, 0.5f);
        containerRt.offsetMin = containerRt.offsetMax = Vector2.zero;
        var containerArf = containerGo.AddComponent<AspectRatioFitter>();
        containerArf.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
        containerArf.aspectRatio = 2f / 3f;

        var prefab = CardAssetTable.Instance.cardPrefab;
        if (prefab == null) return;

        _previewCardGo = Instantiate(prefab, containerGo.transform, false);
        _previewCardGo.name = "PreviewCard";

        if (_previewCardGo.TryGetComponent(out PlacementCard pc)) Destroy(pc);
        if (_previewCardGo.TryGetComponent(out LayoutElement le)) Destroy(le);
        if (_previewCardGo.TryGetComponent(out AspectRatioFitter childArf)) Destroy(childArf);

        if (_previewCardGo.TryGetComponent(out RectTransform rt))
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        _previewCardGo.TryGetComponent(out _previewCardView);
        _previewCardGo.SetActive(false);
    }

    void BuildMovePatternPanel(Transform parent)
    {
        var gridGo = new GameObject("PatternGrid");
        gridGo.transform.SetParent(parent, false);
        var gridRt = gridGo.AddComponent<RectTransform>();
        gridRt.anchorMin = new Vector2(0.05f, 0.02f);
        gridRt.anchorMax = new Vector2(0.95f, 0.86f);
        gridRt.offsetMin = gridRt.offsetMax = Vector2.zero;

        var glg = gridGo.AddComponent<GridLayoutGroup>();
        glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        glg.constraintCount = PatternGrid;
        glg.childAlignment = TextAnchor.MiddleCenter;
        glg.spacing = new Vector2(3f, 3f);
        glg.padding = new RectOffset(3, 3, 3, 3);
        glg.cellSize = new Vector2(40f, 40f);

        for (int i = 0; i < PatternGrid * PatternGrid; i++)
        {
            var cell = new GameObject($"PC_{i}");
            cell.transform.SetParent(gridGo.transform, false);
            _patternCells[i] = cell.AddComponent<Image>();
            _patternCells[i].color = new Color(0.10f, 0.10f, 0.18f, 1f);
        }
    }

    // ---- 配置スロットUI (ズームカメラの代替) ----

    void BuildPlacementGrid(Transform parent)
    {
        var bg = MakePanel(parent,
            new Vector2(0f, CardTrayFrac), new Vector2(1f, DividerBotFrac),
            new Color(0.10f, 0.10f, 0.10f, 1f)); // leftBgと同じダークグレー
        bg.name = "PlacementGrid";

        int numCols = HomeMaxX - HomeMinX + 1;
        int numRows = TerrainGenerator.BASE_ROWS;

        var gridGo = new GameObject("SlotGrid");
        gridGo.transform.SetParent(bg.transform, false);
        var gridRt = gridGo.AddComponent<RectTransform>();
        gridRt.anchorMin = new Vector2(0.04f, 0.08f);
        gridRt.anchorMax = new Vector2(0.96f, 0.92f);
        gridRt.offsetMin = gridRt.offsetMax = Vector2.zero;

        var glg = gridGo.AddComponent<GridLayoutGroup>();
        glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        glg.constraintCount = numCols;
        glg.childAlignment = TextAnchor.MiddleCenter;
        glg.spacing = new Vector2(6f, 6f);
        glg.padding = new RectOffset(4, 4, 4, 4);

        // 利用可能領域からセルサイズを計算（正方形）
        float refW = 750f * 0.92f - 8f - 6f * (numCols - 1);
        float refH = 1334f * (DividerBotFrac - CardTrayFrac) * 0.84f - 8f - 6f * (numRows - 1);
        float cellSize = Mathf.Min(refW / numCols, refH / numRows);
        glg.cellSize = new Vector2(cellSize, cellSize);

        for (int row = 0; row < numRows; row++)
        {
            for (int col = 0; col < numCols; col++)
            {
                var slotGo = new GameObject($"Slot_{row}_{col}");
                slotGo.transform.SetParent(gridGo.transform, false);

                // 外側: ゴールデンブラウン枠 #C7802D
                var borderImg = slotGo.AddComponent<Image>();
                borderImg.color = SlotBorderNormal;
                _slotBorderImages.Add(borderImg);
                _slotRects.Add(slotGo.GetComponent<RectTransform>());

                // 内側: チャコールグレー #2B2B2B（外枠との差で枠線効果）
                var innerGo = new GameObject("Inner");
                innerGo.transform.SetParent(slotGo.transform, false);
                var innerRt = innerGo.AddComponent<RectTransform>();
                innerRt.anchorMin = new Vector2(0.08f, 0.08f);
                innerRt.anchorMax = new Vector2(0.92f, 0.92f);
                innerRt.offsetMin = innerRt.offsetMax = Vector2.zero;
                innerGo.AddComponent<Image>().color = new Color(0.17f, 0.17f, 0.17f, 1f);
            }
        }
    }

    void BuildCardTray(Transform parent)
    {
        var bg = MakePanel(parent,
            Vector2.zero, new Vector2(1f, CardTrayFrac),
            Color.clear); // 枠なし・背景透明
        bg.name = "CardTray";

        var scrollGo = new
         GameObject("CardScroll");
        scrollGo.transform.SetParent(bg.transform, false);
        var sr = scrollGo.AddComponent<ScrollRect>();
        sr.horizontal = true;
        sr.vertical = false;
        sr.scrollSensitivity = 25f;
        sr.movementType = ScrollRect.MovementType.Clamped;
        var scrollRt = scrollGo.GetComponent<RectTransform>();
        scrollRt.anchorMin = Vector2.zero;
        scrollRt.anchorMax = Vector2.one;
        scrollRt.offsetMin = new Vector2(4f, 4f);
        scrollRt.offsetMax = new Vector2(-4f, -4f);

        var viewportGo = new GameObject("Viewport");
        viewportGo.transform.SetParent(scrollGo.transform, false);
        var viewportRt = viewportGo.AddComponent<RectTransform>();
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.offsetMin = viewportRt.offsetMax = Vector2.zero;
        viewportGo.AddComponent<RectMask2D>();
        sr.viewport = viewportRt;

        var contentGo = new GameObject("Content");
        contentGo.transform.SetParent(viewportGo.transform, false);
        var contentRt = contentGo.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0f, 0f);
        contentRt.anchorMax = new Vector2(0f, 1f);
        contentRt.pivot = new Vector2(0f, 0.5f);
        contentRt.offsetMin = contentRt.offsetMax = Vector2.zero;

        var layout = contentGo.AddComponent<HorizontalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = true;
        layout.childForceExpandWidth = false;
        layout.spacing = 12f;
        layout.padding = new RectOffset(6, 6, 4, 4);

        var fitter = contentGo.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        sr.content = contentRt;

        foreach (AnimalType a in System.Enum.GetValues(typeof(AnimalType)))
        {
            if (a == AnimalType.Fox) continue;
            CreateCard(contentGo.transform, a);
        }
    }

    void CreateCard(Transform parent, AnimalType animal)
    {
        var rank = AnimalDefinitions.GetRank(animal);
        var prefab = CardAssetTable.Instance.GetCardPrefab(animal);

        GameObject go;
        CardView view;

        float cardW = 1334f * CardTrayFrac * (2f / 3f);

        if (prefab != null)
        {
            go = Instantiate(prefab, parent, false);
            view = go.GetComponent<CardView>();
            if (go.TryGetComponent(out AspectRatioFitter existArf)) existArf.enabled = false;
            if (!go.TryGetComponent(out LayoutElement existLe)) existLe = go.AddComponent<LayoutElement>();
            existLe.preferredWidth = cardW;
        }
        else
        {
            go = new GameObject($"Card_{animal}");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.10f, 0.14f, 0.22f, 1f);
            go.AddComponent<LayoutElement>().preferredWidth = cardW;
            view = go.AddComponent<CardView>();
        }

        go.name = $"Card_{animal}";
        if (!go.TryGetComponent(out PlacementCard card)) card = go.AddComponent<PlacementCard>();
        card.AnimalType = animal;
        if (view != null) _trayCardViews[animal] = view;

        if (view != null)
        {
            var cards = CardAssetTable.Instance;
            var blocked = AnimalDefinitions.GetBlockedTerrain(animal);

            if (view.rankBadge != null)
                view.rankBadge.sprite = cards.GetRankBadge(rank);
            if (view.costBadge != null)
                view.costBadge.sprite = cards.GetCoinBadge(rank);

            if (view.terrainIcons != null && view.terrainIcons.Length >= 3)
            {
                if (view.terrainIcons[0] != null)
                    view.terrainIcons[0].sprite = cards.GetTerrainSprite(TerrainType.Rocky, blocked.Contains(TerrainType.Rocky));
                if (view.terrainIcons[1] != null)
                    view.terrainIcons[1].sprite = cards.GetTerrainSprite(TerrainType.Forest, blocked.Contains(TerrainType.Forest));
                if (view.terrainIcons[2] != null)
                    view.terrainIcons[2].sprite = cards.GetTerrainSprite(TerrainType.River, blocked.Contains(TerrainType.River) || blocked.Contains(TerrainType.Pond));
            }
        }

        int limit = TypeLimit(rank);
        if (view != null && view.countText != null)
        {
            view.countText.text = $"0/{limit}";
            _cardCounters.Add((animal, view.countText));
        }
    }

    RectTransform CreateGhost(AnimalType animal)
    {
        const float aspect = 2f / 3f;
        float h = 1334f * CardTrayFrac;

        var go = new GameObject("GhostCard");
        go.transform.SetParent(_canvas.transform, false);
        go.AddComponent<Image>().color = new Color(0.10f, 0.14f, 0.22f, 0.72f);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(h * aspect, h);
        rt.pivot = new Vector2(0.5f, 0.0f);

        var animalGo = new GameObject("AnimalImage");
        animalGo.transform.SetParent(go.transform, false);
        var animalImg = animalGo.AddComponent<Image>();
        animalImg.color = new Color(1f, 1f, 1f, 0.72f);
        var sprite = CardAssetTable.Instance.GetAnimalSprite(animal);
        if (sprite != null) animalImg.sprite = sprite;
        var animalRt = animalGo.GetComponent<RectTransform>();
        animalRt.anchorMin = Vector2.zero;
        animalRt.anchorMax = Vector2.one;
        animalRt.offsetMin = animalRt.offsetMax = Vector2.zero;

        return rt;
    }

    // ---- キツネ事前配置 ----

    void PreplaceFox()
    {
        UnitManager.Instance.SpawnUnit(AnimalType.Fox, 1, FoxPos1);
        UnitManager.Instance.SpawnUnit(AnimalType.Fox, 2, FoxPos2);

        var myFoxPos = PlacingOwner == 1 ? FoxPos1 : FoxPos2;
        _occupied.Add(myFoxPos);
        _placedCount++;
        _typeCount[AnimalType.Fox] = 1;
        RefreshSlotBorder(myFoxPos);

        int enemyOwner = PlacingOwner == 1 ? 2 : 1;
        foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(enemyOwner))
            SetUnitVisible(unit, false);

        UpdateCostText();
    }

    static void SetUnitVisible(AnimalUnit unit, bool visible)
    {
        foreach (var r in unit.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
    }

    // ---- Battle start ----

    void OnStartBattle()
    {
        if (_battleStarted) return;
        _battleStarted = true;

        foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(1))
            SetUnitVisible(unit, true);
        foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(2))
            SetUnitVisible(unit, true);

        if (OnlineManager.IsOnline)
        {
            OnlineGame.Instance?.LocalReady();
            // StartBattle は両者 Ready が揃った後に OnlineGame.CheckBothReady() が呼ぶ
        }
        else
        {
            P2AutoPlace();
            GameManager.Instance.StartBattle();
        }
    }

    static AIWeightSet LoadBestWeights()
    {
        string path = Path.Combine(Application.persistentDataPath, "best_weights.json");
        if (File.Exists(path))
            return JsonUtility.FromJson<AIWeightSet>(File.ReadAllText(path));
        var asset = Resources.Load<TextAsset>("best_weights");
        if (asset != null)
            return JsonUtility.FromJson<AIWeightSet>(asset.text);
        return new AIWeightSet();
    }

    void P2AutoPlace()
    {
        if (OnlineManager.IsOnline) return;
        if (GridManager.Instance == null || GridManager.Instance.Grid == null) return;

        var grid = GridManager.Instance.Grid;
        var w = LoadBestWeights();

        var lighthouses = new List<Vector2Int>();
        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
                if (grid[x, z]?.Outpost == OutpostType.Lighthouse)
                    lighthouses.Add(new Vector2Int(x, z));

        int totalTiles = TerrainGenerator.COLS * TerrainGenerator.ROWS;

        var candidates = new AnimalType[]
        {
            AnimalType.Tiger, AnimalType.Bear, AnimalType.Gorilla, AnimalType.Lion,
            AnimalType.Chimpanzee, AnimalType.Rhino, AnimalType.Horse, AnimalType.Snake,
            AnimalType.Boar, AnimalType.Hippo, AnimalType.Giraffe, AnimalType.Elephant,
            AnimalType.Panda, AnimalType.Wolf, AnimalType.Reindeer,
        };
        var scored = new List<(float score, AnimalType type)>();
        foreach (var type in candidates)
        {
            var (reachable, canReach) = TerrainReachability.CountReachableTiles(type, 2, grid, lighthouses);
            float mobility = (float)reachable / totalTiles;
            float score = w.AnimalPick.Get(type) + w.TerrainPickScale * mobility;
            if (!canReach && lighthouses.Count > 0) score -= 50f;
            scored.Add((score, type));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score));

        int budget = Budget - AnimalDefinitions.GetCost(AnimalType.Fox);
        int slots = MaxUnits - 1;
        var team = new List<AnimalType>();
        foreach (var (_, type) in scored)
        {
            if (slots <= 0 || budget <= 0) break;
            int cost = AnimalDefinitions.GetCost(type);
            if (cost <= budget) { team.Add(type); budget -= cost; slots--; }
        }

        var positions = new List<Vector2Int>();
        for (int x = HomeMinX; x <= HomeMaxX; x++)
            for (int z = TerrainGenerator.ROWS - TerrainGenerator.BASE_ROWS; z < TerrainGenerator.ROWS; z++)
            {
                var pos = new Vector2Int(x, z);
                if (GridManager.Instance.GetTile(x, z) != null
                    && UnitManager.Instance.GetUnitAt(pos) == null)
                    positions.Add(pos);
            }
        for (int i = positions.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }

        int count = Mathf.Min(team.Count, positions.Count);
        for (int i = 0; i < count; i++)
            UnitManager.Instance.SpawnUnit(team[i], 2, positions[i]);
    }

    static void SetupURPCamera(GameObject go)
    {
        var cam = go.GetComponent<Camera>();
        cam.allowMSAA = false;
        cam.allowHDR = false;
        cam.depthTextureMode = DepthTextureMode.None;

        if (!go.TryGetComponent(out UniversalAdditionalCameraData d)) d = go.AddComponent<UniversalAdditionalCameraData>();
        d.renderType = CameraRenderType.Base;
        d.renderShadows = false;
        d.requiresDepthTexture = false;
        d.requiresColorTexture = false;
        d.renderPostProcessing = false;
        d.antialiasing = AntialiasingMode.None;
        d.stopNaN = false;
        d.dithering = false;
        d.volumeLayerMask = 0;
    }

    static RenderTexture MakeRT(int w, int h)
    {
        var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 16)
        {
            useMipMap = false,
            autoGenerateMips = false,
            msaaSamples = 1,
        };
        var rt = new RenderTexture(desc);
        rt.Create();
        return rt;
    }

    void BuildQuestLogPanel(Transform parent)
    {
        var BgColor   = new Color(0.17f, 0.17f, 0.17f, 1f);
        var TitleBg   = new Color(0.22f, 0.22f, 0.22f, 1f);
        var LineColor = new Color(0.40f, 0.40f, 0.40f, 0.7f);
        var TextWhite = new Color(0.90f, 0.90f, 0.90f, 1f);
        var TextDim   = new Color(0.65f, 0.65f, 0.65f, 1f);

        var panel = new GameObject("QuestLog");
        panel.transform.SetParent(parent, false);
        panel.AddComponent<Image>().color = BgColor;
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0f, 0.80f);
        panelRt.anchorMax = Vector2.one;
        panelRt.offsetMin = new Vector2(10f, 2f);
        panelRt.offsetMax = new Vector2(-10f, -2f);

        // タイトルバー（上部40%）
        var titleBar = new GameObject("TitleBar");
        titleBar.transform.SetParent(panel.transform, false);
        titleBar.AddComponent<Image>().color = TitleBg;
        var tbRt = titleBar.GetComponent<RectTransform>();
        tbRt.anchorMin = new Vector2(0f, 0.58f);
        tbRt.anchorMax = Vector2.one;
        tbRt.offsetMin = tbRt.offsetMax = Vector2.zero;

        string winCondition = OnlineManager.IsOnline
            ? "狐を倒す / 時間切れ時HP差"
            : "制限時間以内に敵の狐を倒す";
        var titleTxt = MakeText(titleBar.transform, $"勝利条件: {winCondition}", 20);
        titleTxt.color = TextWhite;
        titleTxt.fontStyle = FontStyle.Bold;
        titleTxt.resizeTextForBestFit = true;
        titleTxt.resizeTextMinSize = 10;
        titleTxt.resizeTextMaxSize = 20;

        // 区切り線
        static void MakeLine(Transform p, float anchorY, Color col)
        {
            var go = new GameObject("Line");
            go.transform.SetParent(p, false);
            go.AddComponent<Image>().color = col;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.02f, anchorY);
            rt.anchorMax = new Vector2(0.98f, anchorY);
            rt.offsetMin = new Vector2(0f, -1f);
            rt.offsetMax = new Vector2(0f,  1f);
        }
        MakeLine(panel.transform, 0.58f, LineColor);

        // ステータス2列（下部58%）
        string timeStr  = GameManager.BattleTimeLimit <= 0f ? "制限なし" : $"{(int)(GameManager.BattleTimeLimit / 60)}分";
        int    lim      = PlayerPrefs.GetInt("active_limit", 0);
        string limitStr = lim == 0 ? "無制限" : $"{lim}匹";
        string opponent = OnlineManager.IsOnline && PhotonNetwork.PlayerListOthers.Length > 0
            ? PhotonNetwork.PlayerListOthers[0].NickName : "CPU";

        var leftCol = MakeText(panel.transform, $"相手: {opponent}\n同時移動: {limitStr}", 16);
        leftCol.color = TextWhite; leftCol.alignment = TextAnchor.MiddleLeft;
        leftCol.resizeTextForBestFit = true; leftCol.resizeTextMinSize = 11; leftCol.resizeTextMaxSize = 20;
        var lcRt = leftCol.GetComponent<RectTransform>();
        lcRt.anchorMin = new Vector2(0.02f, 0f); lcRt.anchorMax = new Vector2(0.50f, 0.58f);
        lcRt.offsetMin = new Vector2(6f, 2f); lcRt.offsetMax = Vector2.zero;

        var rightCol = MakeText(panel.transform, $"制限時間: {timeStr}\nシード: {GridManager.TerrainSeed}", 16);
        rightCol.color = TextWhite; rightCol.alignment = TextAnchor.MiddleLeft;
        rightCol.resizeTextForBestFit = true; rightCol.resizeTextMinSize = 11; rightCol.resizeTextMaxSize = 20;
        var rcRt = rightCol.GetComponent<RectTransform>();
        rcRt.anchorMin = new Vector2(0.50f, 0f); rcRt.anchorMax = new Vector2(0.98f, 0.58f);
        rcRt.offsetMin = new Vector2(6f, 2f); rcRt.offsetMax = Vector2.zero;
    }

    static string BuildSettingsSummary()
    {
        // 勝利条件
        string winCond = OnlineManager.IsOnline
            ? "勝利: 狐を倒す / 時間切れHP差"
            : "勝利: 制限時間以内に敵の狐を倒す\n相手: CPU";

        // 制限時間
        string timeStr = GameManager.BattleTimeLimit <= 0f
            ? "制限時間: なし"
            : $"制限時間: {(int)(GameManager.BattleTimeLimit / 60)}分";

        // 同時移動上限
        int lim = PlayerPrefs.GetInt("active_limit", 0);
        string limitStr = lim == 0 ? "同時移動: 無制限" : $"同時移動: {lim}匹";

        string seedStr = $"シード: {GridManager.TerrainSeed}";

        // オンライン時は相手名を先頭に
        if (OnlineManager.IsOnline && PhotonNetwork.PlayerListOthers.Length > 0)
            winCond = $"相手: {PhotonNetwork.PlayerListOthers[0].NickName}\n勝利: 狐を倒す / 時間切れHP差";

        return $"{winCond}\n{timeStr}\n{limitStr}\n{seedStr}";
    }

    public void HideUI()
    {
        if (_canvas != null) _canvas.gameObject.SetActive(false);

        if (_overviewCam != null) Destroy(_overviewCam.gameObject);
        if (_overviewRT != null) _overviewRT.Release();
    }

    // ---- Helpers ----

    GameObject MakePanel(Transform parent, Vector2 ancMin, Vector2 ancMax, Color color)
    {
        var go = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = ancMin;
        rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return go;
    }

    Text MakeText(Transform parent, string txt, int size)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = UIAssetTable.Instance.font;
        t.text = txt;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return t;
    }
}
