using System.Collections.Generic;
using UnityEngine;

// ランタイムで使用する地形到達可能性の計算（PlacementManager から参照）
// 学習用の SimTile[,] 版は GameSimulator（Editor専用）に残す
public static class TerrainReachability
{
    public static (int reachable, bool canReachLighthouse) CountReachableTiles(
        AnimalType type, int owner, TileCell[,] grid, IEnumerable<Vector2Int> lighthouses)
    {
        var blocked = AnimalDefinitions.GetBlockedTerrain(type);
        var offsets = AnimalDefinitions.GetMoveOffsets(type);
        int fSign   = owner == 1 ? 1 : -1;
        var visited = new HashSet<Vector2Int>();
        var queue   = new Queue<Vector2Int>();

        int zBase = owner == 1 ? 0 : TerrainGenerator.ROWS - TerrainGenerator.BASE_ROWS;
        for (int x = TerrainGenerator.HOME_MIN_X; x <= TerrainGenerator.HOME_MAX_X; x++)
        for (int dz = 0; dz < TerrainGenerator.BASE_ROWS; dz++)
        {
            var pos  = new Vector2Int(x, zBase + dz);
            var tile = grid[pos.x, pos.y];
            if (tile == null) continue;
            if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) continue;
            if (visited.Add(pos)) queue.Enqueue(pos);
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var raw in offsets)
            {
                var off  = new Vector2Int(raw.x, raw.y * fSign);
                var next = cur + off;
                if (next.x < 0 || next.x >= TerrainGenerator.COLS) continue;
                if (next.y < 0 || next.y >= TerrainGenerator.ROWS) continue;
                if (visited.Contains(next)) continue;
                var t = grid[next.x, next.y];
                if (t == null) continue;
                if (t.Type != TerrainType.Bridge && blocked.Contains(t.Type)) continue;
                if (System.Math.Abs(off.x) == 2 || System.Math.Abs(off.y) == 2)
                {
                    var mid = cur + new Vector2Int(off.x / 2, off.y / 2);
                    if (mid.x < 0 || mid.x >= TerrainGenerator.COLS) continue;
                    if (mid.y < 0 || mid.y >= TerrainGenerator.ROWS) continue;
                    var mt = grid[mid.x, mid.y];
                    if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
                }
                visited.Add(next);
                queue.Enqueue(next);
            }
        }

        bool canReach = false;
        if (lighthouses != null)
            foreach (var lh in lighthouses)
                if (visited.Contains(lh)) { canReach = true; break; }

        return (visited.Count, canReach);
    }
}
