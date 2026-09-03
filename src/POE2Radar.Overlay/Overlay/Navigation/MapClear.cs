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
    /// Stamp the walkable cells around the player into <paramref name="visited"/>: a disc of
    /// <paramref name="radius"/>, restricted to cells REACHABLE from the player's cell inside that disc
    /// (4-connected flood fill). Cells on the far side of a wall are not stamped, so the frontier they
    /// would otherwise expose (an unreachable cell adjacent to a "visited" one) never becomes a target —
    /// the bot walks around to them instead of stalling on an A* that cannot get there.
    /// Falls back to the plain disc when the player's own cell is not walkable (mid-transition / bad read).
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

        if (!TryFindSeed(walkable, width, height, cx, cy, out var sx, out var sy))
        {
            StampDisc(visited, walkable, width, height, cx, cy, r);
            return;
        }

        var side = 2 * r + 1;
        var seen = new bool[side * side];
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((sx, sy));
        seen[(sy - cy + r) * side + (sx - cx + r)] = true;
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            visited.Add(Key(x, y));
            Push(x + 1, y);
            Push(x - 1, y);
            Push(x, y + 1);
            Push(x, y - 1);
        }

        void Push(int x, int y)
        {
            var dx = x - cx;
            var dy = y - cy;
            if (dx * dx + dy * dy > r2) return;
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            var si = (dy + r) * side + (dx + r);
            if (seen[si]) return;
            seen[si] = true;
            if (walkable[y * width + x] == 0) return;
            queue.Enqueue((x, y));
        }
    }

    /// <summary>Plain (non-connected) disc stamp. Used by the fallback and by the stuck-target give-up.</summary>
    public static void StampDisc(HashSet<long> visited, byte[] walkable, int width, int height, int cx, int cy, int radius)
    {
        if (visited is null || walkable is null || width <= 0 || height <= 0) return;
        var r = Math.Max(0, radius);
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

    /// <summary>The player's cell if walkable, else the nearest walkable cell within 2 (rounding slop).</summary>
    private static bool TryFindSeed(byte[] walkable, int width, int height, int cx, int cy, out int sx, out int sy)
    {
        for (var ring = 0; ring <= 2; ring++)
        {
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    var x = cx + dx;
                    var y = cy + dy;
                    if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                    if (walkable[y * width + x] == 0) continue;
                    sx = x; sy = y;
                    return true;
                }
            }
        }
        sx = sy = 0;
        return false;
    }

    /// <summary>
    /// Pick a nav-target id for this zone, or null when the map is cleared / town.
    /// Order: nearest unique monster, nearest other hostile within <paramref name="maxMobDistance"/>
    /// (0 = unlimited; uniques are never capped), keep the current unvisited cell if it is still
    /// valid, else the nearest walkable neighbor of the visited set.
    /// </summary>
    public static string? PickTarget(
        string areaCode,
        NumVec2 player,
        byte[]? walkable,
        int width,
        int height,
        HashSet<long> visited,
        IReadOnlyList<MobHint> mobs,
        string? currentId,
        float maxMobDistance = 0f,
        NumVec2 heading = default,
        int stampRadius = 0)
    {
        if (QuestFollow.IsTownOrHideout(areaCode)) return null;

        string? bestUnique = null;
        var bestUniqueD = float.MaxValue;
        string? bestMob = null;
        var bestMobD = maxMobDistance > 0f ? maxMobDistance * maxMobDistance : float.MaxValue;
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
                else if (d <= bestMobD && (bestMob is null || d < bestMobD))
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

        // Sweep mode: only fog worth walking to counts; when nothing but slivers is left the zone is cleared.
        if (stampRadius > 0)
            return TryBestSweepTarget(walkable, width, height, visited, player, stampRadius, heading, out var bx, out var by)
                ? CellId(bx, by)
                : null;

        return TryNearestReachableUnvisited(walkable, width, height, visited, player, out var fx, out var fy, heading)
            ? CellId(fx, fy)
            : null;
    }

    /// <summary>
    /// Sweep planner — "go where the fog is". BFS from the player over walkable cells (true walking distance)
    /// up to <c>maxDepth</c>; every reachable UNVISITED cell is a candidate. Its gain = number of unvisited
    /// walkable cells the stamp disc would newly reveal from there, read in O(1) from a summed-area table
    /// built over the BFS bounding box. Its cost = steps walked, where a step through already-explored ground
    /// costs 1.0 and a step through unexplored ground only <see cref="UnexploredStepCost"/> (you clear it as
    /// you pass), so routes that cut back through cleared territory lose to routes that stay in the fog.
    /// Score = gain / (cost + r/2), ×(1 + 0.35·heading alignment). Candidates whose gain is under
    /// <see cref="MinGainFraction"/> of a full disc are slivers (a nook behind a pillar, a one-cell strip along
    /// a wall) and are ignored; when only slivers remain the zone counts as cleared.
    /// </summary>
    public const float UnexploredStepCost = 0.55f;
    public const float MinGainFraction = 0.05f;

    public static bool TryBestSweepTarget(
        byte[] walkable, int width, int height, HashSet<long> visited, NumVec2 player, int stampRadius,
        NumVec2 heading, out int bx, out int by)
    {
        bx = by = 0;
        var cx = (int)MathF.Round(player.X);
        var cy = (int)MathF.Round(player.Y);
        if (!TryFindSeed(walkable, width, height, cx, cy, out var sx, out var sy)) return false;

        var r = Math.Max(1, stampRadius);
        var maxDepth = Math.Clamp(r * 10, 80, 320);

        // ── Pass 1: BFS. Record each reached cell's weighted cost + the bounding box of the region. ──
        var seen = new System.Collections.BitArray(width * height);
        var queue = new Queue<int>();
        var cost = new Dictionary<int, float>();
        var depthOf = new Dictionary<int, int>();
        var reached = new List<int>();
        var start = sy * width + sx;
        queue.Enqueue(start);
        seen[start] = true;
        cost[start] = 0f;
        depthOf[start] = 0;
        int minX = sx, maxX = sx, minY = sy, maxY = sy;
        while (queue.Count > 0)
        {
            var idx = queue.Dequeue();
            var x = idx % width;
            var y = idx / width;
            reached.Add(idx);
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
            var d = depthOf[idx];
            if (d >= maxDepth) continue;
            var c = cost[idx];
            Push(x + 1, y, d, c); Push(x - 1, y, d, c); Push(x, y + 1, d, c); Push(x, y - 1, d, c);
        }

        // ── Pass 2: summed-area table of "unvisited walkable" over the bbox padded by r. ──
        var bx0 = Math.Max(0, minX - r); var by0 = Math.Max(0, minY - r);
        var bx1 = Math.Min(width - 1, maxX + r); var by1 = Math.Min(height - 1, maxY + r);
        var bw = bx1 - bx0 + 1; var bh = by1 - by0 + 1;
        var sat = new int[(bw + 1) * (bh + 1)];
        for (var y = 0; y < bh; y++)
        {
            var rowSum = 0;
            var gy = by0 + y;
            for (var x = 0; x < bw; x++)
            {
                var gx = bx0 + x;
                if (walkable[gy * width + gx] != 0 && !visited.Contains(Key(gx, gy))) rowSum++;
                sat[(y + 1) * (bw + 1) + (x + 1)] = sat[y * (bw + 1) + (x + 1)] + rowSum;
            }
        }
        int BoxGain(int gx, int gy)
        {
            var x0 = Math.Max(bx0, gx - r) - bx0; var x1 = Math.Min(bx1, gx + r) - bx0 + 1;
            var y0 = Math.Max(by0, gy - r) - by0; var y1 = Math.Min(by1, gy + r) - by0 + 1;
            var w1 = bw + 1;
            return sat[y1 * w1 + x1] - sat[y0 * w1 + x1] - sat[y1 * w1 + x0] + sat[y0 * w1 + x0];
        }

        // ── Pass 3: score every reachable unvisited cell. ──
        var fullBox = (2 * r + 1) * (2 * r + 1);
        var minGain = Math.Max(4, (int)(fullBox * MinGainFraction));
        var useHeading = heading.LengthSquared() > 1e-3f;
        var hdir = useHeading ? NumVec2.Normalize(heading) : default;
        var bestScore = float.MinValue;
        var found = false;
        foreach (var idx in reached)
        {
            var x = idx % width;
            var y = idx / width;
            if (visited.Contains(Key(x, y))) continue;
            var gain = BoxGain(x, y);
            if (gain < minGain) continue;
            var score = gain / (cost[idx] + r * 0.5f);
            if (useHeading)
            {
                var dir = new NumVec2(x - player.X + 1e-3f, y - player.Y);
                score *= 1f + 0.35f * MathF.Max(0f, NumVec2.Dot(NumVec2.Normalize(dir), hdir));
            }
            if (score > bestScore) { bestScore = score; bx = x; by = y; found = true; }
        }
        return found;

        void Push(int x, int y, int d, float c)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            var i = y * width + x;
            if (seen[i]) return;
            seen[i] = true;
            if (walkable[i] == 0) return;
            queue.Enqueue(i);
            depthOf[i] = d + 1;
            cost[i] = c + (visited.Contains(Key(x, y)) ? 1f : UnexploredStepCost);
        }
    }

    /// <summary>
    /// Breadth-first walk over the walkable grid from the player's cell: the first unvisited cell popped is
    /// the nearest one by TRUE walking distance (not straight-line), and is reachable by construction — so
    /// the bot never targets a cell that is close as the crow flies but behind a wall, and never bounces
    /// between two "nearest" cells on opposite sides of an obstacle. Ties (same BFS depth) prefer the cell
    /// that continues the player's current heading, which keeps sweeps straight instead of zig-zagging.
    /// </summary>
    public static bool TryNearestReachableUnvisited(
        byte[] walkable, int width, int height, HashSet<long> visited, NumVec2 player, out int fx, out int fy,
        NumVec2 heading = default)
    {
        fx = fy = 0;
        var cx = (int)MathF.Round(player.X);
        var cy = (int)MathF.Round(player.Y);
        if (!TryFindSeed(walkable, width, height, cx, cy, out var sx, out var sy)) return false;

        var seen = new System.Collections.BitArray(width * height);
        var queue = new Queue<int>();
        var start = sy * width + sx;
        queue.Enqueue(start);
        seen[start] = true;
        var useHeading = heading.LengthSquared() > 1e-3f;
        var hdir = useHeading ? NumVec2.Normalize(heading) : default;

        // Process one BFS depth at a time so ties can be broken by heading.
        while (queue.Count > 0)
        {
            var layer = queue.Count;
            var found = false;
            var bestDot = float.MinValue;
            for (var i = 0; i < layer; i++)
            {
                var idx = queue.Dequeue();
                var x = idx % width;
                var y = idx / width;
                if (!visited.Contains(Key(x, y)))
                {
                    var dot = useHeading ? NumVec2.Dot(NumVec2.Normalize(new NumVec2(x - player.X + 1e-3f, y - player.Y)), hdir) : 0f;
                    if (!found || dot > bestDot)
                    {
                        found = true;
                        bestDot = dot;
                        fx = x; fy = y;
                    }
                    if (!useHeading) break;
                    continue;
                }
                Push(x + 1, y); Push(x - 1, y); Push(x, y + 1); Push(x, y - 1);
            }
            if (found) return true;
        }
        return false;

        void Push(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            var i = y * width + x;
            if (seen[i]) return;
            seen[i] = true;
            if (walkable[i] == 0) return;
            queue.Enqueue(i);
        }
    }

    private static bool IsUnvisitedWalkable(
        byte[]? walkable, int width, int height, HashSet<long> visited, int x, int y)
    {
        if (walkable is null || visited is null) return false;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        if (walkable[y * width + x] == 0) return false;
        return !visited.Contains(Key(x, y));
    }
}
