using UnityEngine;
using System.Collections.Generic;

public static class TerrainGenerator
{
    public static int COLS      = 13;
    public static int ROWS      = 26;
    public static int BASE_ROWS = 2;
    public static int HOME_MIN_X = 4;
    public static int HOME_MAX_X = 8;

    public static TileCell[,] Generate(int seed = -1)
    {
        if (seed < 0) seed = Random.Range(0, int.MaxValue);
        Random.InitState(seed);

        var grid = new TileCell[COLS, ROWS];
        for (int x = 0; x < COLS; x++)
            for (int z = 0; z < ROWS; z++)
                grid[x, z] = new TileCell(x, z);

        SetBaseOwners(grid);
        ClassifyTerrain(grid);
        var riverRoute = PlanRiverRoute();
        if (riverRoute != null) ApplyRiver(grid, riverRoute);
        foreach (TerrainType t in System.Enum.GetValues(typeof(TerrainType)))
        {
            if (t == TerrainType.Flat   || t == TerrainType.Sand ||
                t == TerrainType.River  || t == TerrainType.Bridge) continue;
            RemoveSmallRegions(grid, t, 6);
        }
        PlaceSand(grid);
        PlaceLighthouses(grid, ROWS <= 10 ? 1 : 3);
        PlaceCamps(grid, 2);
        FlattenHomeAreas(grid);             // 最後に確定（他の処理で上書きされるのを防ぐ）

        return grid;
    }

    // ---- 地形分類（湿度ノイズ1枚） ----
    // 乾燥 → Rocky、中間 → Flat、湿気 → Forest、多湿 → Pond
    static void ClassifyTerrain(TileCell[,] grid)
    {
        float ox1 = Random.Range(0f, 1000f), oz1 = Random.Range(0f, 1000f);
        float ox2 = Random.Range(0f, 1000f), oz2 = Random.Range(0f, 1000f);

        for (int x = 0; x < COLS; x++)
        {
            for (int z = 0; z < ROWS; z++)
            {
                float m = Mathf.PerlinNoise(x * 0.15f + ox1, z * 0.15f + oz1) * 0.7f
                        + Mathf.PerlinNoise(x * 0.35f + ox2, z * 0.35f + oz2) * 0.3f;

                grid[x, z].Type = m switch
                {
                    < 0.32f => TerrainType.Rocky,
                    < 0.50f => TerrainType.Flat,
                    < 0.69f => TerrainType.Forest,
                    _       => TerrainType.Pond
                };
            }
        }
    }

    static void FlattenHomeAreas(TileCell[,] grid)
    {
        for (int x = HOME_MIN_X; x <= HOME_MAX_X; x++)
        {
            for (int z = 0; z < BASE_ROWS; z++)
                grid[x, z].Type = TerrainType.Flat;
            for (int z = ROWS - BASE_ROWS; z < ROWS; z++)
                grid[x, z].Type = TerrainType.Flat;
        }
    }

    static void SetBaseOwners(TileCell[,] grid)
    {
        for (int x = 0; x < COLS; x++)
        {
            for (int z = 0; z < BASE_ROWS; z++)
                grid[x, z].BaseOwner = 1;
            for (int z = ROWS - BASE_ROWS; z < ROWS; z++)
                grid[x, z].BaseOwner = 2;
        }
    }

    static void RemoveSmallRegions(TileCell[,] grid, TerrainType type, int minSize)
    {
        var visited = new bool[COLS, ROWS];
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int x = 0; x < COLS; x++)
        {
            for (int z = 0; z < ROWS; z++)
            {
                if (grid[x, z].Type != type || visited[x, z]) continue;

                var component = new List<(int, int)>();
                var queue = new Queue<(int, int)>();
                queue.Enqueue((x, z));
                visited[x, z] = true;

                while (queue.Count > 0)
                {
                    var (cx, cz) = queue.Dequeue();
                    component.Add((cx, cz));
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = cx + dx[i], nz = cz + dz[i];
                        if (nx < 0 || nx >= COLS || nz < 0 || nz >= ROWS) continue;
                        if (grid[nx, nz].Type != type || visited[nx, nz]) continue;
                        visited[nx, nz] = true;
                        queue.Enqueue((nx, nz));
                    }
                }

                if (component.Count < minSize)
                    foreach (var (px, pz) in component)
                        grid[px, pz].Type = TerrainType.Flat;
            }
        }
    }

    // ---- 川岸の砂（確率チェーン） ----
    // 1マス目：River/Bridge に隣接する Flat → 確率70% で Sand
    // 2マス目：1マス目の Sand に隣接する Flat → 確率40% で Sand（1マス目が Sand でないなら生成しない）
    static void PlaceSand(TileCell[,] grid)
    {
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };
        var firstLayer = new List<(int x, int z)>();

        for (int x = 0; x < COLS; x++)
        {
            for (int z = BASE_ROWS; z < ROWS - BASE_ROWS; z++)
            {
                if (grid[x, z].Type != TerrainType.Flat) continue;

                bool nextToWater = false;
                for (int i = 0; i < 4; i++)
                {
                    int nx = x + dx[i], nz = z + dz[i];
                    if (nx < 0 || nx >= COLS || nz < 0 || nz >= ROWS) continue;
                    var t = grid[nx, nz].Type;
                    if (t == TerrainType.River || t == TerrainType.Bridge) { nextToWater = true; break; }
                }
                if (!nextToWater) continue;

                if (Random.value < 0.7f)
                {
                    grid[x, z].Type = TerrainType.Sand;
                    firstLayer.Add((x, z));
                }
            }
        }

        foreach (var (sx, sz) in firstLayer)
        {
            for (int i = 0; i < 4; i++)
            {
                int nx = sx + dx[i], nz = sz + dz[i];
                if (nx < 0 || nx >= COLS || nz < 0 || nz >= ROWS) continue;
                if (grid[nx, nz].Type != TerrainType.Flat) continue;
                if (Random.value < 0.4f) grid[nx, nz].Type = TerrainType.Sand;
            }
        }
    }

    // ---- 川 ----

    static List<(int x, int z)> PlanRiverRoute()
    {
        if (Random.value < 0.3f) return null;

        int bias = Random.value < 0.5f ? 1 : -1;
        float biasChance     = Random.Range(0.35f, 0.65f);
        float straightChance = Random.Range(0.15f, 0.45f);
        float total = biasChance + straightChance;
        if (total > 0.9f) { biasChance *= 0.9f / total; straightChance *= 0.9f / total; }

        int z = Random.Range(BASE_ROWS + 2, ROWS - BASE_ROWS - 2);
        var path = new List<(int x, int z)>();

        for (int x = 0; x < COLS; x++)
        {
            path.Add((x, z));
            if (x < COLS - 1)
            {
                float r = Random.value;
                int dz = r < biasChance ? bias
                       : r < biasChance + straightChance ? 0
                       : -bias;
                z = Mathf.Clamp(z + dz, BASE_ROWS + 2, ROWS - BASE_ROWS - 3);
            }
        }

        return path;
    }

    static void ApplyRiver(TileCell[,] grid, List<(int x, int z)> route)
    {
        var fullPath = InsertElbows(route);
        foreach (var (rx, rz) in fullPath)
            grid[rx, rz].Type = TerrainType.River;

        ExpandRiverWidth(grid);
        PlaceBridges(grid, route);
    }

    static List<(int x, int z)> InsertElbows(List<(int x, int z)> path)
    {
        var result = new List<(int, int)>();
        for (int i = 0; i < path.Count; i++)
        {
            result.Add(path[i]);
            if (i < path.Count - 1)
            {
                var (ax, az) = path[i];
                var (bx, bz) = path[i + 1];
                if (ax != bx && az != bz)
                    result.Add((ax, bz));
            }
        }
        return result;
    }

    static void ExpandRiverWidth(TileCell[,] grid)
    {
        var original = new HashSet<(int, int)>();
        for (int x = 0; x < COLS; x++)
            for (int z = 0; z < ROWS; z++)
                if (grid[x, z].Type == TerrainType.River) original.Add((x, z));

        foreach (var (rx, rz) in original)
        {
            TryExpandRiver(grid, original, rx, rz + 1);
            TryExpandRiver(grid, original, rx, rz - 1, 0.3f);
        }
    }

    static void TryExpandRiver(TileCell[,] grid, HashSet<(int, int)> original, int x, int z, float chance = 0.5f)
    {
        if (z < BASE_ROWS || z >= ROWS - BASE_ROWS) return;
        if (original.Contains((x, z))) return;
        var t = grid[x, z].Type;
        if (t != TerrainType.Flat && t != TerrainType.Forest) return;
        if (Random.value > chance) return;
        grid[x, z].Type = TerrainType.River;
    }

    // ---- 橋（川幅全体を Bridge に変換） ----

    static void PlaceBridges(TileCell[,] grid, List<(int x, int z)> riverPath)
    {
        if (riverPath.Count == 0) return;

        var candidates = new List<(float score, int cx, int cz, bool zBridge)>();

        for (int x = 1; x < COLS - 1; x++)
        {
            int minRZ = int.MaxValue, maxRZ = int.MinValue;
            for (int z = 0; z < ROWS; z++)
                if (grid[x, z].Type == TerrainType.River)
                { minRZ = Mathf.Min(minRZ, z); maxRZ = Mathf.Max(maxRZ, z); }
            if (minRZ == int.MaxValue) continue;

            int belowZ = minRZ - 1, aboveZ = maxRZ + 1;
            if (belowZ < 0 || aboveZ >= ROWS) continue;

            float score = BridgeTerrainScore(grid[x, belowZ].Type)
                        + BridgeTerrainScore(grid[x, aboveZ].Type)
                        + BridgeDepthScore(grid, x, belowZ, 0, -1)
                        + BridgeDepthScore(grid, x, aboveZ, 0, +1)
                        + BridgePosScore(x, 1, COLS - 2);

            candidates.Add((score, x, (minRZ + maxRZ) / 2, true));
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        int targetCount = 1;
        var placed = new List<(int x, int z)>();

        foreach (var (_, cx, cz, zBridge) in candidates)
        {
            if (placed.Count >= targetCount) break;
            bool tooClose = false;
            foreach (var (px, pz) in placed)
                if (Mathf.Abs(cx - px) + Mathf.Abs(cz - pz) < 4) { tooClose = true; break; }
            if (tooClose) continue;

            SpanBridge(grid, cx, cz, zBridge);
            placed.Add((cx, cz));
        }

        if (placed.Count == 0 && candidates.Count > 0)
        {
            var (_, cx, cz, zBridge) = candidates[0];
            SpanBridge(grid, cx, cz, zBridge);
        }
    }

    static void SpanBridge(TileCell[,] grid, int cx, int cz, bool zBridge)
    {
        if (zBridge)
        {
            for (int z = 0; z < ROWS; z++)
                if (grid[cx, z].Type == TerrainType.River) grid[cx, z].Type = TerrainType.Bridge;
        }
        else
        {
            for (int x = 0; x < COLS; x++)
                if (grid[x, cz].Type == TerrainType.River) grid[x, cz].Type = TerrainType.Bridge;
        }
    }

    static float BridgeTerrainScore(TerrainType type) => type switch
    {
        TerrainType.Flat   => 3f,
        TerrainType.Sand   => 2f,
        TerrainType.Forest => 1f,
        _                  => 0f,
    };

    static float BridgeDepthScore(TileCell[,] grid, int x, int z, int dx, int dz)
    {
        float score = 0f;
        for (int i = 1; i <= 3; i++)
        {
            int nx = x + dx * i, nz = z + dz * i;
            if (nx < 0 || nx >= COLS || nz < 0 || nz >= ROWS) break;
            var t = grid[nx, nz].Type;
            if (t == TerrainType.Flat || t == TerrainType.Sand || t == TerrainType.Forest)
                score += 1f;
            else break;
        }
        return score;
    }

    static float BridgePosScore(int pos, int min, int max)
    {
        float center = (min + max) / 2f;
        float half   = (max - min) / 2f;
        return half > 0f ? 2f * (1f - Mathf.Abs(pos - center) / half) : 2f;
    }

    // ---- 灯台の配置（縦方向を count 等分してゾーンごとに1つ配置） ----

    static void PlaceLighthouses(TileCell[,] grid, int count)
    {
        int zMin     = BASE_ROWS + 2;
        int zMax     = ROWS - BASE_ROWS - 3;
        int zoneSize = Mathf.Max(1, (zMax - zMin) / count);
        float centerX = (COLS - 1) / 2f;
        var placed = new List<(int x, int z)>();

        for (int i = 0; i < count; i++)
        {
            int zFrom = zMin + i * zoneSize;
            int zTo   = (i == count - 1) ? zMax : zMin + (i + 1) * zoneSize;

            var candidates = new List<(float score, int x, int z)>();

            for (int x = 1; x < COLS - 1; x++)
            for (int z = zFrom; z < zTo; z++)
            {
                if (grid[x, z].Outpost != OutpostType.None) continue;

                float score = grid[x, z].Type switch
                {
                    TerrainType.Flat   => 10f,
                    TerrainType.Forest =>  8f,
                    TerrainType.Rocky  =>  5f,
                    _                  =>  0f
                };
                if (score <= 0f) continue;

                // 中央寄りを優先
                score *= 0.5f + 0.5f * (1f - Mathf.Abs(x - centerX) / centerX);

                bool tooClose = false;
                foreach (var (px, pz) in placed)
                    if (Mathf.Abs(x - px) + Mathf.Abs(z - pz) < 5) { tooClose = true; break; }
                if (tooClose) continue;

                candidates.Add((score, x, z));
            }

            if (candidates.Count == 0) continue;
            var (_, cx, cz) = WeightedRandom(candidates);
            grid[cx, cz].Outpost = OutpostType.Lighthouse;
            placed.Add((cx, cz));
        }
    }

    // ---- キャンプの配置（縦方向を count 等分してゾーンごとに1つ配置、灯台と重複しない） ----

    static void PlaceCamps(TileCell[,] grid, int count)
    {
        int zMin     = BASE_ROWS + 2;
        int zMax     = ROWS - BASE_ROWS - 3;
        int zoneSize = Mathf.Max(1, (zMax - zMin) / count);
        var placed = new List<(int x, int z)>();

        for (int i = 0; i < count; i++)
        {
            int zFrom = zMin + i * zoneSize;
            int zTo   = (i == count - 1) ? zMax : zMin + (i + 1) * zoneSize;

            var candidates = new List<(float score, int x, int z)>();
            for (int x = 1; x < COLS - 1; x++)
            for (int z = zFrom; z < zTo; z++)
            {
                if (grid[x, z].Outpost != OutpostType.None) continue;

                float score = grid[x, z].Type switch
                {
                    TerrainType.Flat   => 10f,
                    TerrainType.Forest =>  8f,
                    _                  =>  0f,
                };
                if (score <= 0f) continue;

                bool tooClose = false;
                foreach (var (px, pz) in placed)
                    if (Mathf.Abs(x - px) + Mathf.Abs(z - pz) < 4) { tooClose = true; break; }
                if (tooClose) continue;

                candidates.Add((score, x, z));
            }

            if (candidates.Count == 0) continue;
            var (_, cx, cz) = WeightedRandom(candidates);
            grid[cx, cz].Outpost = OutpostType.Camp;
            placed.Add((cx, cz));
        }
    }

    static (float score, int x, int z) WeightedRandom(List<(float score, int x, int z)> list)
    {
        float total = 0f;
        foreach (var item in list) total += item.score;
        float r = Random.Range(0f, total);
        float cum = 0f;
        foreach (var item in list)
        {
            cum += item.score;
            if (r <= cum) return item;
        }
        return list[list.Count - 1];
    }
}
