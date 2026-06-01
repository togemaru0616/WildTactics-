using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class AICommander : MonoBehaviour
{
    // Two instances can coexist (P1 and P2 in simulation)
    public static AICommander Instance { get; private set; }

    public int Owner { get; private set; }
    public bool SimMode { get; private set; }

    AIWeightSet _wSet;

    [Header("思考設定")]
    [SerializeField, Range(0f, 1f)] float _depth2Discount = 0.6f;

    // ---- モード管理 ----
    enum AIMode { Explore, Combat }
    AIMode _mode = AIMode.Explore;
    float _lastCombatTime = -999f;
    const float COMBAT_COOLDOWN = 8f; // 敵を見失ってからExploreに戻るまでの秒数

    // 移動優先キュー（QueueBuildLoopが再構築、CommandLoopが消費）
    readonly List<(AnimalUnit unit, float score)> _moveQueue = new();

    // 深さ2シミュレーション用：1ユニットの仮移動先を保持
    AnimalUnit _simUnit;
    Vector2Int _simPos;


    // Fog state (per-instance, tracks what this AI's owner has seen)
    bool[,] _everSeen;
    float[,] _lastSeenTime;
    readonly HashSet<Vector2Int> _visible = new();
    readonly List<Vector2Int>   _campCache = new();

    // Enemy tracking
    readonly HashSet<AnimalUnit> _visibleEnemies = new();
    readonly Dictionary<AnimalUnit, Vector2Int> _lastKnown = new();
    bool _hasEverSeenEnemy;

    // Called by GameSimulator or GameSetup
    public void Init(int owner, AIWeightSet weights, bool simMode = false)
    {
        Owner = owner;
        _wSet = weights;
        SimMode = simMode;
    }

    void Start()
    {
        if (_wSet == null) Init(2, new AIWeightSet()); // default: P2 with stock weights
        if (!SimMode && Owner == 2) Instance = this;
        StartCoroutine(WaitForBattle());
    }

    IEnumerator WaitForBattle()
    {
        yield return new WaitUntil(() =>
            GameManager.Instance != null &&
            GameManager.Instance.Phase == GamePhase.Battle);

        int c = TerrainGenerator.COLS, r = TerrainGenerator.ROWS;
        _everSeen = new bool[c, r];
        _lastSeenTime = new float[c, r];
        for (int x = 0; x < c; x++)
            for (int z = 0; z < r; z++)
                _lastSeenTime[x, z] = -1000f;

        StartCoroutine(SightLoop());
        StartCoroutine(QueueBuildLoop());
        StartCoroutine(CommandLoop());
    }

    // ---- Vision ----

    IEnumerator SightLoop()
    {
        while (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Battle)
        {
            yield return SimMode ? null : new WaitForSeconds(0.25f);
            RefreshSight();
        }
    }

    void RefreshSight()
    {
        _visible.Clear();
        _visibleEnemies.Clear();

        foreach (var unit in UnitManager.Instance.GetUnitsOfOwner(Owner))
        {
            if (unit == null || unit.IsDead) continue;
            RevealAround(unit);
        }

        foreach (var pos in _visible)
        {
            _everSeen[pos.x, pos.y] = true;
            _lastSeenTime[pos.x, pos.y] = Time.time;
        }

        int enemy = Owner == 1 ? 2 : 1;
        foreach (var e in UnitManager.Instance.GetUnitsOfOwner(enemy))
        {
            if (e == null || e.IsDead) continue;
            if (_visible.Contains(e.GridPos))
            {
                _visibleEnemies.Add(e);
                _lastKnown[e] = e.GridPos;
            }
        }

        if (_visibleEnemies.Count > 0) _hasEverSeenEnemy = true;

        _campCache.Clear();
        foreach (var cp in GetKnownCampTiles()) _campCache.Add(cp);
    }

    void RevealAround(AnimalUnit unit)
    {
        int cx = unit.GridPos.x, cz = unit.GridPos.y;
        bool inForest = GridManager.Instance.GetTile(cx, cz)?.Type == TerrainType.Forest;
        int range = inForest
            ? AnimalDefinitions.GetForestViewRange(unit.AnimalType)
            : AnimalDefinitions.GetViewRange(unit.AnimalType);

        for (int dx = -range; dx <= range; dx++)
            for (int dz = -range; dz <= range; dz++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dz) > range) continue;
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nx >= TerrainGenerator.COLS) continue;
                if (nz < 0 || nz >= TerrainGenerator.ROWS) continue;
                _visible.Add(new Vector2Int(nx, nz));
            }
    }

    // ---- Command ----

    // キューが空になったら CanMove ユニットを行動スコア総和順で再構築する
    IEnumerator QueueBuildLoop()
    {
        var wait = new WaitForSeconds(0.05f);
        while (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Battle)
        {
            if (SimMode) yield return null;
            else yield return wait;

            if (GameManager.Instance == null || GameManager.Instance.Phase != GamePhase.Battle) yield break;
            if (_moveQueue.Count > 0) continue;

            foreach (var u in UnitManager.Instance.GetUnitsOfOwner(Owner))
            {
                if (u == null || u.IsDead || !u.CanMove) continue;
                _moveQueue.Add((u, ScoreMove(u, u.GridPos)));
            }

            _moveQueue.Sort((a, b) => a.score.CompareTo(b.score));
        }
    }

    // キューの先頭から1体ずつ探索・移動する（QueueBuildLoop が優先度を常に最新に保つ）
    IEnumerator CommandLoop()
    {
        while (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Battle)
        {
            yield return null;

            if (GameManager.Instance == null || GameManager.Instance.Phase != GamePhase.Battle) yield break;
            if (_moveQueue.Count == 0) continue;

            var (selected, bestScore) = _moveQueue[0];
            _moveQueue.RemoveAt(0);

            if (selected == null || selected.IsDead || !selected.CanMove) continue;

            Vector2Int dest = FallbackGreedy(selected);

            if (dest != selected.GridPos)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[AI P{Owner}] 移動: {selected.AnimalType} {selected.GridPos}→{dest}");
#endif
                selected.MoveTo(dest);
            }
        }
    }

    Vector2Int FallbackGreedy(AnimalUnit unit)
    {
        UpdateMode();
        bool d2 = ShouldUseDepth2(unit);
        var dest = unit.GridPos;
        float bestScore = float.MinValue;

        foreach (var d in GetValidMoves(unit))
        {
            float ms = ScoreMove(unit, d);
            float s  = d2 ? ms - _depth2Discount * EvalEnemyBestResponse(unit, d) : ms;
            if (s >= bestScore) { bestScore = s; dest = d; }
        }
        return dest;
    }

    // ---- Valid Moves ----

    // 仮想盤面での GetUnitAt：_simUnit が仮移動済みの場合はその位置を反映
    AnimalUnit GetUnitAtSim(Vector2Int pos)
    {
        if (_simUnit != null && !_simUnit.IsDead)
        {
            if (_simPos == pos) return _simUnit; // 移動先に存在
            if (_simUnit.GridPos == pos) return null;     // 元の位置は空
        }
        return UnitManager.Instance.GetUnitAt(pos);
    }

    // owner 視点の合法手生成。useSim=true のとき仮想盤面を参照
    List<Vector2Int> GetValidMovesFor(AnimalUnit unit, int owner, bool useSim = false)
    {
        var blocked = AnimalDefinitions.GetBlockedTerrain(unit.AnimalType);
        var offsets = AnimalDefinitions.GetMoveOffsets(unit.AnimalType);
        int fSign = owner == 1 ? 1 : -1;
        var result = new List<Vector2Int>();  // stay added at end so ties go to movement

        foreach (var raw in offsets)
        {
            var off = new Vector2Int(raw.x, raw.y * fSign);
            var dest = unit.GridPos + off;

            if (dest.x < 0 || dest.x >= TerrainGenerator.COLS) continue;
            if (dest.y < 0 || dest.y >= TerrainGenerator.ROWS) continue;

            var tile = GridManager.Instance.GetTile(dest.x, dest.y);
            if (tile == null) continue;
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) continue;

            if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
            {
                var mid = unit.GridPos + new Vector2Int(off.x / 2, off.y / 2);
                var mt = GridManager.Instance.GetTile(mid.x, mid.y);
                if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
            }

            var occupant = useSim ? GetUnitAtSim(dest) : UnitManager.Instance.GetUnitAt(dest);
            if (occupant != null && occupant.Owner == owner) continue; // 味方ブロック

            if (occupant != null && !occupant.IsDead)
            {
                // 突進: 2マス移動先に敵 → 押し出し可能なら合法手
                bool is2 = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2;
                if (!AnimalDefinitions.HasCharge(unit.AnimalType) || !is2) continue;
                var pushDir = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                               off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                if (!CanPushReal(occupant, dest + pushDir)) continue;
            }

            result.Add(dest);
        }
        result.Add(unit.GridPos); // stay evaluated last — ties go to first real move
        return result;
    }

    List<Vector2Int> GetValidMoves(AnimalUnit unit) => GetValidMovesFor(unit, Owner);

    // ---- Depth-2: 敵の最善応手を脅威スコアとして返す ----

    bool ShouldUseDepth2(AnimalUnit unit)
    {
        if (_visibleEnemies.Count == 0) return false;
        foreach (var e in _visibleEnemies)
            if (e != null && !e.IsDead && Manhattan(unit.GridPos, e.GridPos) <= 3) return true;
        return false;
    }

    float EvalEnemyBestResponse(AnimalUnit movedUnit, Vector2Int movedTo)
    {
        _simUnit = movedUnit;
        _simPos = movedTo;

        int enemy = Owner == 1 ? 2 : 1;
        int eFSign = enemy == 1 ? 1 : -1;
        float best = 0f;
        var ownFox = GetFox(Owner);
        var enFox = GetFox(enemy);

        foreach (var eu in UnitManager.Instance.GetUnitsOfOwner(enemy))
        {
            if (eu == null || eu.IsDead) continue;
            var ew = _wSet.Get(eu.AnimalType);

            foreach (var ed in GetValidMovesFor(eu, enemy, useSim: true))
            {
                float threat = 0f;

                // --- 通常攻撃: ed から ed + eFSign*offset にいる味方を攻撃 ---
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(eu.AnimalType))
                {
                    var apos = ed + new Vector2Int(raw.x, raw.y * eFSign);
                    if (apos.x < 0 || apos.x >= TerrainGenerator.COLS) continue;
                    if (apos.y < 0 || apos.y >= TerrainGenerator.ROWS) continue;
                    var tgt = GetUnitAtSim(apos);
                    if (tgt == null || tgt.Owner != Owner || tgt.IsDead) continue;

                    bool canWin = eu.AttackPower >= tgt.AttackPower
                               && eu.CurrentHP >= tgt.CurrentHP * 0.7f;
                    float atk = tgt.AnimalType == AnimalType.Fox
                        ? _wSet.SafeAttack * ew.FoxMult
                        : canWin ? _wSet.SafeAttack : -_wSet.SafeAttack * 0.25f;

                    int eAllies = CountAlliesAttackingPos(ed, enemy);
                    int oNear = CountAlliesAttackingPos(apos, Owner);
                    int ePower = eAllies * eAllies;
                    int oPower = oNear * oNear;
                    int powerDiff = ePower - oPower;
                    if (powerDiff > 0) atk += powerDiff * _wSet.NumericalPowerMult;
                    else if (powerDiff < 0) atk -= (-powerDiff) * _wSet.NumericalPowerMult * (10f / 3f);
                    threat += atk;
                }

                // --- 攻撃専用オフセット (チンパンジー中距離) ---
                foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(eu.AnimalType))
                {
                    var apos = ed + new Vector2Int(raw.x, raw.y * eFSign);
                    if (apos.x < 0 || apos.x >= TerrainGenerator.COLS) continue;
                    if (apos.y < 0 || apos.y >= TerrainGenerator.ROWS) continue;
                    var tgt = GetUnitAtSim(apos);
                    if (tgt == null || tgt.Owner != Owner || tgt.IsDead) continue;
                    float atk = tgt.AnimalType == AnimalType.Fox
                        ? _wSet.SafeAttack * ew.FoxMult
                        : _wSet.SafeAttack;
                    threat += atk;
                }

                // --- 突進着地: eu が2マス移動して味方を押し出す ---
                {
                    var off = ed - eu.GridPos;
                    bool is2 = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2;
                    if (is2 && AnimalDefinitions.HasCharge(eu.AnimalType))
                    {
                        var tgt = GetUnitAtSim(ed);
                        if (tgt != null && tgt.Owner == Owner && !tgt.IsDead)
                        {
                            int chargeDmg = AnimalDefinitions.GetChargeDamage(eu.AnimalType);
                            bool canWin = chargeDmg >= tgt.AttackPower
                                       && eu.CurrentHP >= tgt.CurrentHP * 0.7f;
                            float atk = tgt.AnimalType == AnimalType.Fox
                                ? _wSet.SafeAttack * ew.FoxMult
                                : canWin ? _wSet.SafeAttack : -_wSet.SafeAttack * 0.25f;
                            threat += atk;
                        }
                    }
                }

                // --- 灯台占領 ---
                var tile = GridManager.Instance.GetTile(ed.x, ed.y);
                if (tile != null && tile.Outpost == OutpostType.Lighthouse
                    && OutpostManager.Instance.GetOwner(ed) != enemy)
                    threat += _wSet.LighthouseCapture;

                // --- DefenderFoxDist: 敵が自分のキツネを守る ---
                if (enFox != null && !enFox.IsDead && eu.AnimalType != AnimalType.Fox)
                {
                    int d = Manhattan(ed, enFox.GridPos);
                    float gBase = _wSet.DefenderFoxDist * ew.FoxGuardMult;
                    threat += d <= 3 ? gBase
                            : d <= 5 ? gBase * (5 - d) / 2f
                            :         -gBase * 0.7f;
                }

                // --- FoxDangerPen: 敵のキツネが味方射程内に入る ---
                if (eu.AnimalType == AnimalType.Fox && CountOurThreatsTo(ed) > 0)
                    threat -= _wSet.FoxDangerPen;

                if (threat > best) best = threat;
            }
        }

        _simUnit = null;
        return best;
    }

    // ---- Scoring ----

    // モード遷移：可視敵の有無と最終接触時刻で決定
    void UpdateMode()
    {
        if (_visibleEnemies.Count > 0)
        {
            _mode = AIMode.Combat;
            if (!SimMode) _lastCombatTime = Time.time;
        }
        else if (!_hasEverSeenEnemy || (!SimMode && Time.time - _lastCombatTime > COMBAT_COOLDOWN))
        {
            _mode = AIMode.Explore;
        }
        // else: 敵を見失ったが接触から COMBAT_COOLDOWN 未満 → Combat のまま最終確認位置を追跡
    }

    float ScoreMove(AnimalUnit unit, Vector2Int dest)
    {
        UpdateMode();
        return _mode == AIMode.Explore ? ScoreExplore(unit, dest) : ScoreCombat(unit, dest);
    }

    // ---- 探索モード評価 ----

    float ScoreExplore(AnimalUnit unit, Vector2Int dest)
    {
        float s = CountNewVisibleTiles(unit, dest) * _wSet.ExploreNewTileWeight;

        // 未制圧の灯台に直接乗れるなら最優先
        var destTile = GridManager.Instance.GetTile(dest.x, dest.y);
        if (destTile != null && destTile.Outpost == OutpostType.Lighthouse
            && OutpostManager.Instance.GetOwner(dest) != Owner)
            s += _wSet.LighthouseCapture;

        // 最も近い未制圧灯台への勾配（sum でなく max で過剰評価を防ぐ）
        float bestLhGrad = 0f;
        foreach (var lh in GetKnownLighthouseTiles())
            if (OutpostManager.Instance.GetOwner(lh) != Owner)
                bestLhGrad = Mathf.Max(bestLhGrad, _wSet.ExploreLhGradient / (Manhattan(dest, lh) + 1f));
        s += bestLhGrad;

        // 探索中に隊列を維持（孤立を避ける）
        s += CountUnitsNear(dest, 3, Owner) * _wSet.ExploreFormationBonus;

        // 機会損失リスク：移動中は攻撃できない（moveDuration × 0.67s の無防備時間）
        // 最終確認済みの敵射程内に移動するとその時間分の損失をペナルティ化
        if (dest != unit.GridPos && _lastKnown.Count > 0)
        {
            int enemy = Owner == 1 ? 2 : 1;
            int eFSign = enemy == 1 ? 1 : -1;
            float exposure = AnimalDefinitions.GetMoveTime(unit.AnimalType) * 0.67f;

            foreach (var kvp in _lastKnown)
            {
                var e = kvp.Key;
                var lastPos = kvp.Value;
                if (e == null || e.IsDead) continue;

                // 情報の鮮度（10秒以上経過した情報は無視）
                float staleness = SimMode ? 0f
                    : Mathf.Clamp01((Time.time - _lastSeenTime[lastPos.x, lastPos.y]) / 10f);
                if (staleness >= 1f) continue;

                foreach (var raw in AnimalDefinitions.GetMoveOffsets(e.AnimalType))
                {
                    if (lastPos + new Vector2Int(raw.x, raw.y * eFSign) == dest)
                    {
                        s -= exposure * _wSet.ExploreExposurePen * (1f - staleness);
                        break;
                    }
                }
            }
        }

        return s;
    }

    // ---- 戦闘モード評価 ----

    float ScoreCombat(AnimalUnit unit, Vector2Int dest)
    {
        // 敵が視野外の場合：最終確認位置へ追跡
        if (_visibleEnemies.Count == 0)
        {
            float score = 0f;
            foreach (var kvp in _lastKnown)
            {
                if (kvp.Key == null || kvp.Key.IsDead) continue;
                if (Manhattan(dest, kvp.Value) < Manhattan(unit.GridPos, kvp.Value))
                    score += _wSet.SeekLastKnownBonus;
            }
            float bestLhG = 0f;
            foreach (var lh in GetKnownLighthouseTiles())
                if (OutpostManager.Instance.GetOwner(lh) != Owner)
                    bestLhG = Mathf.Max(bestLhG, _wSet.ExploreLhGradient / (Manhattan(dest, lh) + 1f));
            return score + bestLhG;
        }

        return CombatScorer.Score(
            unit.AnimalType, unit.AttackPower, unit.CurrentHP, unit.MaxHP,
            unit.GridPos, dest,
            new CombatCtx(this, unit));
    }

    bool CanPushReal(AnimalUnit enemy, Vector2Int pushDest)
    {
        if (enemy.AnimalType == AnimalType.Elephant) return false;
        if (pushDest.x < 0 || pushDest.x >= TerrainGenerator.COLS) return false;
        if (pushDest.y < 0 || pushDest.y >= TerrainGenerator.ROWS) return false;
        var tile = GridManager.Instance.GetTile(pushDest.x, pushDest.y);
        if (tile == null) return false;
        var blocked = AnimalDefinitions.GetBlockedTerrain(enemy.AnimalType);
        if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) return false;
        var occ = UnitManager.Instance.GetUnitAt(pushDest);
        return occ == null || occ.IsDead;
    }

    IEnumerable<Vector2Int> GetKnownLighthouseTiles()
    {
        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
            {
                if (!_everSeen[x, z] && !_visible.Contains(new Vector2Int(x, z))) continue;
                var tile = GridManager.Instance.GetTile(x, z);
                if (tile?.Outpost == OutpostType.Lighthouse)
                    yield return new Vector2Int(x, z);
            }
    }

    IEnumerable<Vector2Int> GetKnownCampTiles()
    {
        for (int x = 0; x < TerrainGenerator.COLS; x++)
            for (int z = 0; z < TerrainGenerator.ROWS; z++)
            {
                if (!_everSeen[x, z] && !_visible.Contains(new Vector2Int(x, z))) continue;
                var tile = GridManager.Instance.GetTile(x, z);
                if (tile?.Outpost == OutpostType.Camp)
                    yield return new Vector2Int(x, z);
            }
    }


    // ---- Helpers ----

    List<AnimalUnit> GetEnemiesFromPos(AnimalUnit unit, Vector2Int from)
    {
        var blocked = AnimalDefinitions.GetBlockedTerrain(unit.AnimalType);
        var offsets = AnimalDefinitions.GetMoveOffsets(unit.AnimalType);
        int fSign = Owner == 1 ? 1 : -1;
        var result = new List<AnimalUnit>();
        int enemy = Owner == 1 ? 2 : 1;

        foreach (var raw in offsets)
        {
            var off = new Vector2Int(raw.x, raw.y * fSign);
            var pos = from + off;
            if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) continue;
            if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) continue;

            var tile = GridManager.Instance.GetTile(pos.x, pos.y);
            if (tile == null) continue;

            if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
            {
                var mid = from + new Vector2Int(off.x / 2, off.y / 2);
                var mt = GridManager.Instance.GetTile(mid.x, mid.y);
                if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
            }

            var u = UnitManager.Instance.GetUnitAt(pos);
            if (u != null && u.Owner == enemy && !u.IsDead) result.Add(u);
        }

        // 攻撃専用オフセット（チンパンジー中距離攻撃）
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(unit.AnimalType))
        {
            var pos = from + new Vector2Int(raw.x, raw.y * fSign);
            if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) continue;
            if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) continue;
            var u = UnitManager.Instance.GetUnitAt(pos);
            if (u != null && u.Owner == enemy && !u.IsDead) result.Add(u);
        }

        return result;
    }

    int CountEnemyThreats(Vector2Int dest, int enemyOwner)
    {
        int eFSign = enemyOwner == 1 ? 1 : -1;
        int count = 0;
        foreach (var e in _visibleEnemies)
        {
            if (e == null || e.IsDead) continue;
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(e.AnimalType))
                if (e.GridPos + new Vector2Int(raw.x, raw.y * eFSign) == dest) { count++; break; }
        }
        return count;
    }


    int CountOurThreatsTo(Vector2Int dest)
    {
        int fSign = Owner == 1 ? 1 : -1;
        int count = 0;
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(Owner))
        {
            if (u == null || u.IsDead) continue;
            var uPos = (u == _simUnit) ? _simPos : u.GridPos;
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                if (uPos + new Vector2Int(raw.x, raw.y * fSign) == dest) { count++; break; }
        }
        return count;
    }




    int CountNewVisibleTiles(AnimalUnit unit, Vector2Int from)
    {
        bool inForest = GridManager.Instance.GetTile(from.x, from.y)?.Type == TerrainType.Forest;
        int range = inForest
            ? AnimalDefinitions.GetForestViewRange(unit.AnimalType)
            : AnimalDefinitions.GetViewRange(unit.AnimalType);

        int count = 0;
        for (int dx = -range; dx <= range; dx++)
            for (int dz = -range; dz <= range; dz++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dz) > range) continue;
                int nx = from.x + dx, nz = from.y + dz;
                if (nx < 0 || nx >= TerrainGenerator.COLS) continue;
                if (nz < 0 || nz >= TerrainGenerator.ROWS) continue;
                if (!_everSeen[nx, nz]) count++;
            }
        return count;
    }

    AnimalUnit GetFox(int ownerSide)
    {
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(ownerSide))
            if (u != null && !u.IsDead && u.AnimalType == AnimalType.Fox) return u;
        return null;
    }

    int CountUnitsNear(Vector2Int center, int radius, int ownerSide)
    {
        int count = 0;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                var u = UnitManager.Instance.GetUnitAt(center + new Vector2Int(dx, dz));
                if (u != null && u.Owner == ownerSide && !u.IsDead) count++;
            }
        return count;
    }

    int CountAlliesAttackingPos(Vector2Int targetPos, int ownerSide)
    {
        int count = 0;
        foreach (var u in UnitManager.Instance.GetUnitsOfOwner(ownerSide))
        {
            if (u == null || u.IsDead) continue;
            var pos = (u == _simUnit) ? _simPos : u.GridPos;
            if (CanAllyAttackFromPos(u, pos, targetPos)) count++;
        }
        return count;
    }

    bool CanAllyAttackFromPos(AnimalUnit ally, Vector2Int allyPos, Vector2Int targetPos)
    {
        int fSign = ally.Owner == 1 ? 1 : -1;
        var blocked = AnimalDefinitions.GetBlockedTerrain(ally.AnimalType);
        foreach (var raw in AnimalDefinitions.GetMoveOffsets(ally.AnimalType))
        {
            var off = new Vector2Int(raw.x, raw.y * fSign);
            if (allyPos + off != targetPos) continue;
            if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
            {
                var mid = allyPos + new Vector2Int(off.x / 2, off.y / 2);
                var mt = GridManager.Instance.GetTile(mid.x, mid.y);
                if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
            }
            return true;
        }
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(ally.AnimalType))
            if (allyPos + new Vector2Int(raw.x, raw.y * fSign) == targetPos) return true;
        return false;
    }

    static int Manhattan(Vector2Int a, Vector2Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    // ---- ICombatContext implementation ----

    readonly struct CombatCtx : ICombatContext
    {
        readonly AICommander _ai;
        readonly AnimalUnit  _unit;

        public CombatCtx(AICommander ai, AnimalUnit unit) { _ai = ai; _unit = unit; }

        public int         Owner             => _ai.Owner;
        public int         Enemy             => _ai.Owner == 1 ? 2 : 1;
        public AIWeightSet WSet              => _ai._wSet;
        public bool        HasVisibleEnemies => _ai._visibleEnemies.Count > 0;

        public void FillAttackable(Vector2Int from, AnimalType unitType, List<CombatTarget> buf)
        {
            var blocked = AnimalDefinitions.GetBlockedTerrain(unitType);
            int fSign   = Owner == 1 ? 1 : -1;
            int enemy   = Enemy;

            foreach (var raw in AnimalDefinitions.GetMoveOffsets(unitType))
            {
                var off = new Vector2Int(raw.x, raw.y * fSign);
                var pos = from + off;
                if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) continue;
                if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) continue;
                if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
                {
                    var mid = from + new Vector2Int(off.x / 2, off.y / 2);
                    var mt  = GridManager.Instance.GetTile(mid.x, mid.y);
                    if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
                }
                var u = UnitManager.Instance.GetUnitAt(pos);
                if (u == null || u.Owner != enemy || u.IsDead) continue;
                buf.Add(new CombatTarget
                {
                    Type = u.AnimalType, Owner = u.Owner,
                    AttackPower = u.AttackPower, CurrentHP = u.CurrentHP, MaxHP = u.MaxHP,
                    Pos = u.GridPos,
                    IsRanged = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2,
                });
            }
            foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(unitType))
            {
                var pos = from + new Vector2Int(raw.x, raw.y * fSign);
                if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) continue;
                if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) continue;
                var u = UnitManager.Instance.GetUnitAt(pos);
                if (u == null || u.Owner != enemy || u.IsDead) continue;
                buf.Add(new CombatTarget
                {
                    Type = u.AnimalType, Owner = u.Owner,
                    AttackPower = u.AttackPower, CurrentHP = u.CurrentHP, MaxHP = u.MaxHP,
                    Pos = u.GridPos,
                    IsRanged = true, IsAttackOnly = true,
                });
            }
        }

        public bool IsVisible(Vector2Int pos) => _ai._visible.Contains(pos);

        public float CountAlliesCanAttack(Vector2Int targetPos, Vector2Int selfDest, out int combinedDamage)
        {
            float power = 0f;
            combinedDamage = 0;
            foreach (var ally in UnitManager.Instance.GetUnitsOfOwner(Owner))
            {
                if (ally == null || ally.IsDead) continue;
                var aPos = ally == _unit ? selfDest : ally.GridPos;
                if (_ai.CanAllyAttackFromPos(ally, aPos, targetPos))
                {
                    power += (float)ally.CurrentHP / ally.MaxHP;
                    combinedDamage += ally.AttackPower;
                }
            }
            return power;
        }

        public float CountEnemiesCanAttack(Vector2Int targetPos)
        {
            int eFSign = Enemy == 1 ? 1 : -1;
            float power = 0f;
            foreach (var e in _ai._visibleEnemies)
            {
                if (e == null || e.IsDead) continue;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(e.AnimalType))
                    if (e.GridPos + new Vector2Int(raw.x, raw.y * eFSign) == targetPos)
                    {
                        power += (float)e.CurrentHP / e.MaxHP;
                        break;
                    }
            }
            return power;
        }

        public bool TryGetEnemyAt(Vector2Int pos, out CombatTarget target)
        {
            var u = UnitManager.Instance.GetUnitAt(pos);
            if (u != null && u.Owner == Enemy && !u.IsDead && _ai._visibleEnemies.Contains(u))
            {
                target = new CombatTarget
                {
                    Type = u.AnimalType, Owner = u.Owner,
                    AttackPower = u.AttackPower, CurrentHP = u.CurrentHP, MaxHP = u.MaxHP,
                    Pos = u.GridPos,
                };
                return true;
            }
            target = default;
            return false;
        }

        public bool CanPush(AnimalType targetType, Vector2Int pushDest)
        {
            if (targetType == AnimalType.Elephant) return false;
            if (pushDest.x < 0 || pushDest.x >= TerrainGenerator.COLS) return false;
            if (pushDest.y < 0 || pushDest.y >= TerrainGenerator.ROWS) return false;
            var tile = GridManager.Instance.GetTile(pushDest.x, pushDest.y);
            if (tile == null) return false;
            var blocked = AnimalDefinitions.GetBlockedTerrain(targetType);
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) return false;
            var occ = UnitManager.Instance.GetUnitAt(pushDest);
            return occ == null || occ.IsDead;
        }

        public bool TryGetFoxPos(int owner, out Vector2Int foxPos)
        {
            foreach (var u in UnitManager.Instance.GetUnitsOfOwner(owner))
                if (u != null && !u.IsDead && u.AnimalType == AnimalType.Fox)
                    { foxPos = u.GridPos; return true; }
            foxPos = default;
            return false;
        }

        public int CountAlliesNear(Vector2Int center, int radius, int owner)
        {
            int count = 0;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                var u = UnitManager.Instance.GetUnitAt(center + new Vector2Int(dx, dz));
                if (u != null && u.Owner == owner && !u.IsDead) count++;
            }
            return count;
        }

        public TerrainType GetTerrain(Vector2Int pos)
            => GridManager.Instance.GetTile(pos.x, pos.y)?.Type ?? TerrainType.Flat;

        public IReadOnlyList<Vector2Int> GetKnownCamps() => _ai._campCache;

        public int GetTeamHP(int owner)
        {
            int hp = 0;
            foreach (var u in UnitManager.Instance.GetUnitsOfOwner(owner))
                if (u != null && !u.IsDead) hp += u.CurrentHP;
            return hp;
        }

        public float GetTeamZCenter(int owner)
        {
            float sum = 0f; int count = 0;
            foreach (var u in UnitManager.Instance.GetUnitsOfOwner(owner))
            {
                if (u == null || u.IsDead) continue;
                sum += u.GridPos.y;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        public int NearestEnemyDistFrom(Vector2Int from)
        {
            int nearest = int.MaxValue;
            foreach (var e in _ai._visibleEnemies)
            {
                if (e == null || e.IsDead) continue;
                int d = Mathf.Abs(from.x - e.GridPos.x) + Mathf.Abs(from.y - e.GridPos.y);
                if (d < nearest) nearest = d;
            }
            return nearest;
        }

        public void FillEnemiesThatCanReach(Vector2Int dest, List<CombatTarget> buf)
        {
            int eFSign = Enemy == 1 ? 1 : -1;
            foreach (var e in _ai._visibleEnemies)
            {
                if (e == null || e.IsDead) continue;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(e.AnimalType))
                {
                    if (e.GridPos + new Vector2Int(raw.x, raw.y * eFSign) == dest)
                    {
                        buf.Add(new CombatTarget
                        {
                            Type = e.AnimalType, Owner = e.Owner,
                            AttackPower = e.AttackPower, CurrentHP = e.CurrentHP, MaxHP = e.MaxHP,
                            Pos = e.GridPos,
                        });
                        break;
                    }
                }
            }
        }

        public int CountAlliesReachableBy(AnimalType attackerType, Vector2Int attackerPos, Vector2Int selfDest)
        {
            int eFSign = Enemy == 1 ? 1 : -1, n = 0;
            foreach (var ally in UnitManager.Instance.GetUnitsOfOwner(Owner))
            {
                if (ally == null || ally.IsDead) continue;
                var allyPos = ally == _unit ? selfDest : ally.GridPos;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(attackerType))
                    if (attackerPos + new Vector2Int(raw.x, raw.y * eFSign) == allyPos) { n++; break; }
            }
            return n;
        }

        public float GetMoveTime(AnimalType type) => AnimalDefinitions.GetMoveTime(type);
    }

}
