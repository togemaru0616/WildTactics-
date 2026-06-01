using UnityEngine;
using System.Collections.Generic;

public static class GridPathfinder
{
    // A* 内部キャッシュ（MainThread 専用・毎回 Clear() して再利用）
    static readonly Dictionary<Vector2Int, float>     _gScore   = new();
    static readonly Dictionary<Vector2Int, float>     _fScore   = new();
    static readonly HashSet<Vector2Int>               _openSet  = new();
    static readonly Dictionary<Vector2Int, Vector2Int> _cameFrom = new();

    // 経路を返す。到達不可なら null
    public static List<Vector2Int> FindPath(
        Vector2Int start, Vector2Int end,
        AnimalType animal, int owner,
        TileCell[,] grid, HashSet<Vector2Int> friendlyOccupied)
    {
        _cameFrom.Clear();
        float cost = Search(start, end, animal, owner, grid, friendlyOccupied, _cameFrom);
        return cost < float.MaxValue ? Reconstruct(_cameFrom, end) : null;
    }

    // 移動コスト（秒）を返す。到達不可なら float.MaxValue
    public static float FindPathCost(
        Vector2Int start, Vector2Int end,
        AnimalType animal, int owner, TileCell[,] grid)
    {
        if (start == end) return 0f;
        float terrainCost = Search(start, end, animal, owner, grid, null, null);
        return terrainCost < float.MaxValue
            ? terrainCost * AnimalDefinitions.GetMoveTime(animal)
            : float.MaxValue;
    }

    // ---- A* コア ----
    // cameFrom が null の場合はパス再構築をスキップ（コスト計算専用）
    // 戻り値: end までの地形コスト合計（到達不可なら float.MaxValue）
    static float Search(
        Vector2Int start, Vector2Int end,
        AnimalType animal, int owner,
        TileCell[,] grid, HashSet<Vector2Int> friendlyOccupied,
        Dictionary<Vector2Int, Vector2Int> cameFrom)
    {
        var blocked   = AnimalDefinitions.GetBlockedTerrain(animal);
        var offsets   = AnimalDefinitions.GetMoveOffsets(animal);
        int frontSign = owner == 1 ? 1 : -1;

        _gScore.Clear();
        _fScore.Clear();
        _openSet.Clear();

        _gScore[start] = 0f;
        _fScore[start] = Heuristic(start, end);
        _openSet.Add(start);

        while (_openSet.Count > 0)
        {
            var current = BestNode(_openSet, _fScore);
            if (current == end) return _gScore[current];
            _openSet.Remove(current);

            foreach (var rawOffset in offsets)
            {
                var offset = new Vector2Int(rawOffset.x, rawOffset.y * frontSign);
                var next   = current + offset;

                // 2マスジャンプは中間タイルも地形チェック
                if (Mathf.Abs(offset.y) == 2 || Mathf.Abs(offset.x) == 2)
                {
                    var mid = GetTile(grid, current + new Vector2Int(offset.x / 2, offset.y / 2));
                    if (mid == null || blocked.Contains(mid.Type)) continue;
                }

                var tile = GetTile(grid, next);
                if (tile == null) continue;
                if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) continue;
                if (friendlyOccupied != null && friendlyOccupied.Contains(next) && next != end) continue;

                float newG = _gScore[current] + AnimalDefinitions.GetTerrainMult(animal, tile.Type);
                if (!_gScore.TryGetValue(next, out float existing) || newG < existing)
                {
                    if (cameFrom != null) cameFrom[next] = current;
                    _gScore[next] = newG;
                    _fScore[next] = newG + Heuristic(next, end);
                    _openSet.Add(next);
                }
            }
        }
        return float.MaxValue;
    }

    static Vector2Int BestNode(HashSet<Vector2Int> openSet, Dictionary<Vector2Int, float> fScore)
    {
        Vector2Int best = default;
        float bestF = float.MaxValue;
        foreach (var n in openSet)
        {
            float f = fScore.TryGetValue(n, out float v) ? v : float.MaxValue;
            if (f < bestF) { bestF = f; best = n; }
        }
        return best;
    }

    static float Heuristic(Vector2Int a, Vector2Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    static TileCell GetTile(TileCell[,] grid, Vector2Int pos)
    {
        if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) return null;
        if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) return null;
        return grid[pos.x, pos.y];
    }

    static List<Vector2Int> Reconstruct(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int cur)
    {
        var path = new List<Vector2Int>();
        while (cameFrom.TryGetValue(cur, out var prev)) { path.Add(cur); cur = prev; }
        path.Reverse();
        return path;
    }
}
