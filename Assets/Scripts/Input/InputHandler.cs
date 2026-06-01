using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Collections;
using System.Collections.Generic;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// バトルフェーズ専用の入力処理
// タップ → 自ユニット選択 → 移動/攻撃タイルをハイライト → タップで実行
public class InputHandler : MonoBehaviour
{
    public static InputHandler Instance { get; private set; }

    const float DragThreshold = 12f;
    // PlayerPrefs "active_limit": 1=1匹, 2=2匹, 0=無制限
    // オンライン時はP1の設定をOnlineManager.OnlineActiveLimitで上書き
    static int ActiveLimit
    {
        get
        {
            int raw = OnlineManager.IsOnline && OnlineManager.OnlineActiveLimit >= 0
                ? OnlineManager.OnlineActiveLimit
                : UnityEngine.PlayerPrefs.GetInt("active_limit", 0);
            return raw == 0 ? int.MaxValue : raw;
        }
    }

    AnimalUnit _selected;
    readonly List<(Vector2Int pos, bool isAttack)> _shown = new();

    // ---- ハイライトオブジェクトプール ----
    // 自ユニットハイライトと敵プレビューは同時に表示されないため共有プールで管理
    const int HlPoolSize = 16;
    readonly GameObject[] _hlPool = new GameObject[HlPoolSize];
    readonly Material[]   _hlMats = new Material[HlPoolSize];
    int _ownHlActive;
    int _enemyHlActive;
    // CantMoveFlash 専用（フラッシュアニメーションがあるため別管理）
    GameObject _cantMoveFlash;
    Material   _cantMoveFlashMat;

    Vector2 _touchStart;
    bool    _isDrag;

    // P1：青系
    static readonly Color ColP1Ring   = new(0.40f, 0.75f, 1.00f, 0.90f);
    static readonly Color ColP1Move   = new(0.20f, 0.85f, 0.30f, 0.60f); // 緑：移動可能
    static readonly Color ColP1Attack = new(1.00f, 0.75f, 0.10f, 0.70f); // 黄：攻撃可能
    static readonly Color ColP1Charge = new(1.00f, 0.45f, 0.05f, 0.75f); // オレンジ：突進
    // P2：赤系
    static readonly Color ColP2Move   = new(1.00f, 0.30f, 0.20f, 0.60f);
    static readonly Color ColP2Attack = new(1.00f, 0.75f, 0.10f, 0.70f); // 黄：攻撃可能
    static readonly Color ColP2Ring   = new(1.00f, 0.45f, 0.35f, 0.90f);

    AnimalUnit _enemyPreviewed;


    void OnEnable()  => EnhancedTouchSupport.Enable();
    void OnDisable() => EnhancedTouchSupport.Disable();

    void Awake()
    {
        Instance = this;
        InitPool();
    }

    void InitPool()
    {
        var shader = UIAssetTable.Instance.unlitShader;
        for (int i = 0; i < HlPoolSize; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "HL";
            go.transform.localScale = new Vector3(0.80f, 0.008f, 0.80f);
            Destroy(go.GetComponent<Collider>());
            go.SetActive(false);
            _hlPool[i] = go;
            var mat = new Material(shader);
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            go.GetComponent<MeshRenderer>().material = mat;
            _hlMats[i] = mat;
        }
        _cantMoveFlash = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _cantMoveFlash.name = "CantMoveFlash";
        _cantMoveFlash.transform.localScale = new Vector3(0.80f, 0.008f, 0.80f);
        Destroy(_cantMoveFlash.GetComponent<Collider>());
        _cantMoveFlash.SetActive(false);
        _cantMoveFlashMat = new Material(shader);
        _cantMoveFlashMat.SetFloat("_Surface", 1f);
        _cantMoveFlashMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _cantMoveFlashMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _cantMoveFlashMat.SetInt("_ZWrite", 0);
        _cantMoveFlashMat.color = new Color(1f, 0.15f, 0.15f, 0.90f);
        _cantMoveFlashMat.renderQueue = 3000;
        _cantMoveFlash.GetComponent<MeshRenderer>().material = _cantMoveFlashMat;
    }

    // プールからハイライト1個を借りる
    void RentOwn(Vector3 pos, Color col, int rq)
    {
        if (_ownHlActive >= HlPoolSize) return;
        var go  = _hlPool[_ownHlActive];
        var mat = _hlMats[_ownHlActive];
        _ownHlActive++;
        go.transform.position = pos;
        mat.color        = col;
        mat.renderQueue  = rq;
        go.SetActive(true);
    }

    void RentEnemy(Vector3 pos, Color col, int rq)
    {
        if (_enemyHlActive >= HlPoolSize) return;
        var go  = _hlPool[_enemyHlActive];
        var mat = _hlMats[_enemyHlActive];
        _enemyHlActive++;
        go.transform.position = pos;
        mat.color       = col;
        mat.renderQueue = rq;
        go.SetActive(true);
    }

    void ReturnOwnHl()
    {
        for (int i = 0; i < _ownHlActive; i++) _hlPool[i].SetActive(false);
        _ownHlActive = 0;
    }

    void ReturnEnemyHl()
    {
        for (int i = 0; i < _enemyHlActive; i++) _hlPool[i].SetActive(false);
        _enemyHlActive = 0;
    }

    void Update()
    {
        if (GameManager.Instance == null || GameManager.Instance.Phase != GamePhase.Battle) return;

        // 選択中ユニットが死んだ or 移動開始したらデセレクト（ハイライトが古くなるため）
        if (_selected != null && (_selected.IsDead || _selected.IsMoving)) Deselect();

        bool   tapped = false;
        Vector2 tapPos = default;

        if (Touch.activeTouches.Count == 1)
        {
            var t = Touch.activeTouches[0];
            if (t.began)  { _touchStart = t.screenPosition; _isDrag = false; }
            if (!t.began && !t.ended && !_isDrag &&
                Vector2.Distance(t.screenPosition, _touchStart) > DragThreshold) _isDrag = true;
            if (t.ended && !_isDrag) { tapped = true; tapPos = t.screenPosition; }
        }
        else
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var mpos = mouse.position.ReadValue();
                if (mouse.leftButton.wasPressedThisFrame)  { _touchStart = mpos; _isDrag = false; }
                if (mouse.leftButton.isPressed && !_isDrag &&
                    Vector2.Distance(mpos, _touchStart) > DragThreshold) _isDrag = true;
                if (mouse.leftButton.wasReleasedThisFrame && !_isDrag) { tapped = true; tapPos = mpos; }
            }
        }

        if (tapped) HandleTap(tapPos);

        // 選択リングを動いているユニットに追従
        UpdateSelectRing();
    }

    // ---- タップ処理 ----

    static int LocalPlayer => OnlineManager.IsOnline ? OnlineManager.LocalPlayer : 1;

    void HandleTap(Vector2 screenPos)
    {
        if (screenPos.y / Screen.height > 0.92f) return;

        var cam = Camera.main;
        if (cam == null) return;

        if (!Physics.Raycast(cam.ScreenPointToRay(screenPos), out var hit, 200f)) return;

        var parts = hit.collider.gameObject.name.Split('_');
        if (parts.Length < 3 || parts[0] != "Tile") return;
        if (!int.TryParse(parts[1], out int tx) || !int.TryParse(parts[2], out int tz)) return;
        var grid = new Vector2Int(tx, tz);

        // ① ハイライト済みタイルをタップ → 行動
        foreach (var (pos, isAttack) in _shown)
        {
            if (pos != grid) continue;

            if (isAttack)
            {
                var enemy = UnitManager.Instance.GetUnitAt(pos);
                if (enemy != null && !enemy.IsDead)
                {
                    _selected.LockTarget(enemy);
                    if (OnlineManager.IsOnline)
                        OnlineGame.Instance?.SendLockTarget(_selected.NetworkId, enemy.NetworkId);
                }
            }
            else
            {
                bool moved = _selected.RequestMoveTo(pos);
                if (moved && OnlineManager.IsOnline)
                    OnlineGame.Instance?.SendMoveTo(_selected.NetworkId, pos.x, pos.y);
            }
            Deselect();
            return;
        }

        // ② 自ユニットをタップ → 選択 or 同一ユニットで解除
        var unit = UnitManager.Instance.GetUnitAt(grid);
        if (unit != null && unit.Owner == LocalPlayer && !unit.IsDead)
        {
            ClearEnemyPreview();

            // CANTMOVE: 移動中 or クールダウン中はタップ不可（フィードバックのみ）
            if (unit.IsMoving || unit.IsOnMoveCooldown)
            {
                ShowCantMoveFeedback(unit);
                return;
            }

            // リアルタイム: すでに ACTIVE_LIMIT 匹が動いている → 新規移動不可
            if (CountActiveOwnUnits() >= ActiveLimit)
            {
                ShowCantMoveFeedback(unit);
                return;
            }

            if (_selected == unit) { Deselect(); return; }
            Select(unit);
            return;
        }

        // ③ 敵ユニットをタップ → 移動範囲プレビュー（読み取り専用）
        if (unit != null && unit.Owner != LocalPlayer && !unit.IsDead)
        {
            if (!unit.IsVisible) { Deselect(); ClearEnemyPreview(); return; }
            Deselect();
            if (_enemyPreviewed == unit) { ClearEnemyPreview(); return; }
            BuildEnemyHighlights(unit);
            return;
        }

        // ④ 選択中ユニットあり → ハイライト外タップは選択解除のみ
        if (_selected != null)
        {
            Deselect();
            return;
        }

        // ⑤ 何もないところ → 全解除
        Deselect();
        ClearEnemyPreview();
    }

    // ---- 選択・解除 ----

    void Select(AnimalUnit unit)
    {
        Deselect();
        _selected = unit;
        unit.OnTapped();
        BuildHighlights(unit);
        SoundManager.AnimalTouch();
    }

    void Deselect()
    {
        if (_selected != null) SoundManager.Cancel();
        _selected = null;
        ClearHighlights();
    }

    // ---- ハイライト構築 ----

    // 地形ブロック・2マスジャンプ中間チェックをまとめたヘルパー
    static bool IsTileReachable(Vector2Int from, Vector2Int off, HashSet<TerrainType> blocked,
                                 out Vector2Int dest, out TileCell tile)
    {
        dest = from + off;
        tile = GridManager.Instance.GetTile(dest.x, dest.y);
        if (tile == null) return false;

        if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
        {
            var mt = GridManager.Instance.GetTile(
                from.x + off.x / 2, from.y + off.y / 2);
            if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type)))
                return false;
        }
        return true;
    }

    void BuildHighlights(AnimalUnit unit)
    {
        ClearHighlights();

        float sp      = GridManager.Instance.TileSize + GridManager.Instance.TileGap;
        var   offsets = AnimalDefinitions.GetMoveOffsets(unit.AnimalType);
        var   blocked = AnimalDefinitions.GetBlockedTerrain(unit.AnimalType);
        int   front   = unit.Owner == 1 ? 1 : -1;

        foreach (var raw in offsets)
        {
            var off = new Vector2Int(raw.x, raw.y * front);
            if (!IsTileReachable(unit.GridPos, off, blocked, out var dest, out var tile)) continue;
            bool destBlocked = tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type);

            var occ     = UnitManager.Instance.GetUnitAt(dest);
            bool isAlly = occ != null && !occ.IsDead && occ.Owner == unit.Owner;
            if (isAlly) continue;

            bool isEnemy = occ != null && !occ.IsDead;

            // 入れない地形 → 攻撃専用タイル（黄）として常に表示
            if (destBlocked)
            {
                if (isEnemy) _shown.Add((dest, true));
                RentOwn(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP1Attack, 3001);
                continue;
            }

            // 突進: 2マス移動先に敵 → 押し出し可能なら移動として扱う（オレンジ）
            bool is2step = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2;
            if (isEnemy && is2step && AnimalDefinitions.HasCharge(unit.AnimalType))
            {
                var pushDir  = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                               off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                var pushDest = dest + pushDir;
                var pushTile = GridManager.Instance.GetTile(pushDest.x, pushDest.y);
                var blockedE = AnimalDefinitions.GetBlockedTerrain(occ.AnimalType);
                var pushOcc  = UnitManager.Instance.GetUnitAt(pushDest);
                bool canPush = occ.AnimalType != AnimalType.Elephant
                    && pushTile != null
                    && (pushTile.Type == TerrainType.Bridge || !blockedE.Contains(pushTile.Type))
                    && (pushOcc == null || pushOcc.IsDead);
                if (canPush)
                {
                    _shown.Add((dest, false));
                    RentOwn(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP1Charge, 3001);
                }
                continue;
            }

            if (isEnemy)
            {
                _shown.Add((dest, true));
                RentOwn(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP1Attack, 3001);
            }
            else if (!isEnemy)
            {
                _shown.Add((dest, false));
                RentOwn(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP1Move, 3001);
            }
        }

        // 攻撃専用オフセット（チンパンジー中距離攻撃）
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(unit.AnimalType))
        {
            var off  = new Vector2Int(raw.x, raw.y * front);
            var dest = unit.GridPos + off;
            if (GridManager.Instance.GetTile(dest.x, dest.y) == null) continue;
            var occ  = UnitManager.Instance.GetUnitAt(dest);
            bool isAlly = occ != null && !occ.IsDead && occ.Owner == unit.Owner;
            if (isAlly) continue;
            bool isEnemy = occ != null && !occ.IsDead;
            if (isEnemy)
                _shown.Add((dest, true));
            RentOwn(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP1Attack, 3001);
        }

        // 選択リングは必ず最後に借りる（UpdateSelectRing が _hlPool[_ownHlActive-1] を参照）
        var ringPos0 = GridManager.Instance.TileToWorld(unit.GridPos.x, unit.GridPos.y) + new Vector3(0f, 0.31f, 0f);
        RentOwn(ringPos0, ColP1Ring, 3000);
    }

    // ---- 選択リングを動いているユニットに追従 ----

    void UpdateSelectRing()
    {
        if (_ownHlActive == 0) return;
        if (_selected != null)
        {
            var tp = GridManager.Instance.TileToWorld(_selected.GridPos.x, _selected.GridPos.y);
            _hlPool[_ownHlActive - 1].transform.position = tp + new Vector3(0f, 0.31f, 0f);
        }
    }

    void ClearHighlights()
    {
        _shown.Clear();
        ReturnOwnHl();
    }

    // ---- 敵移動範囲プレビュー ----

    void BuildEnemyHighlights(AnimalUnit enemy)
    {
        ClearEnemyPreview();
        _enemyPreviewed = enemy;

        float sp      = GridManager.Instance.TileSize + GridManager.Instance.TileGap;
        var   offsets = AnimalDefinitions.GetMoveOffsets(enemy.AnimalType);
        var   blocked = AnimalDefinitions.GetBlockedTerrain(enemy.AnimalType);
        int   front   = enemy.Owner == 1 ? 1 : -1;

        foreach (var raw in offsets)
        {
            var off = new Vector2Int(raw.x, raw.y * front);
            if (!IsTileReachable(enemy.GridPos, off, blocked, out var dest, out var tile)) continue;
            var occ        = UnitManager.Instance.GetUnitAt(dest);
            if (occ != null && occ.Owner == enemy.Owner) continue;
            bool destBlocked  = tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type);
            bool isFriendly   = occ != null && !occ.IsDead;
            if (destBlocked)
            {
                RentEnemy(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP2Attack, 3001);
                continue;
            }
            RentEnemy(new Vector3(dest.x * sp, 0.14f, dest.y * sp),
                isFriendly ? ColP2Attack : ColP2Move, 3001);
        }

        // 攻撃専用オフセット（チンパンジー中距離攻撃など）
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(enemy.AnimalType))
        {
            var off  = new Vector2Int(raw.x, raw.y * front);
            var dest = enemy.GridPos + off;
            if (GridManager.Instance.GetTile(dest.x, dest.y) == null) continue;
            var occ = UnitManager.Instance.GetUnitAt(dest);
            if (occ != null && occ.Owner == enemy.Owner) continue;
            RentEnemy(new Vector3(dest.x * sp, 0.14f, dest.y * sp), ColP2Attack, 3001);
        }

        var ringPosE = GridManager.Instance.TileToWorld(enemy.GridPos.x, enemy.GridPos.y) + new Vector3(0f, 0.03f, 0f);
        RentEnemy(ringPosE, ColP2Ring, 3000);
    }

    void ClearEnemyPreview()
    {
        _enemyPreviewed = null;
        ReturnEnemyHl();
    }

    // 移動中またはクールダウン中の自ユニット数
    int CountActiveOwnUnits()
    {
        int count = 0;
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(LocalPlayer))
            if (u != null && !u.IsDead && (u.IsMoving || u.IsOnMoveCooldown)) count++;
        return count;
    }

    // CANTMOVE フィードバック: サウンド＋赤リングを短時間表示
    void ShowCantMoveFeedback(AnimalUnit unit)
    {
        SoundManager.Cancel();
        var pos = GridManager.Instance.TileToWorld(unit.GridPos.x, unit.GridPos.y);
        _cantMoveFlash.transform.position = pos + new Vector3(0f, 0.31f, 0f);
        _cantMoveFlash.SetActive(true);
        StartCoroutine(FlashAndHide(0.35f));
    }

    IEnumerator FlashAndHide(float duration)
    {
        yield return new WaitForSeconds(duration);
        if (_cantMoveFlash != null) _cantMoveFlash.SetActive(false);
    }

}
