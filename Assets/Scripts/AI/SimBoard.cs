using UnityEngine;
using System;
using System.Collections.Generic;

// UnityEngineへの依存はVector2Int（値型）とAnimalDefinitions（staticロジック）のみ
// シミュレーション中はGameObjectを一切生成しない
public struct SimTile
{
    public TerrainType Type;
    public OutpostType Outpost;
}

public class SimBoard
{
    const int MAX_UNITS = 22;

    // 地形（Resetごとに参照差し替え、読み取り専用）
    SimTile[,] _tiles;
    readonly int _cols, _rows;

    // 試合ごとの可変状態（再利用）
    readonly SimUnit[] _units   = new SimUnit[MAX_UNITS];
    int _unitCount;
    readonly int[,]  _outpost;   // 0=neutral 1=P1 2=P2
    readonly bool[,] _seen1, _seen2;

    AIWeightSet _wA, _wB;

    // Reset ごとにキャッシュする灯台・キャンプ座標
    readonly List<Vector2Int> _lighthouses = new(8);
    readonly List<Vector2Int> _camps       = new(4);

    // 合法手計算用一時バッファ（スレッドごとにSimBoardを持つので競合なし）
    readonly List<Vector2Int> _moveBuf      = new(16);
    readonly List<Vector2Int> _enemyMoveBuf = new(16);

    // Depth-2: 仮移動ユニットのインデックスと移動先
    int        _simIdx = -1;
    Vector2Int _simPos;

    public float Depth2Discount = 0.6f;

    public SimBoard()
    {
        _cols    = TerrainGenerator.COLS;
        _rows    = TerrainGenerator.ROWS;
        _outpost = new int [_cols, _rows];
        _seen1   = new bool[_cols, _rows];
        _seen2   = new bool[_cols, _rows];
    }

    // 0 = 制限なし（通常）, 1 or 2 = そのプレイヤーに全視界を与える
    public int FullVisionOwner = 0;

    readonly System.Random _rng = new();

    public void Reset(SimTile[,] tiles, SimUnit[] units, int unitCount,
                      AIWeightSet wA, AIWeightSet wB)
    {
        _tiles     = tiles;
        _unitCount = unitCount;
        for (int i = 0; i < unitCount; i++) _units[i] = units[i];
        _wA = wA;
        _wB = wB;
        Array.Clear(_outpost, 0, _outpost.Length);
        Array.Clear(_seen1,   0, _seen1.Length);
        Array.Clear(_seen2,   0, _seen2.Length);
        for (int i = 0; i < MAX_UNITS; i++) _lastAttackTarget[i] = -1;

        // 灯台・キャンプ座標をキャッシュ（ScoreMove で毎回全スキャンしないため）
        _lighthouses.Clear();
        _camps.Clear();
        for (int x = 0; x < _cols; x++)
        for (int z = 0; z < _rows; z++)
        {
            if (tiles[x, z].Outpost == OutpostType.Lighthouse)
                _lighthouses.Add(new Vector2Int(x, z));
            else if (tiles[x, z].Outpost == OutpostType.Camp)
                _camps.Add(new Vector2Int(x, z));
        }
    }

    // ---- 局面評価関数（非線形・ロールアウト不要） ----

    float EvalPosition(int owner)
    {
        int   enemy = 3 - owner;
        var   w     = owner == 1 ? _wA : _wB;
        float score = 0f;

        // ── 1. HP と数的優位 ──────────────────────────────────────
        int hOwn = 0, hEnemy = 0, cOwn = 0, cEnemy = 0;
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead) continue;
            if (u.Owner == owner) { hOwn += u.CurrentHP; cOwn++; }
            else                  { hEnemy += u.CurrentHP; cEnemy++; }
        }
        int totalH = hOwn + hEnemy;

        // HP 優位: tanh で非線形スケーリング（小差は緩やか、大差は加速）
        if (totalH > 0)
        {
            float hpRatio = (float)(hOwn - hEnemy) / totalH;
            score += (float)Math.Tanh(hpRatio * 2.5) * 220f;
        }

        // 数的優位: ランチェスターの2乗則（元から非線形）
        score += (cOwn * cOwn - cEnemy * cEnemy) * w.NumericalPowerMult * (5f / 3f);

        // 複合優位ボーナス（連続関数: 両優位の積に比例）
        {
            float hpAdv2  = totalH > 0 ? (float)(hOwn - hEnemy) / totalH : 0f;
            float cntAdv2 = (float)(cOwn - cEnemy) / (cOwn + cEnemy + 1);
            score += Math.Max(0f, hpAdv2) * Math.Max(0f, cntAdv2) * w.DualAdvantageBonus;
        }

        // ── 2. 灯台占有（tanh で過半数制圧を非線形に評価） ──────
        int lhOwn = 0, lhEnemy = 0;
        foreach (var lh in _lighthouses)
        {
            int o = _outpost[lh.x, lh.y];
            if      (o == owner) lhOwn++;
            else if (o == enemy) lhEnemy++;
        }
        if (_lighthouses.Count > 0)
        {
            float lhRatio = (float)(lhOwn - lhEnemy) / _lighthouses.Count;
            score += (float)Math.Tanh(lhRatio * 3.0) * w.LighthouseCapture * 0.7f;
        }

        // ── 3. Fox 生存・距離危険（指数減衰で非線形） ───────────
        int foxIdx = FindFox(owner);
        if (foxIdx >= 0)
        {
            ref var fox     = ref _units[foxIdx];
            float   foxHpR  = (float)fox.CurrentHP / fox.MaxHP;
            score += 100f + foxHpR * 50f; // 生存ボーナス + HP 残量ボーナス

            // 各敵との距離を指数減衰でリスク換算（近いほど急激に危険）
            for (int i = 0; i < _unitCount; i++)
            {
                ref var e = ref _units[i];
                if (e.IsDead || e.Owner != enemy) continue;
                int d = Manhattan(e.Pos, fox.Pos);
                // exp(-d*0.6)：d=0→1.0 / d=1→0.55 / d=2→0.30 / d=3→0.16 / d=5→0.05
                score -= w.FoxDangerPen * (float)Math.Exp(-d * 0.6) * (2f - foxHpR);
            }
        }
        if (FindFox(enemy) < 0) score += w.FoxKillReward;

        return score;
    }

    // ピボットユニット以外の全味方ユニットに貪欲手を適用
    void ApplyGreedyMovesExcept(int excludeUnitId, int owner)
    {
        var seen  = owner == 1 ? _seen1 : _seen2;
        var w     = owner == 1 ? _wA    : _wB;
        int enemy = 3 - owner;

        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.Owner != owner || u.Id == excludeUnitId) continue;

            GetValidMoves(ref u, owner, _moveBuf);

            float bestScore = float.MinValue;
            var bestDest = u.Pos;

            foreach (var dest in _moveBuf)
            {
                float s = ScoreMove(ref u, dest, owner, enemy, seen, w);
                if (s > bestScore) { bestScore = s; bestDest = dest; }
            }

            if (bestDest != u.Pos) ApplyMove(i, bestDest);
        }
    }

    // ---- メインループ（イベント駆動リアルタイムシミュレーション） ----
    // 各ユニットが個別タイマーを持ち、最小タイマーのユニットから順に行動する。
    // 速いユニット（Horse等）は遅いユニット（Elephant等）より多く行動できる。

    public int RunToEnd(int maxTicks)
    {
        // タイマー初期化：同時行動を避けるためランダムにずらす
        for (int i = 0; i < _unitCount; i++)
        {
            if (_units[i].IsDead) continue;
            float full = AnimalDefinitions.GetMoveTime(_units[i].AnimalType);
            _units[i].MoveTimer   = (float)(_rng.NextDouble() * full);
            _units[i].AttackTimer = (float)(_rng.NextDouble() * 2f);
        }

        float gameClock  = 0f;
        float nextPoison = 6f;  // 毒・キャンプ回復は6秒ごと

        for (int ev = 0; ev < maxTicks; ev++)
        {
            // MoveTimer・AttackTimer 両方から最小を選択
            float minMoveTime   = float.MaxValue;
            float minAttackTime = float.MaxValue;
            int   minMoveIdx    = -1;
            int   minAttackIdx  = -1;
            for (int i = 0; i < _unitCount; i++)
            {
                if (_units[i].IsDead) continue;
                if (_units[i].MoveTimer   < minMoveTime)   { minMoveTime   = _units[i].MoveTimer;   minMoveIdx   = i; }
                if (_units[i].AttackTimer < minAttackTime) { minAttackTime = _units[i].AttackTimer; minAttackIdx = i; }
            }
            if (minMoveIdx < 0 && minAttackIdx < 0) break;

            bool isMoveEvent = minAttackIdx < 0 || (minMoveIdx >= 0 && minMoveTime <= minAttackTime);
            int   nextIdx    = isMoveEvent ? minMoveIdx   : minAttackIdx;
            float minTime    = isMoveEvent ? minMoveTime  : minAttackTime;

            // ゲーム時間を進め、全ユニットの両タイマーを減算
            gameClock += minTime;
            for (int i = 0; i < _unitCount; i++)
            {
                if (_units[i].IsDead) continue;
                _units[i].MoveTimer   -= minTime;
                _units[i].AttackTimer -= minTime;
            }

            // 毒ダメージ・キャンプ回復（時間ベース）
            while (gameClock >= nextPoison)
            {
                TickPoisonPhase();
                TickCampHealPhase();
                nextPoison += 6f;
            }

            RefreshSight(1, _seen1);
            RefreshSight(2, _seen2);

            if (isMoveEvent)
            {
                // 移動イベント：移動先を選択・適用（攻撃は AttackEvent に委譲）
                Vector2Int posBeforeAct = _units[nextIdx].Pos;
                ActUnit(nextIdx);

                // タイマーリセット（実ゲームの AnimalUnit.MoveTo に合わせる）
                ref var u       = ref _units[nextIdx];
                float   mult    = AnimalDefinitions.GetTerrainMult(u.AnimalType, _tiles[u.Pos.x, u.Pos.y].Type);
                var     off     = u.Pos - posBeforeAct;
                bool    actuallyMoved = off.x != 0 || off.y != 0;
                bool    is2     = Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2;
                float   moveDur = AnimalDefinitions.GetMoveTime(u.AnimalType) * mult * (is2 ? 1.5f : 1f);
                _units[nextIdx].MoveTimer = moveDur;
                // 実ゲームでは MoveTo() 内でのみ AttackTimer をリセット。待機時はリセットしない
                if (actuallyMoved)
                    _units[nextIdx].AttackTimer = moveDur * 0.67f;
            }
            else
            {
                // 攻撃イベント：AutoAttackLoop 相当（移動なし、待機中の自動攻撃）
                SimulateSingleAttack(nextIdx, _units[nextIdx].Owner);
                _units[nextIdx].AttackTimer = 2f;
            }

            int w = CheckWinner();
            if (w >= 0) return w;

            // 早期打ち切り：HP 4:1以上の差
            if (ev % 100 == 0)
            {
                int h1 = 0, h2 = 0;
                for (int i = 0; i < _unitCount; i++)
                {
                    if (_units[i].IsDead) continue;
                    if (_units[i].Owner == 1) h1 += _units[i].CurrentHP;
                    else                       h2 += _units[i].CurrentHP;
                }
                if (h1 > h2 * 4) return 1;
                if (h2 > h1 * 4) return 2;
            }
        }
        return ResolveByHP();
    }

    // 1ユニットの攻撃をシミュレート（将棋AIの1プライ用）
    void SimulateSingleAttack(int unitIdx, int owner)
    {
        if (_justCharged[unitIdx]) { _justCharged[unitIdx] = false; return; }
        ref var attacker = ref _units[unitIdx];
        if (attacker.IsDead) return;

        int enemy = 3 - owner;
        int fSign = owner == 1 ? 1 : -1;

        // 攻撃範囲内の敵インデックスをすべて収集
        _attackRangeBuf.Clear();
        foreach (var raw in AnimalDefinitions.GetMoveOffsets(attacker.AnimalType))
        {
            var ap = attacker.Pos + new Vector2Int(raw.x, raw.y * fSign);
            if ((uint)ap.x >= (uint)_cols || (uint)ap.y >= (uint)_rows) continue;
            int ti = FindUnitAt(ap, enemy);
            if (ti >= 0 && !_units[ti].IsDead) _attackRangeBuf.Add(ti);
        }
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(attacker.AnimalType))
        {
            var ap = attacker.Pos + new Vector2Int(raw.x, raw.y * fSign);
            if ((uint)ap.x >= (uint)_cols || (uint)ap.y >= (uint)_rows) continue;
            int ti = FindUnitAt(ap, enemy);
            if (ti >= 0 && !_units[ti].IsDead) _attackRangeBuf.Add(ti);
        }
        if (_attackRangeBuf.Count == 0) return;

        // 実ゲームの PickTarget に合わせる: 継続ターゲット優先 → 最小HP
        int bestTarget = -1, bestHP = int.MaxValue;
        int last = _lastAttackTarget[unitIdx];
        if (last >= 0 && last < _unitCount && !_units[last].IsDead && _attackRangeBuf.Contains(last))
        {
            bestTarget = last;
        }
        if (bestTarget < 0)
        {
            foreach (int ti in _attackRangeBuf)
                if (_units[ti].CurrentHP < bestHP) { bestTarget = ti; bestHP = _units[ti].CurrentHP; }
        }

        if (bestTarget < 0) return;
        _lastAttackTarget[unitIdx] = bestTarget;
        ref var target = ref _units[bestTarget];
        target.CurrentHP -= attacker.AttackPower;
        if (target.CurrentHP <= 0) { target.IsDead = true; target.CurrentHP = 0; }
        if (attacker.AnimalType == AnimalType.Snake && !target.IsDead)
            target.PoisonedTurns = Math.Max(target.PoisonedTurns, AnimalDefinitions.SnakePoisonDuration);
    }

    // ---- 視界更新 ----

    void RefreshSight(int owner, bool[,] seen)
    {
        if (FullVisionOwner == owner)
        {
            for (int x = 0; x < _cols; x++)
                for (int z = 0; z < _rows; z++)
                    seen[x, z] = true;
            return;
        }

        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.Owner != owner) continue;
            bool inForest = _tiles[u.Pos.x, u.Pos.y].Type == TerrainType.Forest;
            int range = inForest
                ? AnimalDefinitions.GetForestViewRange(u.AnimalType)
                : AnimalDefinitions.GetViewRange(u.AnimalType);
            for (int dx = -range; dx <= range; dx++)
            for (int dz = -range; dz <= range; dz++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) > range) continue;
                int nx = u.Pos.x + dx, nz = u.Pos.y + dz;
                if ((uint)nx < (uint)_cols && (uint)nz < (uint)_rows)
                    seen[nx, nz] = true;
            }
        }
    }

    // ---- AI：指定ユニットを1体動かして攻撃する ----
    // RunToEnd のイベント駆動ループから呼ばれる。タイマーで選ばれたユニット専用。

    void ActUnit(int unitIdx)
    {
        ref var u     = ref _units[unitIdx];
        int     owner = u.Owner;
        int     enemy = 3 - owner;
        var     seen  = owner == 1 ? _seen1 : _seen2;
        var     w     = owner == 1 ? _wA    : _wB;

        Vector2Int bestDest = u.Pos;
        float      bestPri  = float.MinValue;

        GetValidMoves(ref u, owner, _moveBuf);

        bool d2 = ShouldUseDepth2(unitIdx, owner, enemy, seen);
        foreach (var dest in _moveBuf)
        {
            float ms  = ScoreMove(ref u, dest, owner, enemy, seen, w);
            float pri = d2 ? ms - Depth2Discount * EvalEnemyBestResponse(unitIdx, dest, owner, enemy, w) : ms;
            if (pri > bestPri) { bestPri = pri; bestDest = dest; }
        }

        // 移動または灯台占領
        if (bestDest == u.Pos)
        {
            if (_tiles[u.Pos.x, u.Pos.y].Outpost == OutpostType.Lighthouse
                && _outpost[u.Pos.x, u.Pos.y] != owner)
                _outpost[u.Pos.x, u.Pos.y] = owner;
        }
        else
        {
            ApplyMove(unitIdx, bestDest);
        }
        // 攻撃は AttackEvent（AutoAttackLoop相当）に任せる
    }

    // チャージ追跡（TurnAttackPhaseでダメージに使用）
    readonly bool[] _justCharged = new bool[MAX_UNITS];

    // 継続ターゲット追跡（実ゲームの _lastTarget 相当）
    readonly int[]  _lastAttackTarget  = new int[MAX_UNITS];
    readonly List<int> _attackRangeBuf = new(8);

    void ApplyMove(int idx, Vector2Int dest)
    {
        ref var mover = ref _units[idx];
        var     off   = dest - mover.Pos;
        bool    is2   = Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2;

        _justCharged[idx] = false;

        // 突進: 敵を押し出してダメージ
        if (AnimalDefinitions.HasCharge(mover.AnimalType) && is2)
        {
            int eIdx = FindUnitAt(dest, 3 - mover.Owner);
            if (eIdx >= 0 && !_units[eIdx].IsDead)
            {
                var pushDir = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                              off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                _units[eIdx].Pos        = dest + pushDir;
                _units[eIdx].CurrentHP -= AnimalDefinitions.GetChargeDamage(mover.AnimalType);
                if (_units[eIdx].CurrentHP <= 0) { _units[eIdx].CurrentHP = 0; _units[eIdx].IsDead = true; }
                _justCharged[idx] = true; // 突進済み → TurnAttackPhase をスキップ
            }
        }

        mover.Pos = dest;
    }

    bool CanPushSim(int enemyIdx, Vector2Int pushDest)
    {
        ref var enemy = ref _units[enemyIdx];
        if (enemy.AnimalType == AnimalType.Elephant) return false;
        if ((uint)pushDest.x >= (uint)_cols || (uint)pushDest.y >= (uint)_rows) return false;
        var tile    = _tiles[pushDest.x, pushDest.y];
        var blocked = AnimalDefinitions.GetBlockedTerrain(enemy.AnimalType);
        if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) return false;
        for (int i = 0; i < _unitCount; i++)
        {
            if (i == enemyIdx) continue;
            if (!_units[i].IsDead && _units[i].Pos == pushDest) return false;
        }
        return true;
    }


    // 実ゲームのTriggerTurnAttack()と同一：ターン終了時に全ユニットが攻撃
    void TurnAttackPhase(int owner)
    {
        int enemy = owner == 1 ? 2 : 1;
        int fSign = owner == 1 ? 1 : -1;

        for (int i = 0; i < _unitCount; i++)
        {
            ref var attacker = ref _units[i];
            if (attacker.IsDead || attacker.Owner != owner) continue;
            if (_justCharged[i]) { _justCharged[i] = false; continue; } // 突進済み → スキップ

            // 攻撃範囲内（移動オフセットと同一）で最低HPの敵を探す
            int bestTarget = -1;
            int bestHP     = int.MaxValue;
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(attacker.AnimalType))
            {
                var ap = attacker.Pos + new Vector2Int(raw.x, raw.y * fSign);
                if ((uint)ap.x >= (uint)_cols || (uint)ap.y >= (uint)_rows) continue;
                int ti = FindUnitAt(ap, enemy);
                if (ti >= 0 && !_units[ti].IsDead && _units[ti].CurrentHP < bestHP)
                {
                    bestTarget = ti;
                    bestHP     = _units[ti].CurrentHP;
                }
            }

            // 攻撃専用オフセット（チンパンジー中距離攻撃）
            foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(attacker.AnimalType))
            {
                var ap = attacker.Pos + new Vector2Int(raw.x, raw.y * fSign);
                if ((uint)ap.x >= (uint)_cols || (uint)ap.y >= (uint)_rows) continue;
                int ti = FindUnitAt(ap, enemy);
                if (ti >= 0 && !_units[ti].IsDead && _units[ti].CurrentHP < bestHP)
                {
                    bestTarget = ti;
                    bestHP     = _units[ti].CurrentHP;
                }
            }

            if (bestTarget < 0) continue;

            ref var target = ref _units[bestTarget];
            int dmg = _justCharged[i]
                ? AnimalDefinitions.GetChargeDamage(attacker.AnimalType)
                : attacker.AttackPower;
            target.CurrentHP -= dmg;
            if (target.CurrentHP <= 0) { target.IsDead = true; target.CurrentHP = 0; }

            if (attacker.AnimalType == AnimalType.Snake && !target.IsDead)
                target.PoisonedTurns = Math.Max(target.PoisonedTurns, AnimalDefinitions.SnakePoisonDuration);
        }
    }

    void TickCampHealPhase()
    {
        foreach (var cp in _camps)
        {
            for (int i = 0; i < _unitCount; i++)
            {
                ref var u = ref _units[i];
                if (!u.IsDead && u.Pos == cp)
                {
                    u.CurrentHP = Math.Min(u.MaxHP, u.CurrentHP + 20);
                    break;
                }
            }
        }
    }

    void TickPoisonPhase()
    {
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.PoisonedTurns <= 0) continue;
            u.CurrentHP   -= AnimalDefinitions.SnakePoisonDamage;
            u.PoisonedTurns--;
            if (u.CurrentHP <= 0) { u.CurrentHP = 0; u.IsDead = true; }
        }
    }

    // ---- 勝敗判定 ----

    int CheckWinner()
    {
        bool f1 = false, f2 = false;
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.AnimalType != AnimalType.Fox) continue;
            if (u.Owner == 1) f1 = true; else f2 = true;
        }
        if (!f1 && !f2) return 0;
        if (!f1) return 2;
        if (!f2) return 1;
        return -1;
    }

    int ResolveByHP()
    {
        int h1 = 0, h2 = 0;
        for (int i = 0; i < _unitCount; i++)
        {
            if (_units[i].IsDead) continue;
            if (_units[i].Owner == 1) h1 += _units[i].CurrentHP;
            else                       h2 += _units[i].CurrentHP;
        }
        if (h1 > h2) return 1;
        if (h2 > h1) return 2;
        return 0;
    }

    bool CanAttackFromDest(ref SimUnit unit, Vector2Int dest, Vector2Int targetPos, int owner)
    {
        int fSign = owner == 1 ? 1 : -1;
        foreach (var raw in AnimalDefinitions.GetMoveOffsets(unit.AnimalType))
            if (dest + new Vector2Int(raw.x, raw.y * fSign) == targetPos) return true;
        return false;
    }

    // ---- AICommander.ScoreMove 移植（depth-1 greedy） ----

    float ScoreMove(ref SimUnit unit, Vector2Int dest, int owner, int enemy,
                    bool[,] seen, AIWeightSet w)
    {
        float score = 0f;

        // AICommander.ScoreMove と同じ戦闘/探索モード分岐
        var combatCtx = new SimCombatCtx(this, unit.Pos, owner, seen, w);
        if (combatCtx.HasVisibleEnemies)
        {
            // 戦闘モード: AICommander.ScoreCombat と同じ（CombatScorer のみ）
            score += CombatScorer.Score(
                unit.AnimalType, unit.AttackPower, unit.CurrentHP, unit.MaxHP,
                unit.Pos, dest, combatCtx);
        }
        else
        {
            // 探索モード: AICommander.ScoreExplore と同じ
            // 灯台制圧（直接乗る）
            if (seen[dest.x, dest.y])
            {
                var t = _tiles[dest.x, dest.y];
                if (t.Outpost == OutpostType.Lighthouse && _outpost[dest.x, dest.y] != owner)
                    score += w.LighthouseCapture;
            }
            // 灯台グラジエント（最も近い1灯台のみ・max）
            float bestLhGrad = 0f;
            foreach (var lh in _lighthouses)
            {
                if (!seen[lh.x, lh.y] || _outpost[lh.x, lh.y] == owner) continue;
                bestLhGrad = Math.Max(bestLhGrad, w.ExploreLhGradient / (Manhattan(dest, lh) + 1f));
            }
            score += bestLhGrad;
            // 隊列ボーナス
            score += combatCtx.CountAlliesNear(dest, 3, owner) * w.ExploreFormationBonus;
            // 探索ボーナス
            score += CountNewVisible(unit.AnimalType, dest, seen) * w.ExploreNewTileWeight;
        }

        return score;
    }


    // ---- 合法手生成 ----

    void GetValidMoves(ref SimUnit unit, int owner, List<Vector2Int> result)
    {
        result.Clear();
        var blocked = AnimalDefinitions.GetBlockedTerrain(unit.AnimalType);
        var offsets = AnimalDefinitions.GetMoveOffsets(unit.AnimalType);
        int fSign   = owner == 1 ? 1 : -1;

        foreach (var raw in offsets)
        {
            var off  = new Vector2Int(raw.x, raw.y * fSign);
            var dest = unit.Pos + off;
            if ((uint)dest.x >= (uint)_cols || (uint)dest.y >= (uint)_rows) continue;

            var tile = _tiles[dest.x, dest.y];
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) continue;

            if (Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2)
            {
                var mid = unit.Pos + new Vector2Int(off.x / 2, off.y / 2);
                var mt  = _tiles[mid.x, mid.y];
                if (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type)) continue;
            }

            if (FindUnitAt(dest, owner) >= 0) continue; // 味方ブロック

            int eIdx = FindUnitAt(dest, 3 - owner);
            if (eIdx >= 0)
            {
                // 突進: 2マス移動先に敵 → 押し出し可能なら合法手
                bool is2 = Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2;
                if (!AnimalDefinitions.HasCharge(unit.AnimalType) || !is2) continue;
                var pushDir  = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                               off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                if (!CanPushSim(eIdx, dest + pushDir)) continue;
            }

            result.Add(dest);
        }
        result.Add(unit.Pos); // stay last — ties go to first real move
    }

    // ---- Depth-2: 敵の最善応手を脅威スコアとして返す ----

    bool ShouldUseDepth2(int unitIdx, int owner, int enemy, bool[,] seen)
    {
        ref var unit = ref _units[unitIdx];
        for (int i = 0; i < _unitCount; i++)
        {
            ref var e = ref _units[i];
            if (e.IsDead || e.Owner != enemy || !seen[e.Pos.x, e.Pos.y]) continue;
            if (Manhattan(unit.Pos, e.Pos) <= 3) return true;
        }
        return false;
    }

    // 仮想盤面での味方ユニット検索（_simIdx の位置を _simPos として扱う）
    int FindUnitAtSim(Vector2Int pos, int owner)
    {
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.Owner != owner) continue;
            var uPos = (i == _simIdx) ? _simPos : u.Pos;
            if (uPos == pos) return i;
        }
        return -1;
    }

    // 味方ユニットが dest を攻撃できる数（_simPos を反映）
    int CountOurThreatsTo(Vector2Int dest, int owner)
    {
        int fSign = owner == 1 ? 1 : -1, count = 0;
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (u.IsDead || u.Owner != owner) continue;
            var uPos = (i == _simIdx) ? _simPos : u.Pos;
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                if (uPos + new Vector2Int(raw.x, raw.y * fSign) == dest) { count++; break; }
        }
        return count;
    }

    // 敵側の合法手生成（味方の仮想位置を反映）
    void GetValidMovesForEnemy(ref SimUnit unit, int enemyOwner, List<Vector2Int> result)
    {
        result.Clear();
        var blocked  = AnimalDefinitions.GetBlockedTerrain(unit.AnimalType);
        var offsets  = AnimalDefinitions.GetMoveOffsets(unit.AnimalType);
        int fSign    = enemyOwner == 1 ? 1 : -1;
        int ourOwner = 3 - enemyOwner;

        foreach (var raw in offsets)
        {
            var off  = new Vector2Int(raw.x, raw.y * fSign);
            var dest = unit.Pos + off;
            if ((uint)dest.x >= (uint)_cols || (uint)dest.y >= (uint)_rows) continue;

            var tile = _tiles[dest.x, dest.y];
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) continue;

            if (Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2)
            {
                var mid = unit.Pos + new Vector2Int(off.x / 2, off.y / 2);
                var mt  = _tiles[mid.x, mid.y];
                if (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type)) continue;
            }

            if (FindUnitAt(dest, enemyOwner) >= 0) continue; // 敵自身の味方ブロック

            int ourIdx = FindUnitAtSim(dest, ourOwner);
            if (ourIdx >= 0)
            {
                bool is2 = Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2;
                if (!AnimalDefinitions.HasCharge(unit.AnimalType) || !is2) continue;
                var pushDir = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                              off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                if (!CanPushSim(ourIdx, dest + pushDir)) continue;
            }

            result.Add(dest);
        }
        result.Add(unit.Pos);
    }

    float EvalEnemyBestResponse(int movedIdx, Vector2Int movedTo,
                                int owner, int enemy, AIWeightSet w)
    {
        _simIdx = movedIdx;
        _simPos = movedTo;

        int   eFSign   = enemy == 1 ? 1 : -1;
        float best     = 0f;
        int   enFoxIdx = FindFox(enemy);

        for (int ei = 0; ei < _unitCount; ei++)
        {
            ref var eu = ref _units[ei];
            if (eu.IsDead || eu.Owner != enemy) continue;
            var ew = w.Get(eu.AnimalType);

            GetValidMovesForEnemy(ref eu, enemy, _enemyMoveBuf);

            foreach (var ed in _enemyMoveBuf)
            {
                float threat = 0f;

                // --- 通常攻撃: ed から ed + eFSign*offset ---
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(eu.AnimalType))
                {
                    var apos = ed + new Vector2Int(raw.x, raw.y * eFSign);
                    if ((uint)apos.x >= (uint)_cols || (uint)apos.y >= (uint)_rows) continue;
                    int tgtIdx = FindUnitAtSim(apos, owner);
                    if (tgtIdx < 0) continue;
                    ref var tgt = ref _units[tgtIdx];
                    if (tgt.IsDead) continue;

                    bool canWin = eu.AttackPower >= tgt.AttackPower
                               && eu.CurrentHP   >= tgt.CurrentHP * 0.7f;
                    float atk = tgt.AnimalType == AnimalType.Fox
                        ? w.SafeAttack * ew.FoxMult
                        : canWin ? w.SafeAttack : -w.SafeAttack * 0.25f;
                    int eAllies    = CountAlliesNear(ed,   enemy, 2);
                    int oNear      = CountAlliesNear(apos, owner, 2);
                    int ePower    = eAllies * eAllies;
                    int oPower    = oNear * oNear;
                    int powerDiff = ePower - oPower;
                    if (powerDiff > 0)       atk += powerDiff * w.NumericalPowerMult;
                    else if (powerDiff < 0)  atk -= (-powerDiff) * w.NumericalPowerMult * (10f / 3f);
                    threat += atk;
                }

                // --- 攻撃専用オフセット (チンパンジー) ---
                foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(eu.AnimalType))
                {
                    var apos = ed + new Vector2Int(raw.x, raw.y * eFSign);
                    if ((uint)apos.x >= (uint)_cols || (uint)apos.y >= (uint)_rows) continue;
                    int tgtIdx = FindUnitAtSim(apos, owner);
                    if (tgtIdx < 0) continue;
                    ref var tgt = ref _units[tgtIdx];
                    if (tgt.IsDead) continue;
                    float atk = tgt.AnimalType == AnimalType.Fox
                        ? w.SafeAttack * ew.FoxMult
                        : w.SafeAttack;
                    threat += atk;
                }

                // --- 突進着地: 敵が2マス移動して味方ユニットを押し出す ---
                {
                    var off = ed - eu.Pos;
                    if ((Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2)
                        && AnimalDefinitions.HasCharge(eu.AnimalType))
                    {
                        int tgtIdx = FindUnitAtSim(ed, owner);
                        if (tgtIdx >= 0)
                        {
                            ref var tgt = ref _units[tgtIdx];
                            if (!tgt.IsDead)
                            {
                                int  chgDmg = AnimalDefinitions.GetChargeDamage(eu.AnimalType);
                                bool canWin = chgDmg >= tgt.AttackPower
                                           && eu.CurrentHP >= tgt.CurrentHP * 0.7f;
                                float atk = tgt.AnimalType == AnimalType.Fox
                                    ? w.SafeAttack * ew.FoxMult
                                    : canWin ? w.SafeAttack : -w.SafeAttack * 0.25f;
                                threat += atk;
                            }
                        }
                    }
                }

                // --- 灯台占領 ---
                if (_tiles[ed.x, ed.y].Outpost == OutpostType.Lighthouse
                    && _outpost[ed.x, ed.y] != enemy)
                    threat += w.LighthouseCapture;

                // --- DefenderFoxDist: 敵が自分のキツネを守る ---
                if (enFoxIdx >= 0 && eu.AnimalType != AnimalType.Fox)
                {
                    ref var enFox = ref _units[enFoxIdx];
                    int d = Manhattan(ed, enFox.Pos);
                    float guardBaseE = w.DefenderFoxDist * ew.FoxGuardMult;
                    threat += d <= 3 ? guardBaseE
                            : d <= 5 ? guardBaseE * (5 - d) / 2f
                            :         -guardBaseE * 0.7f;
                }

                if (threat > best) best = threat;
            }
        }

        _simIdx = -1;
        return best;
    }

    // ---- ヘルパー ----

    int FindUnitAt(Vector2Int pos, int owner)
    {
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (!u.IsDead && u.Owner == owner && u.Pos == pos) return i;
        }
        return -1;
    }

    int FindFox(int owner)
    {
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (!u.IsDead && u.Owner == owner && u.AnimalType == AnimalType.Fox) return i;
        }
        return -1;
    }

    int CountAlliesAttackingPos(Vector2Int targetPos, int owner, int excludeIdx)
    {
        int fSign = owner == 1 ? 1 : -1, count = 0;
        for (int i = 0; i < _unitCount; i++)
        {
            if (i == excludeIdx) continue;
            ref var u = ref _units[i];
            if (u.IsDead || u.Owner != owner) continue;
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                if (u.Pos + new Vector2Int(raw.x, raw.y * fSign) == targetPos) { count++; break; }
        }
        return count;
    }

    int CountAlliesNear(Vector2Int center, int owner, int radius)
    {
        int count = 0;
        for (int i = 0; i < _unitCount; i++)
        {
            ref var u = ref _units[i];
            if (!u.IsDead && u.Owner == owner && u.Pos != center
                && Manhattan(u.Pos, center) <= radius) count++;
        }
        return count;
    }

    int CountNewVisible(AnimalType type, Vector2Int from, bool[,] seen)
    {
        bool inForest = _tiles[from.x, from.y].Type == TerrainType.Forest;
        int  range    = inForest
            ? AnimalDefinitions.GetForestViewRange(type)
            : AnimalDefinitions.GetViewRange(type);
        int count = 0;
        for (int dx = -range; dx <= range; dx++)
        for (int dz = -range; dz <= range; dz++)
        {
            if (Math.Abs(dx) + Math.Abs(dz) > range) continue;
            int nx = from.x + dx, nz = from.y + dz;
            if ((uint)nx < (uint)_cols && (uint)nz < (uint)_rows && !seen[nx, nz]) count++;
        }
        return count;
    }

    static int Manhattan(Vector2Int a, Vector2Int b)
        => Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y);

    // ---- ICombatContext implementation for SimBoard ----

    readonly struct SimCombatCtx : ICombatContext
    {
        readonly SimBoard    _b;
        readonly Vector2Int  _curPos;  // current position of the unit being scored
        readonly bool[,]     _seen;
        readonly int         _owner;
        readonly int         _enemy;
        readonly int         _fSign;   // owner's forward direction
        readonly int         _eFSign;  // enemy's forward direction
        readonly AIWeightSet _wSet;

        public SimCombatCtx(SimBoard b, Vector2Int curPos, int owner, bool[,] seen, AIWeightSet wSet)
        {
            _b      = b;
            _curPos = curPos;
            _seen   = seen;
            _owner  = owner;
            _enemy  = 3 - owner;
            _fSign  = owner == 1 ? 1 : -1;
            _eFSign = _enemy == 1 ? 1 : -1;
            _wSet   = wSet;
        }

        public int         Owner             => _owner;
        public int         Enemy             => _enemy;
        public AIWeightSet WSet              => _wSet;

        public bool HasVisibleEnemies
        {
            get
            {
                for (int i = 0; i < _b._unitCount; i++)
                {
                    ref var u = ref _b._units[i];
                    if (!u.IsDead && u.Owner == _enemy && _seen[u.Pos.x, u.Pos.y]) return true;
                }
                return false;
            }
        }

        public void FillAttackable(Vector2Int from, AnimalType unitType, List<CombatTarget> buf)
        {
            foreach (var raw in AnimalDefinitions.GetMoveOffsets(unitType))
            {
                var ap = from + new Vector2Int(raw.x, raw.y * _fSign);
                if ((uint)ap.x >= (uint)_b._cols || (uint)ap.y >= (uint)_b._rows) continue;
                int eIdx = _b.FindUnitAt(ap, _enemy);
                if (eIdx < 0) continue;
                ref var e = ref _b._units[eIdx];
                if (e.IsDead) continue;
                buf.Add(new CombatTarget
                {
                    Type = e.AnimalType, Owner = _enemy,
                    AttackPower = e.AttackPower, CurrentHP = e.CurrentHP, MaxHP = e.MaxHP,
                    Pos = ap, IsRanged = Math.Abs(raw.x) == 2 || Math.Abs(raw.y) == 2,
                });
            }
            foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(unitType))
            {
                var ap = from + new Vector2Int(raw.x, raw.y * _fSign);
                if ((uint)ap.x >= (uint)_b._cols || (uint)ap.y >= (uint)_b._rows) continue;
                int eIdx = _b.FindUnitAt(ap, _enemy);
                if (eIdx < 0) continue;
                ref var e = ref _b._units[eIdx];
                if (e.IsDead) continue;
                buf.Add(new CombatTarget
                {
                    Type = e.AnimalType, Owner = _enemy,
                    AttackPower = e.AttackPower, CurrentHP = e.CurrentHP, MaxHP = e.MaxHP,
                    Pos = ap, IsRanged = true, IsAttackOnly = true,
                });
            }
        }

        public bool IsVisible(Vector2Int pos) => _seen[pos.x, pos.y];

        public float CountAlliesCanAttack(Vector2Int targetPos, Vector2Int selfDest, out int combinedDamage)
        {
            float power = 0f;
            combinedDamage = 0;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != _owner) continue;
                var uPos = (u.Pos == _curPos) ? selfDest : u.Pos;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                {
                    if (uPos + new Vector2Int(raw.x, raw.y * _fSign) == targetPos)
                    {
                        power += (float)u.CurrentHP / u.MaxHP;
                        combinedDamage += u.AttackPower;
                        break;
                    }
                }
            }
            return power;
        }

        public float CountEnemiesCanAttack(Vector2Int targetPos)
        {
            float power = 0f;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != _enemy || !_seen[u.Pos.x, u.Pos.y]) continue;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                {
                    if (u.Pos + new Vector2Int(raw.x, raw.y * _eFSign) == targetPos)
                    {
                        power += (float)u.CurrentHP / u.MaxHP;
                        break;
                    }
                }
            }
            return power;
        }

        public bool TryGetEnemyAt(Vector2Int pos, out CombatTarget target)
        {
            int eIdx = _b.FindUnitAt(pos, _enemy);
            if (eIdx < 0 || _b._units[eIdx].IsDead) { target = default; return false; }
            ref var e = ref _b._units[eIdx];
            target = new CombatTarget
            {
                Type = e.AnimalType, Owner = _enemy,
                AttackPower = e.AttackPower, CurrentHP = e.CurrentHP, MaxHP = e.MaxHP,
                Pos = pos,
            };
            return true;
        }

        public bool CanPush(AnimalType targetType, Vector2Int pushDest)
        {
            if (targetType == AnimalType.Elephant) return false;
            if ((uint)pushDest.x >= (uint)_b._cols || (uint)pushDest.y >= (uint)_b._rows) return false;
            var tile    = _b._tiles[pushDest.x, pushDest.y];
            var blocked = AnimalDefinitions.GetBlockedTerrain(targetType);
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) return false;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (!u.IsDead && u.Pos == pushDest) return false;
            }
            return true;
        }

        public bool TryGetFoxPos(int owner, out Vector2Int foxPos)
        {
            int foxIdx = _b.FindFox(owner);
            if (foxIdx < 0) { foxPos = default; return false; }
            foxPos = _b._units[foxIdx].Pos;
            return true;
        }

        public int CountAlliesNear(Vector2Int center, int radius, int owner)
            => _b.CountAlliesNear(center, owner, radius);

        public TerrainType GetTerrain(Vector2Int pos) => _b._tiles[pos.x, pos.y].Type;

        public IReadOnlyList<Vector2Int> GetKnownCamps() => _b._camps;

        public int GetTeamHP(int owner)
        {
            int hp = 0;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (!u.IsDead && u.Owner == owner) hp += u.CurrentHP;
            }
            return hp;
        }

        public float GetTeamZCenter(int owner)
        {
            float sum = 0f; int count = 0;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != owner) continue;
                sum += u.Pos.y;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        public int NearestEnemyDistFrom(Vector2Int from)
        {
            int nearest = int.MaxValue;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != _enemy || !_seen[u.Pos.x, u.Pos.y]) continue;
                int d = Math.Abs(from.x - u.Pos.x) + Math.Abs(from.y - u.Pos.y);
                if (d < nearest) nearest = d;
            }
            return nearest;
        }

        public void FillEnemiesThatCanReach(Vector2Int dest, List<CombatTarget> buf)
        {
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != _enemy || !_seen[u.Pos.x, u.Pos.y]) continue;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(u.AnimalType))
                {
                    if (u.Pos + new Vector2Int(raw.x, raw.y * _eFSign) == dest)
                    {
                        buf.Add(new CombatTarget
                        {
                            Type = u.AnimalType, Owner = _enemy,
                            AttackPower = u.AttackPower, CurrentHP = u.CurrentHP, MaxHP = u.MaxHP,
                            Pos = u.Pos,
                        });
                        break;
                    }
                }
            }
        }

        public int CountAlliesReachableBy(AnimalType attackerType, Vector2Int attackerPos, Vector2Int selfDest)
        {
            int count = 0;
            for (int i = 0; i < _b._unitCount; i++)
            {
                ref var u = ref _b._units[i];
                if (u.IsDead || u.Owner != _owner) continue;
                var allyPos = (u.Pos == _curPos) ? selfDest : u.Pos;
                foreach (var raw in AnimalDefinitions.GetMoveOffsets(attackerType))
                {
                    if (attackerPos + new Vector2Int(raw.x, raw.y * _eFSign) == allyPos)
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        public float GetMoveTime(AnimalType type) => AnimalDefinitions.GetMoveTime(type);
    }

    // ---- ロールアウト探索 ----

    struct BoardSnapshot
    {
        public SimUnit[] Units;
        public int       UnitCount;
        public int[,]    Outpost;
        public bool[,]   Seen1;
        public bool[,]   Seen2;
        public bool[]    JustCharged;
    }

    BoardSnapshot TakeSnapshot()
    {
        var s = new BoardSnapshot
        {
            Units       = new SimUnit[_unitCount],
            UnitCount   = _unitCount,
            Outpost     = (int[,])_outpost.Clone(),
            Seen1       = (bool[,])_seen1.Clone(),
            Seen2       = (bool[,])_seen2.Clone(),
            JustCharged = new bool[MAX_UNITS],
        };
        Array.Copy(_units,       s.Units,       _unitCount);
        Array.Copy(_justCharged, s.JustCharged, MAX_UNITS);
        return s;
    }

    void RestoreSnapshot(in BoardSnapshot s)
    {
        _unitCount = s.UnitCount;
        Array.Copy(s.Units,       _units,       s.UnitCount);
        Array.Copy(s.JustCharged, _justCharged, MAX_UNITS);
        Array.Copy(s.Outpost,     _outpost,     _outpost.Length);
        Array.Copy(s.Seen1,       _seen1,       _seen1.Length);
        Array.Copy(s.Seen2,       _seen2,       _seen2.Length);
    }

    int FindUnitById(int id)
    {
        for (int i = 0; i < _unitCount; i++)
            if (_units[i].Id == id && !_units[i].IsDead) return i;
        return -1;
    }

}