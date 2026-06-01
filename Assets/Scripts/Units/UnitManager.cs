using UnityEngine;
using System.Collections.Generic;

// 全ユニットの管理・グリッド上の占有情報を管理する
[DefaultExecutionOrder(-49)]
public class UnitManager : MonoBehaviour
{
    public static UnitManager Instance { get; private set; }

    readonly Dictionary<Vector2Int, AnimalUnit> _posToUnit = new();
    readonly Dictionary<int, AnimalUnit> _netIdToUnit = new();
    readonly List<AnimalUnit> _allUnits = new();

    // オーナー別キャッシュ（毎フレームnewしないため）
    readonly List<AnimalUnit> _units1 = new();
    readonly List<AnimalUnit> _units2 = new();
    readonly HashSet<Vector2Int> _friendly1 = new();
    readonly HashSet<Vector2Int> _friendly2 = new();

    // Owner 1: Fox=100, others=101-110  /  Owner 2: Fox=200, others=201-210
    int _nextNetId1 = 101;
    int _nextNetId2 = 201;

    void Awake()
    {
        Instance = this;
        ValidateAnimalAssets();
    }

    void ValidateAnimalAssets()
    {
        var table = AnimalAssetTable.Instance;
        if (table == null)
        {
            Debug.LogWarning("[UnitManager] AnimalAssetTable が Resources に見つかりません。");
            return;
        }
        foreach (AnimalType type in System.Enum.GetValues(typeof(AnimalType)))
        {
            var entry = table.Get(type);
            if (entry == null)
            {
                Debug.LogWarning($"[Validation] {type}: AnimalAssetTable にエントリがありません。");
                continue;
            }
            if (entry.prefab == null)
                Debug.LogWarning($"[Validation] {type}: visualPrefab が null です。");
            if (entry.visualScale <= 0f)
                Debug.LogWarning($"[Validation] {type}: visualScale = {entry.visualScale}（0より大きい値が必要です）。");
            if (entry.attackClip == null)
                Debug.LogWarning($"[Validation] {type}: attackClip が null です。");
            if (entry.deathClip == null)
                Debug.LogWarning($"[Validation] {type}: deathClip が null です。");
        }
    }

    public AnimalUnit GetUnitByNetId(int netId)
        => _netIdToUnit.TryGetValue(netId, out var u) ? u : null;

    public void Register(AnimalUnit unit)
    {
        _allUnits.Add(unit);
        _posToUnit[unit.GridPos] = unit;
        OwnerList(unit.Owner).Add(unit);
        OwnerFriendly(unit.Owner).Add(unit.GridPos);
    }

    public void NotifyMoved(AnimalUnit unit, Vector2Int from, Vector2Int to)
    {
        _posToUnit.Remove(from);
        _posToUnit[to] = unit;
        var fp = OwnerFriendly(unit.Owner);
        fp.Remove(from);
        fp.Add(to);
    }

    public AnimalUnit GetUnitAt(Vector2Int pos)
        => _posToUnit.TryGetValue(pos, out var u) ? u : null;

    // キャッシュ済みリストを返す（呼び出し元は変更禁止）
    public List<AnimalUnit> GetUnitsOfOwner(int owner) => OwnerList(owner);

    public void RemoveUnit(AnimalUnit unit)
    {
        _posToUnit.Remove(unit.GridPos);
        _netIdToUnit.Remove(unit.NetworkId);
        _allUnits.Remove(unit);
        OwnerList(unit.Owner).Remove(unit);
        OwnerFriendly(unit.Owner).Remove(unit.GridPos);
    }

    // キャッシュ済みHashSetを返す（A*用・呼び出し元は変更禁止）
    public HashSet<Vector2Int> GetFriendlyPositions(int owner) => OwnerFriendly(owner);

    List<AnimalUnit> OwnerList(int owner) => owner == 1 ? _units1 : _units2;
    HashSet<Vector2Int> OwnerFriendly(int owner) => owner == 1 ? _friendly1 : _friendly2;

    public AnimalUnit SpawnUnit(AnimalType type, int owner, Vector2Int pos, int forcedNetId = -1)
    {
        var table = AnimalAssetTable.Instance;
        var entry = table != null ? table.Get(type) : null;
        GameObject go;

        if (entry != null && entry.prefab != null)
        {
            // プレハブを非アクティブのまま Instantiate → Awake を遅延させる
            bool wasActive = entry.prefab.activeSelf;
            if (wasActive) entry.prefab.SetActive(false);
            go = Object.Instantiate(entry.prefab);   // inactive
            if (wasActive) entry.prefab.SetActive(true);

            // 不要コンポーネントを非アクティブ中に除去（アニメーター・Audio は除く）
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb.GetType().Name is "Animal_WanderScript" or "Common_WanderScript"
                                        or "Common_WanderManager" or "AnimalWanderManager"
                                        or "Common_SurfaceRotation" or "Animal_SurfaceRotation"
                                        or "Common_KillSwitch" or "Common_RandomInt")
                    Object.DestroyImmediate(mb);
            foreach (var agent in go.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
                Object.DestroyImmediate(agent);
            // タップ判定は Tile コライダーで行うため、モデル自身のコライダーを除去
            // （残っていると raycast がユニットに当たりタップが無視される）
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);

            // プレハブ由来の AudioSource を除去（AnimalUnit.Awake で新規追加する）
            foreach (var src in go.GetComponentsInChildren<AudioSource>(true))
                Object.DestroyImmediate(src);

            go.transform.localScale = Vector3.one * entry.visualScale;

            // 3Dモデルのマテリアルを Unlit に差し替え（床と輝度を揃える）
            var unlitShader = UIAssetTable.Instance.unlitShader;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var src = r.sharedMaterials;
                var dst = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    var m = new Material(unlitShader);
                    if (src[i] != null)
                    {
                        m.mainTexture = BrightenTexture(src[i].mainTexture, 3f);
                        m.color = src[i].color * 10f;
                    }
                    dst[i] = m;
                }
                r.materials = dst;
            }
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.SetActive(false);   // AddComponent 前に非アクティブにする
            go.transform.localScale = new Vector3(0.5f, 0.4f, 0.5f);
            var mat = new Material(UIAssetTable.Instance.unlitShader);
            mat.color = owner == 1 ? new Color(0.3f, 0.5f, 1f) : new Color(1f, 0.3f, 0.3f);
            go.GetComponent<MeshRenderer>().material = mat;
        }

        go.name = $"{type}_{owner}";
        // P1は敵陣(+Z)向き、P2は自陣(-Z)向き
        go.transform.rotation = Quaternion.Euler(0f, owner == 1 ? 0f : 180f, 0f);

        // GO が非アクティブの間に AddComponent・フィールド設定 → Awake は SetActive(true) まで遅延
        var unit = go.AddComponent<AnimalUnit>();
        unit.AnimalType = type;
        unit.Owner = owner;

        // NetworkId: 外部指定 > Fox固定値 > 連番
        int netId;
        if (forcedNetId >= 0)
            netId = forcedNetId;
        else if (type == AnimalType.Fox)
            netId = owner * 100;
        else
            netId = owner == 1 ? _nextNetId1++ : _nextNetId2++;
        unit.NetworkId = netId;
        _netIdToUnit[netId] = unit;

        unit.PlaceAt(pos);
        Register(unit);

        go.SetActive(true);   // ここで初めて Awake/Start が正しい AnimalType で動く
        return unit;
    }

    static Texture2D BrightenTexture(Texture src, float multiplier)
    {
        if (src == null) return null;
        var src2d = src as Texture2D;
        if (src2d == null || !src2d.isReadable) return src2d;

        var tex = new Texture2D(src2d.width, src2d.height, src2d.format, false);
        var pixels = src2d.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Color32(
                (byte)Mathf.Clamp(pixels[i].r * multiplier, 0, 255),
                (byte)Mathf.Clamp(pixels[i].g * multiplier, 0, 255),
                (byte)Mathf.Clamp(pixels[i].b * multiplier, 0, 255),
                pixels[i].a);
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}
