using UnityEngine;
using System;
using System.Collections.Generic;

public enum AnimalRank { B, A, S }

// All animal data is stored in static arrays indexed by (int)AnimalType.
// No Dictionary/HashSet allocations after startup — safe for Burst/Job access patterns.
public static class AnimalDefinitions
{
    // Direction constants used to build offset tables
    static readonly Vector2Int F  = new( 0,  1);
    static readonly Vector2Int B  = new( 0, -1);
    static readonly Vector2Int L  = new(-1,  0);
    static readonly Vector2Int R  = new( 1,  0);
    static readonly Vector2Int FL = new(-1,  1);
    static readonly Vector2Int FR = new( 1,  1);
    static readonly Vector2Int BL = new(-1, -1);
    static readonly Vector2Int BR = new( 1, -1);
    static readonly Vector2Int F2 = new( 0,  2);
    static readonly Vector2Int B2 = new( 0, -2);
    static readonly Vector2Int L2 = new(-2,  0);
    static readonly Vector2Int R2 = new( 2,  0);
    static readonly Vector2Int KL = new(-1,  2);
    static readonly Vector2Int KR = new( 1,  2);

    const int N = 16; // AnimalType count
    const int T =  7; // TerrainType count

    static readonly Vector2Int[][]         _moveOffsets       = new Vector2Int[N][];
    static readonly Vector2Int[][]         _attackOnlyOffsets = new Vector2Int[N][];
    // Bit k = (TerrainType)k is blocked / preferred for that animal
    static readonly uint[]                 _blockedMask       = new uint[N];
    static readonly uint[]                 _preferredMask     = new uint[N];
    // Shims so existing callers of GetBlockedTerrain().Contains() keep working
    static readonly HashSet<TerrainType>[] _blockedSets       = new HashSet<TerrainType>[N];
    // _terrainMult[animalIdx * T + terrainIdx]
    static readonly float[]                _terrainMult       = new float[N * T];
    static readonly int[]                  _viewRange         = new int[N];
    static readonly int[]                  _forestViewRange   = new int[N];
    static readonly float[]                _moveTime          = new float[N];
    static readonly int[]                  _maxHP             = new int[N];
    static readonly int[]                  _attackPower       = new int[N];
    static readonly int[]                  _chargeDamage      = new int[N]; // 0 = no charge
    static readonly AnimalRank[]           _rank              = new AnimalRank[N];

    static AnimalDefinitions()
    {
        // Local index aliases — match AnimalType enum order
        int fox=0, tiger=1, bear=2, gorilla=3, lion=4, chimpanzee=5, rhino=6, horse=7,
            snake=8, boar=9, hippo=10, giraffe=11, elephant=12, panda=13, wolf=14, reindeer=15;
        // TerrainType enum order
        int flat=0, rocky=1, forest=2, river=3, /*bridge=4,*/ pond=5, sand=6;

        // ---- Move offsets ----
        _moveOffsets[fox]        = new[]{ F, B, L, R, FL, FR, BL, BR };
        _moveOffsets[tiger]      = new[]{ FL, FR, BL, BR, KL, KR };
        _moveOffsets[bear]       = new[]{ F, B, L, R, FL, FR };
        _moveOffsets[gorilla]    = new[]{ F, FL, FR, BL, BR };
        _moveOffsets[lion]       = new[]{ F, B, L, R, FL, FR, BL, BR };
        _moveOffsets[chimpanzee] = new[]{ F, B, L, R, FL, FR };
        _moveOffsets[rhino]      = new[]{ F, F2, L, R, FL, FR, B };
        _moveOffsets[horse]      = new[]{ F, F2, L, R, B };
        _moveOffsets[snake]      = new[]{ F, L, R, FL, FR, BL, BR };
        _moveOffsets[boar]       = new[]{ F, B, L, R, F2, B2, L2, R2 };
        _moveOffsets[hippo]      = new[]{ F, B, L, R };
        _moveOffsets[giraffe]    = new[]{ F, B, L, R, FL, FR };
        _moveOffsets[elephant]   = new[]{ F, B, L, R, FL, FR, BL, BR };
        _moveOffsets[panda]      = new[]{ F, B, L, R, FL, FR };
        _moveOffsets[wolf]       = new[]{ F, L, R, BL, BR };
        _moveOffsets[reindeer]   = new[]{ F, FL, FR, BL, BR };

        // ---- Attack-only offsets (Chimpanzee only) ----
        var empty = Array.Empty<Vector2Int>();
        for (int i = 0; i < N; i++) _attackOnlyOffsets[i] = empty;
        _attackOnlyOffsets[chimpanzee] = new[]{ new Vector2Int(-1,2), new Vector2Int(0,2), new Vector2Int(1,2) };

        // ---- Blocked terrain bitmasks ----
        uint bRiver  = 1u << river;
        uint bPond   = 1u << pond;
        uint bRocky  = 1u << rocky;
        uint bForest = 1u << forest;
        _blockedMask[fox]        = 0;
        _blockedMask[tiger]      = bRocky;
        _blockedMask[bear]       = 0;
        _blockedMask[gorilla]    = bRiver | bPond;
        _blockedMask[lion]       = bRiver | bPond | bRocky;
        _blockedMask[chimpanzee] = bRiver | bPond;
        _blockedMask[rhino]      = bRiver | bPond | bRocky;
        _blockedMask[horse]      = bRiver | bPond | bRocky;
        _blockedMask[snake]      = 0;
        _blockedMask[boar]       = bRiver | bPond;
        _blockedMask[hippo]      = bForest | bRocky;
        _blockedMask[giraffe]    = bRiver | bPond | bRocky;
        _blockedMask[elephant]   = bForest;
        _blockedMask[panda]      = bRiver | bPond;
        _blockedMask[wolf]       = bRiver | bPond;
        _blockedMask[reindeer]   = bRiver | bPond;

        // Build HashSet shims for callers that still use .Contains()
        for (int i = 0; i < N; i++)
        {
            _blockedSets[i] = new HashSet<TerrainType>();
            uint m = _blockedMask[i];
            for (int t = 0; t < T; t++)
                if ((m & (1u << t)) != 0)
                    _blockedSets[i].Add((TerrainType)t);
        }

        // ---- Terrain mult table ----
        // Defaults: Flat=1.0, Rocky=1.8, Forest=1.3, River=2.3, Bridge=1.0, Pond=2.3, Sand=1.0
        float[] def = { 1.0f, 1.8f, 1.3f, 2.3f, 1.0f, 2.3f, 1.0f };
        for (int i = 0; i < N; i++)
            for (int t = 0; t < T; t++)
                _terrainMult[i * T + t] = def[t];

        // Per-animal overrides
        void S(int a, int t2, float v) => _terrainMult[a * T + t2] = v;
        S(reindeer,   rocky,  1.0f);
        S(bear,       rocky,  1.0f); S(bear,        forest, 1.0f); S(bear,  river, 1.2f); S(bear,  pond, 1.2f);
        S(wolf,       rocky,  1.2f); S(wolf,        forest, 1.0f);
        S(tiger,      rocky,  1.2f); S(tiger,       forest, 1.0f);
        S(snake,      forest, 1.2f); S(snake,       river,  1.2f); S(snake, pond,  1.2f);
        S(hippo,      river,  1.0f); S(hippo,       pond,   1.0f);
        S(gorilla,    forest, 1.0f);
        S(chimpanzee, forest, 0.8f);
        S(boar,       forest, 1.0f);
        S(fox,        forest, 1.2f);
        S(lion,       forest, 1.2f);

        // ---- View ranges (default = 3) ----
        for (int i = 0; i < N; i++) _viewRange[i] = 3;
        _viewRange[giraffe]    = 7;
        _viewRange[elephant]   = 5;
        _viewRange[chimpanzee] = 4;
        _viewRange[horse]      = 4;
        _viewRange[reindeer]   = 4;
        _viewRange[gorilla]    = 4;
        _viewRange[lion]       = 4;
        _viewRange[tiger]      = 4;
        _viewRange[wolf]       = 4;
        _viewRange[boar]       = 2;
        _viewRange[rhino]      = 2;

        // ---- Forest view ranges (default = 1) ----
        for (int i = 0; i < N; i++) _forestViewRange[i] = 1;
        _forestViewRange[chimpanzee] = 5;
        _forestViewRange[tiger]      = 3;
        _forestViewRange[gorilla]    = 3;
        _forestViewRange[wolf]       = 3;
        _forestViewRange[lion]       = 2;
        _forestViewRange[reindeer]   = 2;
        _forestViewRange[bear]       = 2;
        _forestViewRange[panda]      = 2;
        _forestViewRange[horse]      = 2;

        // ---- Move times ----
        _moveTime[fox]        = 2.0f;
        _moveTime[tiger]      = 1.6f;
        _moveTime[bear]       = 2.0f;
        _moveTime[gorilla]    = 2.6f;
        _moveTime[lion]       = 1.4f;
        _moveTime[chimpanzee] = 2.3f;
        _moveTime[rhino]      = 2.0f;
        _moveTime[horse]      = 1.3f;
        _moveTime[snake]      = 1.6f;
        _moveTime[boar]       = 2.0f;
        _moveTime[hippo]      = 3.3f;
        _moveTime[giraffe]    = 1.6f;
        _moveTime[elephant]   = 2.6f;
        _moveTime[panda]      = 3.3f;
        _moveTime[wolf]       = 1.6f;
        _moveTime[reindeer]   = 1.4f;

        // ---- Max HPs ----
        _maxHP[fox]        =  90;
        _maxHP[tiger]      = 160;
        _maxHP[bear]       = 190;
        _maxHP[gorilla]    = 200;
        _maxHP[lion]       = 140;
        _maxHP[chimpanzee] = 140;
        _maxHP[rhino]      = 175;
        _maxHP[horse]      = 145;
        _maxHP[snake]      = 115;
        _maxHP[boar]       = 130;
        _maxHP[hippo]      = 220;
        _maxHP[giraffe]    = 150;
        _maxHP[elephant]   = 250;
        _maxHP[panda]      = 155;
        _maxHP[wolf]       = 125;
        _maxHP[reindeer]   = 115;

        // ---- Attack powers ----
        _attackPower[fox]        =  15;
        _attackPower[tiger]      =  45;
        _attackPower[bear]       =  44;
        _attackPower[gorilla]    =  48;
        _attackPower[lion]       =  40;
        _attackPower[chimpanzee] =  38;
        _attackPower[rhino]      =  42;
        _attackPower[horse]      =  30;
        _attackPower[snake]      =  36;
        _attackPower[boar]       =  38;
        _attackPower[hippo]      =  45;
        _attackPower[giraffe]    =  40;
        _attackPower[elephant]   =  55;
        _attackPower[panda]      =  35;
        _attackPower[wolf]       =  32;
        _attackPower[reindeer]   =  20;

        // ---- Charge damages (0 = no charge) ----
        _chargeDamage[rhino] = 55;
        _chargeDamage[boar]  = 42;

        // ---- Ranks (default = B = 0) ----
        _rank[fox]        = AnimalRank.A;
        _rank[tiger]      = AnimalRank.S;
        _rank[bear]       = AnimalRank.S;
        _rank[gorilla]    = AnimalRank.S;
        _rank[lion]       = AnimalRank.A;
        _rank[chimpanzee] = AnimalRank.A;
        _rank[rhino]      = AnimalRank.S;
        _rank[horse]      = AnimalRank.A;
        _rank[snake]      = AnimalRank.A;
        _rank[boar]       = AnimalRank.A;
        _rank[hippo]      = AnimalRank.S;
        _rank[giraffe]    = AnimalRank.S;
        _rank[elephant]   = AnimalRank.S;
        _rank[panda]      = AnimalRank.A;
        // wolf=B, reindeer=B — already zero-initialized

        // ---- Preferred terrain bitmasks ----
        uint bFlat = 1u << flat;
        uint bSand = 1u << sand;
        _preferredMask[tiger]      = bForest;
        _preferredMask[wolf]       = bForest;
        _preferredMask[gorilla]    = bForest;
        _preferredMask[chimpanzee] = bForest;
        _preferredMask[boar]       = bForest;
        _preferredMask[bear]       = bForest | bRocky;
        _preferredMask[reindeer]   = bRocky;
        _preferredMask[hippo]      = bRiver | bPond;
        _preferredMask[horse]      = bFlat | bSand;
        _preferredMask[lion]       = bFlat | bSand;
        _preferredMask[giraffe]    = bFlat | bSand;
        _preferredMask[elephant]   = bFlat | bSand;
    }

    // --- Public API (call signatures unchanged) ---

    public static Vector2Int[] GetMoveOffsets(AnimalType type)
        => _moveOffsets[(int)type];

    public static Vector2Int[] GetAttackOnlyOffsets(AnimalType type)
        => _attackOnlyOffsets[(int)type];

    public static int GetViewRange(AnimalType type)
        => _viewRange[(int)type];

    public static int GetForestViewRange(AnimalType type)
        => _forestViewRange[(int)type];

    // Returns the pre-allocated set — no allocation on call.
    public static HashSet<TerrainType> GetBlockedTerrain(AnimalType type)
        => _blockedSets[(int)type];

    // Bitmask version for Burst-compatible code paths.
    public static bool IsBlocked(AnimalType type, TerrainType terrain)
        => (_blockedMask[(int)type] & (1u << (int)terrain)) != 0;

    public static float GetTerrainMult(AnimalType type, TerrainType terrain)
        => _terrainMult[(int)type * T + (int)terrain];

    public static float GetMoveTime(AnimalType type)
        => _moveTime[(int)type];

    public static int GetMaxHP(AnimalType type)
        => _maxHP[(int)type];

    public static int GetAttackPower(AnimalType type)
        => _attackPower[(int)type];

    public static int GetChargeDamage(AnimalType type)
    {
        int d = _chargeDamage[(int)type];
        return d != 0 ? d : _attackPower[(int)type];
    }

    public static bool HasCharge(AnimalType type)
        => _chargeDamage[(int)type] != 0;

    public static AnimalRank GetRank(AnimalType type)
        => _rank[(int)type];

    public static int GetCost(AnimalType type)
        => GetRank(type) switch { AnimalRank.S => 20, AnimalRank.A => 10, _ => 5 };

    public static bool PrefersTerrain(AnimalType type, TerrainType terrain)
        => (_preferredMask[(int)type] & (1u << (int)terrain)) != 0;

    // ---- Snake poison constants ----
    public const int   SnakePoisonDamage   = 15;
    public const int   SnakePoisonDuration = 3;
    public const float SnakePoisonInterval = 3f;
}
