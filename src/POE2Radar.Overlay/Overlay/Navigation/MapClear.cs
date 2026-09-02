using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Navigation;

/// <summary>
/// Map-clear nav picker: walk the zone until every walkable cell is visited. Unique bosses
/// first, then the nearest live hostile, then the nearest unexplored frontier cell.
/// Pure functions — tests never need a live client. Cell ids are <c>c:x,y</c>.
/// </summary>
public static class MapClear
{
    public readonly record struct MobHint(string Id, NumVec2 Grid, bool Unique);

    public static string CellId(int x, int y) => $"c:{x},{y}";

    public static bool TryParseCell(string? id, out int x, out int y)
    {
        x = y = 0;
        if (id is null || !id.StartsWith("c:", StringComparison.Ordinal)) return false;
        var comma = id.IndexOf(',', 2);
        if (comma < 0) return false;
        return int.TryParse(id.AsSpan(2, comma - 2), out x)
            && int.TryParse(id.AsSpan(comma + 1), out y);
    }

    public static long Key(int x, int y) => ((long)y << 32) | (uint)x;

    /// <summary>
    /// Stamp a disc of walkable cells around the player into <paramref name="visited"/>.
    /// </summary>
    public static void StampVisited(
        HashSet<long> visited,
        byte[] walkable,
        int width,
        int height,
        NumVec2 player,
        int radius)
    {
        if (visited is null || walkable is null || width <= 0 || height <= 0) return;
        var r = Math.Max(0, radius);
        var cx = (int)MathF.Round(player.X);
        var cy = (int)MathF.Round(player.Y);
        var r2 = r * r;
        for (var dy = -r; dy <= r; dy++)
        {
            var y = cy + dy;
            if ((uint)y >= (uint)height) continue;
            var row = y * width;
            for (var dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                var x = cx + dx;
                if ((uint)x >= (uint)width) continue;
                if (walkable[row + x] == 0) continue;
                visited.Add(Key(x, y));
            }
        }
    }

    /// <summary>
    /// Pick a nav-target id for this zone, or null when the map is cleared / town.
    /// Order: nearest unique monster, nearest other hostile, keep the current unvisited
    /// cell if it is still valid, else the nearest walkable neighbor of the visited set.
    /// </summary>
    public static string? PickTarget(
        string areaCode,
        NumVec2 player,
        byte[]? walkable,
        int width,
        int height,
        HashSet<long> visited,
        IReadOnlyList<MobHint> mobs,
        string? currentId)
    {
        if (QuestFollow.IsTownOrHideout(areaCode)) return null;

        string? bestUnique = null;
        var bestUniqueD = float.MaxValue;
        string? bestMob = null;
        var bestMobD = float.MaxValue;
        if (mobs is not null)
        {
            foreach (var m in mobs)
            {
                var dx = m.Grid.X - player.X;
                var dy = m.Grid.Y - player.Y;
                var d = dx * dx + dy * dy;
                if (m.Unique)
                {
                    if (d < bestUniqueD) { bestUniqueD = d; bestUnique = m.Id; }
                }
                else if (d < bestMobD)
                {
                    bestMobD = d;
                    bestMob = m.Id;
                }
            }
        }
        if (bestUnique is not null) return bestUnique;
        if (bestMob is not null) return bestMob;

        if (currentId is not null
            && TryParseCell(currentId, out var holdX, out var holdY)
            && IsUnvisitedWalkable(walkable, width, height, visited, holdX, holdY))
            return currentId;

        if (walkable is null || width <= 0 || height <= 0 || visited is null || visited.Count == 0)
            return null;

        var bestX = 0;
        var bestY = 0;
        var bestD = float.MaxValue;
        var found = false;
        foreach (var key in visited)
        {
            var x = (int)(uint)key;
            var y = (int)(key >> 32);
            Consider(walkable, width, height, visited, player, x + 1, y, ref found, ref bestD, ref bestX, ref bestY);
            Consider(walkable, width, height, visited, player, x - 1, y, ref found, ref bestD, ref bestX, ref bestY);
            Consider(walkable, width, height, visited, player, x, y + 1, ref found, ref bestD, ref bestX, ref bestY);
            Consider(walkable, width, height, visited, player, x, y - 1, ref found, ref bestD, ref bestX, ref bestY);
        }
        return found ? CellId(bestX, bestY) : null;
    }

    private static bool IsUnvisitedWalkable(
        byte[]? walkable, int width, int height, HashSet<long> visited, int x, int y)
    {
        if (walkable is null || visited is null) return false;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        if (walkable[y * width + x] == 0) return false;
        return !visited.Contains(Key(x, y));
    }

    private static void Consider(
        byte[] walkable, int width, int height, HashSet<long> visited, NumVec2 player,
        int x, int y, ref bool found, ref float bestD, ref int bestX, ref int bestY)
    {
        if (!IsUnvisitedWalkable(walkable, width, height, visited, x, y)) return;
        var dx = x - player.X;
        var dy = y - player.Y;
        var d = dx * dx + dy * dy;
        if (!found || d < bestD)
        {
            found = true;
            bestD = d;
            bestX = x;
            bestY = y;
        }
    }
}
