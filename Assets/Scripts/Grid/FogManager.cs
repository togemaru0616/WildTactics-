using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// 指定プレイヤー視点の霧システム
// - 霧は半透明：地形は常に透けて見える、ユニット・拠点マーカーは視界外で非表示
// - 拠点は一度発見したら霧が戻っても表示し続ける
[DefaultExecutionOrder(-48)]
public class FogManager : MonoBehaviour
{
    public static FogManager Instance { get; private set; }

    public static bool ShowEnemyInFog = false;

    // WiFiモードでは各端末でこの値を切り替える（1=P1視点, 2=P2視点）
    public int ViewingPlayer = 1;

    public float FogDelay = 10f;

    float[,] _timer;
    bool[,]  _outpostRevealed;
    bool[,]  _fogActive;       // 各タイルの現在の SetActive 状態キャッシュ
    GameObject[,] _fogTiles;

    // 灯台の占領オーナー（0=中立）
    readonly Dictionary<Vector2Int, int> _lighthouseOwners = new();
    const int LighthouseVisionRadius = 5;

    void Awake() => Instance = this;

    void Start()
    {
        InitFogTiles();
        StartCoroutine(FogUpdateLoop());
    }

    void InitFogTiles()
    {
        int cols = TerrainGenerator.COLS;
        int rows = TerrainGenerator.ROWS;
        _timer           = new float[cols, rows];
        _outpostRevealed = new bool[cols, rows];
        _fogActive       = new bool[cols, rows];
        _fogTiles        = new GameObject[cols, rows];

        float spacing = GridManager.Instance.TileSize + GridManager.Instance.TileGap;

        var mat = new Material(UIAssetTable.Instance.unlitShader);
        mat.color = new Color(0.07f, 0.07f, 0.13f, 0.78f);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend",   0f);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3000;

        for (int x = 0; x < cols; x++)
        {
            for (int z = 0; z < rows; z++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"Fog_{x}_{z}";
                go.transform.SetParent(transform);
                go.transform.position = new Vector3(x * spacing, 0.12f, z * spacing);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                go.transform.localScale = Vector3.one * GridManager.Instance.TileSize * 0.99f;
                Destroy(go.GetComponent<MeshCollider>());
                go.GetComponent<MeshRenderer>().material = new Material(mat);
                _fogTiles[x, z] = go;
                _timer[x, z] = 0f;
                _fogActive[x, z] = true; // GameObject はデフォルトでアクティブ（霧が出ている）
            }
        }
    }

    IEnumerator FogUpdateLoop()
    {
        const float tick = 0.1f;
        while (true)
        {
            yield return new WaitForSeconds(tick);

            // 配置フェーズ中は霧を全非表示（状態変化のあるタイルだけ更新）
            if (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Placement)
            {
                for (int x = 0; x < TerrainGenerator.COLS; x++)
                    for (int z = 0; z < TerrainGenerator.ROWS; z++)
                    {
                        if (_fogActive[x, z]) { _fogActive[x, z] = false; _fogTiles[x, z].SetActive(false); }
                        GridManager.Instance.SetOutpostVisible(x, z, false);
                    }
                continue;
            }

            // タイマーを減らす
            for (int x = 0; x < TerrainGenerator.COLS; x++)
                for (int z = 0; z < TerrainGenerator.ROWS; z++)
                    if (_timer[x, z] > 0f) _timer[x, z] -= tick;

            // 視点プレイヤーのユニットで視界を開く
            foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(ViewingPlayer))
                RevealAroundUnit(unit);

            // 自軍が占領している灯台の周囲を照らす
            foreach (var kv in _lighthouseOwners)
            {
                if (kv.Value != ViewingPlayer) continue;
                var lp = kv.Key;
                for (int dx = -LighthouseVisionRadius; dx <= LighthouseVisionRadius; dx++)
                for (int dz = -LighthouseVisionRadius; dz <= LighthouseVisionRadius; dz++)
                {
                    if (Mathf.Abs(dx) + Mathf.Abs(dz) > LighthouseVisionRadius) continue;
                    int nx = lp.x + dx, nz = lp.y + dz;
                    if (nx < 0 || nx >= TerrainGenerator.COLS) continue;
                    if (nz < 0 || nz >= TerrainGenerator.ROWS) continue;
                    Reveal(nx, nz);
                }
            }

            // 霧タイル・拠点の表示更新（SetActive は状態が変わったタイルのみ）
            for (int x = 0; x < TerrainGenerator.COLS; x++)
                for (int z = 0; z < TerrainGenerator.ROWS; z++)
                {
                    bool fog = _timer[x, z] <= 0f;
                    if (fog != _fogActive[x, z])
                    {
                        _fogActive[x, z] = fog;
                        _fogTiles[x, z].SetActive(fog);
                    }
                    if (!fog && !_outpostRevealed[x, z])
                    {
                        _outpostRevealed[x, z] = true;
                        OutpostManager.Instance?.NotifyTileRevealed(x, z, ViewingPlayer);
                    }
                    GridManager.Instance.SetOutpostVisible(x, z, _outpostRevealed[x, z]);
                }

            // 敵ユニットを霧で隠す＆BGM切り替え
            int  enemy        = 3 - ViewingPlayer;
            bool enemyVisible = false;
            foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(enemy))
            {
                bool vis = ShowEnemyInFog || IsVisible(unit.GridPos.x, unit.GridPos.y);
                unit.SetVisible(vis);
                if (vis && !unit.IsDead) enemyVisible = true;
            }

            if (GameManager.Instance?.Phase == GamePhase.Battle)
            {
                if (enemyVisible) SoundManager.Combat();
                else              SoundManager.Explore();
            }
        }
    }

    void RevealAroundUnit(AnimalUnit unit)
    {
        int cx = unit.GridPos.x, cz = unit.GridPos.y;
        bool selfInForest = GridManager.Instance.GetTile(cx, cz)?.Type == TerrainType.Forest;

        int range = selfInForest
            ? AnimalDefinitions.GetForestViewRange(unit.AnimalType)
            : AnimalDefinitions.GetViewRange(unit.AnimalType);

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dz = -range; dz <= range; dz++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dz) > range) continue;
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nx >= TerrainGenerator.COLS) continue;
                if (nz < 0 || nz >= TerrainGenerator.ROWS) continue;

                if (selfInForest)
                    Reveal(nx, nz);
                else
                    RevealAlongPath(cx, cz, nx, nz);
            }
        }
    }

    void RevealAlongPath(int fromX, int fromZ, int toX, int toZ)
    {
        if (fromX == toX && fromZ == toZ) { Reveal(toX, toZ); return; }

        int dx = toX - fromX, dz = toZ - fromZ;
        int ax = Mathf.Abs(dx), az = Mathf.Abs(dz);
        int sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
        int err = ax - az, x = fromX, z = fromZ;

        while (x != toX || z != toZ)
        {
            int e2 = 2 * err;
            if (e2 > -az) { err -= az; x += sx; }
            if (e2 <  ax) { err += ax; z += sz; }

            if (x < 0 || x >= TerrainGenerator.COLS || z < 0 || z >= TerrainGenerator.ROWS) return;

            Reveal(x, z);
            if (GridManager.Instance.GetTile(x, z)?.Type == TerrainType.Forest) return;
        }
    }

    void Reveal(int x, int z)
    {
        _timer[x, z] = FogDelay;
    }

    public bool IsVisible(int x, int z)
        => x >= 0 && x < TerrainGenerator.COLS
        && z >= 0 && z < TerrainGenerator.ROWS
        && _timer[x, z] > 0f;

    // OutpostManager から占領時に呼ばれる
    public void SetLighthouseOwner(Vector2Int pos, int owner)
        => _lighthouseOwners[pos] = owner;
}
