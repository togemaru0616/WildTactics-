using System;
using System.Collections.Generic;
using UnityEngine;

// ── Shared data types ────────────────────────────────────────────────────────

public struct CombatTarget
{
    public AnimalType Type;
    public int Owner, AttackPower, CurrentHP, MaxHP;
    public Vector2Int Pos;
    public bool IsRanged;     // 2-tile range attack
    public bool IsAttackOnly; // attack-only offset (Chimpanzee mid-range)
}

// ── Context interface ────────────────────────────────────────────────────────
// Implemented by AICommanderCombatContext (Unity) and SimBoardCombatContext (simulation).

public interface ICombatContext
{
    int         Owner            { get; }
    int         Enemy            { get; }
    AIWeightSet WSet             { get; }
    bool        HasVisibleEnemies { get; }

    // Enemies reachable/attackable FROM 'from' by 'unitType' (move-offsets + attack-only offsets).
    void FillAttackable(Vector2Int from, AnimalType unitType, List<CombatTarget> buf);

    // Is the tile at 'pos' currently visible?
    bool IsVisible(Vector2Int pos);

    // Lanchester allies: HP-ratio-weighted power of allies that can attack targetPos (self at selfDest).
    // Also outputs combined damage for kill-progress.
    float CountAlliesCanAttack(Vector2Int targetPos, Vector2Int selfDest, out int combinedDamage);

    // Lanchester enemies: HP-ratio-weighted power of visible enemies that can attack targetPos.
    float CountEnemiesCanAttack(Vector2Int targetPos);

    // Charge: is there an enemy unit exactly at pos?
    bool TryGetEnemyAt(Vector2Int pos, out CombatTarget target);

    // Charge: can the enemy at pos be pushed to pushDest?
    bool CanPush(AnimalType targetType, Vector2Int pushDest);

    // Fox position of 'owner' side (returns false if Fox is dead/absent).
    bool TryGetFoxPos(int owner, out Vector2Int foxPos);

    // Count our allies (excluding self) within Chebyshev radius of center.
    int CountAlliesNear(Vector2Int center, int radius, int owner);

    // Terrain type at a tile.
    TerrainType GetTerrain(Vector2Int pos);

    // Known camp tile positions (may return a cached list).
    IReadOnlyList<Vector2Int> GetKnownCamps();

    // Total alive HP for a team.
    int GetTeamHP(int owner);

    // Average Z position of alive units on a team.
    float GetTeamZCenter(int owner);

    // Distance to nearest visible enemy from 'from' (int.MaxValue if none visible).
    int NearestEnemyDistFrom(Vector2Int from);

    // 被攻撃ペナルティ: enemy units that can move to dest (via move-offsets only).
    void FillEnemiesThatCanReach(Vector2Int dest, List<CombatTarget> buf);

    // How many of our allies (self counted at selfDest) are reachable by this attacker.
    int CountAlliesReachableBy(AnimalType attackerType, Vector2Int attackerPos, Vector2Int selfDest);

    // Move time for opportunity-cost calculation.
    float GetMoveTime(AnimalType type);
}

// ── Scorer ───────────────────────────────────────────────────────────────────

public static class CombatScorer
{
    // Thread-local reusable buffers (SimBoard is single-threaded per instance; Unity main thread).
    [ThreadStatic] static List<CombatTarget> _atkBuf;
    [ThreadStatic] static List<CombatTarget> _curBuf;
    [ThreadStatic] static List<CombatTarget> _penBuf;

    static List<CombatTarget> AtkBuf => _atkBuf ??= new(16);
    static List<CombatTarget> CurBuf => _curBuf ??= new(16);
    static List<CombatTarget> PenBuf => _penBuf ??= new(16);

    // Combat score for moving 'unit' to 'dest'.
    // This is the single source of truth shared by AICommander.ScoreCombat and SimBoard.ScoreMove.
    public static float Score(
        AnimalType unitType,
        int        attackPower,
        int        currentHP,
        int        maxHP,
        Vector2Int unitPos,
        Vector2Int dest,
        ICombatContext ctx)
    {
        float score   = 0f;
        var   wSet    = ctx.WSet;
        var   w       = wSet.Get(unitType);
        float hpRatio = (float)currentHP / maxHP;
        int   owner   = ctx.Owner;
        int   enemy   = ctx.Enemy;

        // ── 1. 攻撃スコア ────────────────────────────────────────────────
        AtkBuf.Clear();
        ctx.FillAttackable(dest, unitType, AtkBuf);
        foreach (var tgt in AtkBuf)
        {
            if (!ctx.IsVisible(tgt.Pos)) continue;

            float allyPower  = ctx.CountAlliesCanAttack(tgt.Pos, dest, out int combinedDmg);
            bool canWin = combinedDmg >= tgt.AttackPower && currentHP >= tgt.CurrentHP * 0.7f;
            float atk = tgt.Type == AnimalType.Fox
                ? wSet.SafeAttack * w.FoxMult
                : canWin ? wSet.SafeAttack : -wSet.SafeAttack * 0.25f;
            float enemyPower = ctx.CountEnemiesCanAttack(tgt.Pos);
            float lancAdv    = allyPower * allyPower - enemyPower * enemyPower - 1f;
            if (lancAdv > 0) atk += lancAdv * wSet.NumericalPowerMult;
            else             atk += lancAdv * wSet.NumericalPowerMult * (10f / 3f);

            float killProg = Mathf.Clamp01((float)combinedDmg / tgt.CurrentHP);
            atk += killProg * killProg * 50f;
            score += atk;

            if (tgt.IsRanged)
                score += wSet.RangeAttackBonus;
        }

        // ── 2. 突進 ──────────────────────────────────────────────────────
        if (AnimalDefinitions.HasCharge(unitType))
        {
            var off = dest - unitPos;
            bool is2 = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2;
            if (is2 && ctx.TryGetEnemyAt(dest, out var charge) && ctx.IsVisible(charge.Pos))
            {
                var pushDir = new Vector2Int(
                    off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                    off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                if (ctx.CanPush(charge.Type, dest + pushDir))
                {
                    int  chgDmg = AnimalDefinitions.GetChargeDamage(unitType);
                    bool canWin = chgDmg >= charge.AttackPower && currentHP >= charge.CurrentHP * 0.7f;
                    score += charge.Type == AnimalType.Fox
                        ? wSet.SafeAttack * w.FoxMult
                        : canWin ? wSet.SafeAttack : -wSet.SafeAttack * 0.25f;
                }
            }
        }

        // ── 3. Fox 護衛 ───────────────────────────────────────────────────
        if (ctx.TryGetFoxPos(owner, out var foxPos))
        {
            if (unitType != AnimalType.Fox)
            {
                int d = Manhattan(dest, foxPos);
                float guardBase = wSet.DefenderFoxDist * w.FoxGuardMult;
                score += d <= 3 ? guardBase
                       : d <= 5 ? guardBase * (5 - d) / 2f
                       :         0f;

                // Fox 周囲の脅威レベルに応じて近接ボーナスを追加
                // 敵が多いほど・距離が近いほど高くなるグラジェント
                float foxThreat = ctx.CountEnemiesCanAttack(foxPos);
                if (foxThreat > 0f)
                    score += guardBase * foxThreat / (d + 1f);
            }
            else
            {
                score += ctx.CountAlliesNear(dest, 3, owner)
                       * (wSet.DefenderFoxDist * w.FoxGuardMult * 0.3f);
            }
        }

        // ── 4. 地形ボーナス ───────────────────────────────────────────────
        if (AnimalDefinitions.PrefersTerrain(unitType, ctx.GetTerrain(dest)))
            score += wSet.TerrainBonus;

        // ── 5. キャンプ回復 ───────────────────────────────────────────────
        {
            float campUrg = 1f - hpRatio;
            foreach (var cp in ctx.GetKnownCamps())
                score += w.CampMult * campUrg * (1f / (Manhattan(dest, cp) + 1f)) * wSet.CampBonus;
        }

        // ── 6. 圧力・隊列 ─────────────────────────────────────────────────
        if (ctx.HasVisibleEnemies)
        {
            int allyHP  = ctx.GetTeamHP(owner);
            int enemyHP = ctx.GetTeamHP(enemy);
            float hpAdv = (float)(allyHP - enemyHP) / (allyHP + enemyHP + 1);

            float refZ = ctx.TryGetFoxPos(owner, out var allyFoxPos)
                ? allyFoxPos.y
                : ctx.GetTeamZCenter(owner);
            int   fSign       = owner == 1 ? 1 : -1;
            float actualFwd   = (dest.y - refZ) * fSign;
            float effectiveBias = w.FormationBias + hpAdv * wSet.PressureShift * 0.01f;
            float deviation = actualFwd - effectiveBias;
            float fwdDamp = deviation > 0 ? Mathf.Clamp01(1f - hpAdv) : 1f;
            score -= deviation * deviation * wSet.FormationSpreadPen * 0.1f * fwdDamp;
        }

        // ── 7. 機会損失コスト ─────────────────────────────────────────────
        {
            CurBuf.Clear();
            ctx.FillAttackable(unitPos, unitType, CurBuf);
            float bestAtkVal = 0f;
            foreach (var tgt in CurBuf)
            {
                if (!ctx.IsVisible(tgt.Pos)) continue;
                ctx.CountAlliesCanAttack(tgt.Pos, unitPos, out int oppCombDmg);
                bool canWin = oppCombDmg >= tgt.AttackPower && currentHP >= tgt.CurrentHP * 0.7f;
                float av = tgt.Type == AnimalType.Fox
                    ? wSet.SafeAttack * w.FoxMult
                    : canWin ? wSet.SafeAttack : 0f;
                if (av > bestAtkVal) bestAtkVal = av;
            }
            if (bestAtkVal > 0f)
            {
                float opp = bestAtkVal * ctx.GetMoveTime(unitType) * 0.67f * 0.5f;
                score += dest == unitPos ? opp : -opp;
            }
        }

        // ── 8. 被攻撃ペナルティ ───────────────────────────────────────────
        {
            PenBuf.Clear();
            ctx.FillEnemiesThatCanReach(dest, PenBuf);
            foreach (var attacker in PenBuf)
            {
                var  ew          = wSet.Get(attacker.Type);
                bool canEnemyWin = attacker.AttackPower >= attackPower
                                && attacker.CurrentHP   >= currentHP * 0.7f;
                float enemyAtkVal = unitType == AnimalType.Fox
                    ? wSet.SafeAttack * ew.FoxMult
                    : canEnemyWin ? wSet.SafeAttack : 0f;
                if (enemyAtkVal <= 0f) continue;
                int n = ctx.CountAlliesReachableBy(attacker.Type, attacker.Pos, dest);
                score -= enemyAtkVal / Mathf.Max(1, n) * 0.5f * w.FleeThresholdMult;
            }
        }

        return score;
    }

    static int Manhattan(Vector2Int a, Vector2Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
}
