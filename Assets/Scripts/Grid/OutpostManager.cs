using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class OutpostManager : MonoBehaviour
{
    public static OutpostManager Instance { get; private set; }

    const float LighthouseOccupyTime = 6f;

    class OutpostState
    {
        public Vector2Int Pos;
        public int Owner;
        public float Timer;
        public AnimalUnit OccupyingUnit;
        public bool DiscoveredP1;
        public bool DiscoveredP2;
        public GameObject Marker;
        public Transform CaptureBarFill;
        public Material CaptureBarMat;
        public float CaptureBarWidth;
    }

    static readonly Color NeutralColor = new Color(0.80f, 0.72f, 0.50f);
    static readonly Color P1Color = new Color(0.55f, 0.60f, 0.70f);
    static readonly Color P2Color = new Color(0.90f, 0.55f, 0.10f);

    readonly Dictionary<Vector2Int, OutpostState> _outposts = new();
    readonly HashSet<Vector2Int> _camps = new();
    int _campTick;

    void Awake() => Instance = this;

    void Start()
    {
        BuildOutposts();
        StartCoroutine(OccupationLoop());
    }

    void BuildOutposts()
    {
        var grid = GridManager.Instance.Grid;
        float spacing = GridManager.Instance.TileSize + GridManager.Instance.TileGap;

        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
            {
                if (grid[x, z].Outpost == OutpostType.Camp)
                    _camps.Add(new Vector2Int(x, z));
            }

        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
            {
                var tile = grid[x, z];
                if (tile.Outpost != OutpostType.Lighthouse) continue;

                var (marker, barFill, barMat, barW) = CreateMarker(x, z, spacing);
                var state = new OutpostState
                {
                    Pos = new Vector2Int(x, z),
                    Owner = 0,
                    Marker = marker,
                    CaptureBarFill = barFill,
                    CaptureBarMat = barMat,
                    CaptureBarWidth = barW,
                };
                state.Marker.SetActive(false);
                _outposts[state.Pos] = state;
            }
    }

    (GameObject marker, Transform barFill, Material barMat, float barW) CreateMarker(int x, int z, float spacing)
    {
        float tileSize = GridManager.Instance.TileSize;
        float outerR = tileSize * 0.45f * 0.66f;
        float innerR = outerR - tileSize * 0.04f;

        var go = new GameObject($"LighthouseRing_{x}_{z}");
        go.transform.SetParent(transform);
        go.transform.position = new Vector3(x * spacing, 0.13f, z * spacing);

        var ringGo = new GameObject("Ring");
        ringGo.transform.SetParent(go.transform, false);
        ringGo.AddComponent<MeshFilter>().mesh = BuildRingMesh(outerR, innerR);
        ringGo.AddComponent<MeshRenderer>().material = MakeUnlitMat(NeutralColor, 1f);

        float barW = tileSize * 0.75f;
        float barH = 0.05f;

        var barBG = GameObject.CreatePrimitive(PrimitiveType.Quad);
        barBG.name = "CaptureBarBG";
        barBG.transform.SetParent(go.transform, false);
        barBG.transform.localPosition = new Vector3(0f, 0.02f, -outerR - barH);
        barBG.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        barBG.transform.localScale = new Vector3(barW, barH, 1f);
        Object.Destroy(barBG.GetComponent<MeshCollider>());
        barBG.GetComponent<MeshRenderer>().material = MakeUnlitMat(new Color(0.1f, 0.1f, 0.1f), 0.8f);
        barBG.SetActive(false);

        var barFill = GameObject.CreatePrimitive(PrimitiveType.Quad);
        barFill.name = "CaptureBarFill";
        barFill.transform.SetParent(go.transform, false);
        barFill.transform.localPosition = new Vector3(-barW * 0.5f, 0.03f, -outerR - barH);
        barFill.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        barFill.transform.localScale = new Vector3(0f, barH, 1f);
        Object.Destroy(barFill.GetComponent<MeshCollider>());
        var fillMat = MakeUnlitMat(Color.white, 1f);
        barFill.GetComponent<MeshRenderer>().material = fillMat;
        barFill.SetActive(false);

        return (go, barFill.transform, fillMat, barW);
    }

    static Material MakeUnlitMat(Color col, float alpha)
    {
        var c = col; c.a = alpha;
        var mat = new Material(UIAssetTable.Instance.unlitShader);
        mat.color = c;
        if (alpha < 1f)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
        }
        return mat;
    }

    static Mesh BuildRingMesh(float outerR, float innerR, int seg = 40)
    {
        var verts = new Vector3[seg * 2];
        var tris = new int[seg * 6];

        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            verts[i * 2] = new Vector3(outerR * c, 0f, outerR * s);
            verts[i * 2 + 1] = new Vector3(innerR * c, 0f, innerR * s);

            int next = (i + 1) % seg;
            int ti = i * 6;
            tris[ti] = i * 2;
            tris[ti + 1] = i * 2 + 1;
            tris[ti + 2] = next * 2;
            tris[ti + 3] = next * 2;
            tris[ti + 4] = i * 2 + 1;
            tris[ti + 5] = next * 2 + 1;
        }

        var mesh = new Mesh();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        return mesh;
    }

    // ---- 占領ループ ----

    IEnumerator OccupationLoop()
    {
        const float tick = 0.5f;
        while (true)
        {
            yield return new WaitForSeconds(tick);

            _campTick++;
            if (_campTick >= 6)
            {
                _campTick = 0;
                foreach (var cp in _camps)
                {
                    var healTarget = UnitManager.Instance.GetUnitAt(cp);
                    if (healTarget != null && !healTarget.IsDead && !healTarget.IsMoving)
                        healTarget.Heal(10);
                }
            }

            foreach (var state in _outposts.Values)
            {
                var unit = UnitManager.Instance.GetUnitAt(state.Pos);
                bool hasUnit = unit != null && !unit.IsDead;

                // IsMoving: GridPos は移動開始時に予約済みだが視覚到達前なので占領カウントしない
                if (!hasUnit || unit.Owner == state.Owner || unit.IsMoving)
                {
                    state.Timer = 0f;
                    state.OccupyingUnit = null;
                    HideCaptureBar(state);
                    continue;
                }

                if (state.OccupyingUnit != unit) state.Timer = 0f;
                state.OccupyingUnit = unit;
                state.Timer += tick;

                UpdateCaptureBar(state, unit.Owner, state.Timer / LighthouseOccupyTime);
                if (state.Timer >= LighthouseOccupyTime) Capture(state, unit.Owner);
            }
        }
    }

    void UpdateCaptureBar(OutpostState state, int capturingOwner, float ratio)
    {
        if (state.CaptureBarFill == null) return;
        var bgTf = state.Marker.transform.Find("CaptureBarBG");
        if (bgTf != null) bgTf.gameObject.SetActive(true);
        state.CaptureBarFill.gameObject.SetActive(true);

        state.CaptureBarMat.color = capturingOwner == 1
            ? new Color(0.40f, 0.75f, 1.00f)
            : new Color(0.90f, 0.55f, 0.10f);

        float w = state.CaptureBarWidth * Mathf.Clamp01(ratio);
        var s = state.CaptureBarFill.localScale;
        state.CaptureBarFill.localScale = new Vector3(w, s.y, s.z);
        state.CaptureBarFill.localPosition = new Vector3(
            -state.CaptureBarWidth * 0.5f + w * 0.5f,
            state.CaptureBarFill.localPosition.y,
            state.CaptureBarFill.localPosition.z);
    }

    void HideCaptureBar(OutpostState state)
    {
        if (state.CaptureBarFill == null) return;
        state.CaptureBarFill.gameObject.SetActive(false);
        var bgTf = state.Marker.transform.Find("CaptureBarBG");
        if (bgTf != null) bgTf.gameObject.SetActive(false);
    }

    void Capture(OutpostState state, int newOwner)
    {
        state.Owner = newOwner;
        state.Timer = 0f;
        state.OccupyingUnit = null;
        RefreshMarkerColor(state);

        // 霧に灯台の新オーナーを通知
        FogManager.Instance?.SetLighthouseOwner(state.Pos, newOwner);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[Lighthouse] {state.Pos} → Player {newOwner}");
#endif
    }

    void RefreshMarkerColor(OutpostState state)
    {
        var baseCol = state.Owner switch { 1 => P1Color, 2 => P2Color, _ => NeutralColor };
        foreach (var mr in state.Marker.GetComponentsInChildren<MeshRenderer>())
        {
            var n = mr.gameObject.name;
            if (n == "CaptureBarBG" || n == "CaptureBarFill") continue;
            var c = baseCol; c.a = 1f;
            mr.material.color = c;
        }
    }

    // ---- 外部通知 ----

    public void NotifyUnitDamaged(Vector2Int pos)
    {
        if (_outposts.TryGetValue(pos, out var state) && state.OccupyingUnit != null)
            state.Timer = 0f;
    }

    public void NotifyTileRevealed(int x, int z, int player)
    {
        var pos = new Vector2Int(x, z);
        if (!_outposts.TryGetValue(pos, out var state)) return;

        bool alreadyKnown = player == 1 ? state.DiscoveredP1 : state.DiscoveredP2;
        if (alreadyKnown) return;

        if (player == 1) state.DiscoveredP1 = true;
        else state.DiscoveredP2 = true;

        state.Marker.SetActive(true);
    }

    public int GetOwner(Vector2Int pos)
        => _outposts.TryGetValue(pos, out var s) ? s.Owner : 0;

    public bool IsOutpost(Vector2Int pos) => _outposts.ContainsKey(pos);
}
