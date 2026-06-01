using UnityEngine;
using System.Collections.Generic;

[DefaultExecutionOrder(-50)]
public class GridManager : MonoBehaviour
{
    public static GridManager Instance { get; private set; }

    public enum GridSizePreset { Standard, Medium, Small }

    public static GridSizePreset SelectedPreset = GridSizePreset.Small;
    public static int            TerrainSeed    = -1; // -1=ランダム, WiFiホストが生成して共有

    [Header("Grid")]
    public float     TileSize     = 1f;
    public float     TileGap      = 0.02f;
    public TileCell[,] Grid { get; private set; }

    // ボードの四隅（GameCamera がそのまま使う）
    public static float BoardLeft, BoardRight, BoardNear, BoardFar;

    static readonly Color[] FallbackColors =
    {
        new Color(0.45f, 0.78f, 0.30f), // Flat
        new Color(0.62f, 0.58f, 0.52f), // Rocky
        new Color(0.10f, 0.42f, 0.10f), // Forest
        new Color(0.20f, 0.55f, 0.90f), // River
        new Color(0.92f, 0.78f, 0.42f), // Bridge
        new Color(0.28f, 0.65f, 0.75f), // Pond
        new Color(0.88f, 0.80f, 0.52f), // Sand
    };

    GameObject[] _tileObjects;
    List<GameObject> _borderTiles = new();
    GameObject   _blackGrid;
    Material     _baseMat;
    readonly Dictionary<Vector2Int, GameObject> _outpostOverlays = new();

    void Awake() => Instance = this;

    void Start()
    {
        if (AnimalUnit.SimMode) return;
        GenerateMap();
    }

    // ---- マップ生成 ----

    public void GenerateMap(int seed = -1)
    {
        (int cols, int rows) = SelectedPreset switch
        {
            GridSizePreset.Small  => (7,  10),
            GridSizePreset.Medium => (9,  13),
            _                     => (13, 18),
        };
        TerrainGenerator.COLS      = cols;
        TerrainGenerator.ROWS      = rows;
        TerrainGenerator.BASE_ROWS = 2;
        TerrainGenerator.HOME_MIN_X = cols / 2 - 2;
        TerrainGenerator.HOME_MAX_X = cols / 2 + 2;

        if (seed < 0 && TerrainSeed >= 0) seed = TerrainSeed;
        if (seed < 0) seed = Random.Range(0, int.MaxValue);
        TerrainSeed = seed;

        if (AnimalUnit.SimMode)
        {
            Grid = TerrainGenerator.Generate(seed);
            return;
        }

        ClearGrid();
        Grid = TerrainGenerator.Generate(seed);

        _baseMat     = new Material(UIAssetTable.Instance.unlitShader);
        _tileObjects = new GameObject[TerrainGenerator.COLS * TerrainGenerator.ROWS];

        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
                CreateTile(Grid[x, z]);

        CreateBlackGrid();
        CreateBorderTiles();
    }

    void CreateTile(TileCell tile)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"Tile_{tile.X}_{tile.Z}_{tile.Type}";
        go.transform.SetParent(transform);

        float sp = TileSize + TileGap;
        go.transform.position   = new Vector3(tile.X * sp, 0f, tile.Z * sp);
        go.transform.localScale = new Vector3(TileSize, 0.1f, TileSize);

        if (tile.Type == TerrainType.Bridge && BridgeShouldFlip(tile.X, tile.Z))
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

        var tex = TileAssetTable.Instance.GetTerrainTex(tile.Type);
        var mat = new Material(_baseMat);
        if (tex != null) { mat.SetTexture("_BaseMap", tex); mat.SetColor("_BaseColor", Color.white); }
        else             { mat.SetColor("_BaseColor", FallbackColors[(int)tile.Type]); }

        go.GetComponent<MeshRenderer>().material = mat;
        _tileObjects[tile.X * TerrainGenerator.ROWS + tile.Z] = go;

        if (tile.Outpost != OutpostType.None)
        {
            var t = TileAssetTable.Instance;
            var overlayTex = tile.Outpost == OutpostType.Lighthouse
                ? t.lighthouse
                : tile.Type == TerrainType.Forest ? t.campForest : t.campGrass;
            if (overlayTex != null)
            {
                var overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
                overlay.name = $"Outpost_{tile.X}_{tile.Z}";
                overlay.transform.SetParent(transform);
                overlay.transform.position = new Vector3(tile.X * sp, 0.07f, tile.Z * sp);
                bool flip = tile.Z >= TerrainGenerator.ROWS / 2;
                overlay.transform.rotation = flip
                    ? Quaternion.Euler(90f, 180f, 0f)
                    : Quaternion.Euler(90f, 0f, 0f);
                overlay.transform.localScale = Vector3.one * TileSize * 0.92f;
                Destroy(overlay.GetComponent<MeshCollider>());
                var oMat = new Material(UIAssetTable.Instance.unlitShader);
                oMat.SetTexture("_BaseMap", overlayTex);
                oMat.SetColor("_BaseColor", Color.white);
                oMat.SetFloat("_Surface", 1f);
                oMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                oMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                oMat.SetInt("_ZWrite", 0);
                oMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                oMat.renderQueue = 3000;
                overlay.GetComponent<MeshRenderer>().material = oMat;
                overlay.SetActive(false);
                _outpostOverlays[new Vector2Int(tile.X, tile.Z)] = overlay;
            }
        }
    }

    public void SetOutpostVisible(int x, int z, bool visible)
    {
        if (_outpostOverlays.TryGetValue(new Vector2Int(x, z), out var go) && go != null)
            go.SetActive(visible);
    }

    // ---- 台座・グリッド線 ----

    void CreateBlackGrid()
    {
        float sp     = TileSize + TileGap;
        // ボーダー（外周1マス）を含めたサイズ
        float totalW = (TerrainGenerator.COLS + 1) * sp + TileSize;
        float totalH = (TerrainGenerator.ROWS + 1) * sp + TileSize;
        float cx     = (TerrainGenerator.COLS - 1) * sp / 2f;
        float cz     = (TerrainGenerator.ROWS - 1) * sp / 2f;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "BlackGrid";
        go.transform.SetParent(transform);
        go.transform.position   = new Vector3(cx, -0.04f, cz);
        go.transform.rotation   = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(totalW, totalH, 1f);
        Destroy(go.GetComponent<MeshCollider>());

        var mat = new Material(UIAssetTable.Instance.unlitShader);
        mat.color = Color.black;
        go.GetComponent<MeshRenderer>().material = mat;
        _blackGrid = go;
    }

    void CreateBorderTiles()
    {
        float sp   = TileSize + TileGap;
        int   cols = TerrainGenerator.COLS;
        int   rows = TerrainGenerator.ROWS;

        var boardTex  = TileAssetTable.Instance.gameBoard;
        var borderMat = new Material(UIAssetTable.Instance.unlitShader);
        if (boardTex != null) { borderMat.SetTexture("_BaseMap", boardTex); borderMat.SetColor("_BaseColor", Color.white); }
        else                  { borderMat.SetColor("_BaseColor", new Color(0.58f, 0.38f, 0.18f)); }

        void Place(int gx, int gz)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Border";
            go.transform.SetParent(transform);
            go.transform.position   = new Vector3(gx * sp, 0f, gz * sp);
            go.transform.localScale = new Vector3(TileSize, 0.1f, TileSize);
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().material = borderMat;
            _borderTiles.Add(go);
        }

        // 上下の行（コーナー込み）
        for (int x = -1; x <= cols; x++) { Place(x, -1); Place(x, rows); }
        // 左右の列（コーナー除く）
        for (int z = 0; z < rows; z++)   { Place(-1, z); Place(cols, z); }

        // カメラ移動境界: 元グリッド端から 0.5 タイル分のみ（ボーダータイルは視覚的装飾）
        float cx     = (cols - 1) * sp / 2f;
        float cz     = (rows - 1) * sp / 2f;
        float gridW  = (cols - 1) * sp + TileSize;
        float gridH  = (rows - 1) * sp + TileSize;
        float margin = TileSize * 0.5f;
        BoardLeft  = cx - gridW / 2f - margin;
        BoardRight = cx + gridW / 2f + margin;
        BoardNear  = cz - gridH / 2f - margin;
        BoardFar   = cz + gridH / 2f + margin;
    }

    // ---- ユーティリティ ----

    void ClearGrid()
    {
        if (_tileObjects != null)
            foreach (var go in _tileObjects) if (go != null) Destroy(go);
        foreach (var go in _outpostOverlays.Values) if (go != null) Destroy(go);
        _outpostOverlays.Clear();
        foreach (var go in _borderTiles) if (go != null) Destroy(go);
        _borderTiles.Clear();
        if (_blackGrid != null) { Destroy(_blackGrid); _blackGrid = null; }
    }

    // ブリッジ列（固定 x）の中心 Z でセグメント全体の回転方向を統一する。
    // タイル単体の Z で判断すると ROWS/2 をまたぐ長さのブリッジで一部だけ回転するバグが出る。
    bool BridgeShouldFlip(int x, int z)
    {
        int minZ = z, maxZ = z;
        while (minZ > 0 && Grid[x, minZ - 1].Type == TerrainType.Bridge) minZ--;
        while (maxZ < TerrainGenerator.ROWS - 1 && Grid[x, maxZ + 1].Type == TerrainType.Bridge) maxZ++;
        return (minZ + maxZ) * 0.5f >= TerrainGenerator.ROWS * 0.5f;
    }

    public TileCell GetTile(int x, int z)
    {
        if (x < 0 || x >= TerrainGenerator.COLS || z < 0 || z >= TerrainGenerator.ROWS) return null;
        return Grid[x, z];
    }

    public Vector3 TileToWorld(int x, int z)
    {
        float sp = TileSize + TileGap;
        return new Vector3(x * sp, 0f, z * sp);
    }

[ContextMenu("Regenerate Map")]
    void RegenerateMap() => GenerateMap();
}
