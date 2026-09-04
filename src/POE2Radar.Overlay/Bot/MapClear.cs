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

        // Player off walkable ground (doorway / ledge lip): the disc is centred on the ground beside him — the
        // seed — so the flood fill's window contains it and the reveal covers where he actually is.
        if (sx != cx || sy != cy) { cx = sx; cy = sy; }

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

    /// <summary>Seed search radius: the player can stand a few cells off walkable ground (a door threshold, a
    /// ledge lip — collision is looser than the nav grid); the sweep must still anchor to the hall beside him.</summary>
    public const int SeedRadius = 6;

    /// <summary>The player's cell if walkable, else the nearest walkable cell within <see cref="SeedRadius"/>.</summary>
    private static bool TryFindSeed(byte[] walkable, int width, int height, int cx, int cy, out int sx, out int sy)
    {
        for (var ring = 0; ring <= SeedRadius; ring++)
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
        int stampRadius = 0,
        IReadOnlyList<MapEvents.Event>? events = null,
        float maxEventDistance = 0f)
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

        // Map events (essence / strongbox / shrine / breach / stalled mob …): after the fight, before the sweep.
        if (events is { Count: > 0 } && MapEvents.Nearest(events, player, maxEventDistance) is { } ev)
            return "e:" + ev.Id;

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
    /// Sweep planner — "go where the fog is". Dijkstra from the player over walkable cells (true walking distance)
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

        // Everything below works in a WINDOW around the player (player ± maxDepth, padded by r for the gain
        // disc, clipped to the map) on flat reusable arrays — no per-cell dictionary/hash work in the walk.
        var wx0 = Math.Max(0, sx - maxDepth - r); var wy0 = Math.Max(0, sy - maxDepth - r);
        var wx1 = Math.Min(width - 1, sx + maxDepth + r); var wy1 = Math.Min(height - 1, sy + maxDepth + r);
        var ww = wx1 - wx0 + 1; var wh = wy1 - wy0 + 1;
        var n = ww * wh;
        var s = SweepScratch.Get(n, (ww + 1) * (wh + 1));
        var cost = s.Cost; var depth = s.Depth; var sat = s.Sat; var unvisited = s.Unvisited;

        // ── Pass 1: window masks — walkable-and-unvisited per cell + its summed-area table. ──
        var w1 = ww + 1;
        Array.Clear(sat, 0, w1 * (wh + 1));
        for (var y = 0; y < wh; y++)
        {
            var rowSum = 0;
            var gy = wy0 + y;
            var rowBase = gy * width;
            for (var x = 0; x < ww; x++)
            {
                var gx = wx0 + x;
                var u = walkable[rowBase + gx] != 0 && !visited.Contains(Key(gx, gy));
                unvisited[y * ww + x] = u;
                if (u) rowSum++;
                sat[(y + 1) * w1 + (x + 1)] = sat[y * w1 + (x + 1)] + rowSum;
            }
        }
        int BoxGain(int gx, int gy)
        {
            var x0 = Math.Max(wx0, gx - r) - wx0; var x1 = Math.Min(wx1, gx + r) - wx0 + 1;
            var y0 = Math.Max(wy0, gy - r) - wy0; var y1 = Math.Min(wy1, gy + r) - wy0 + 1;
            return sat[y1 * w1 + x1] - sat[y0 * w1 + x1] - sat[y1 * w1 + x0] + sat[y0 * w1 + x0];
        }

        // ── Pass 2: Dijkstra from the player (4-connected, true walking distance) with the weighted step cost,
        //    bounded by maxDepth steps. Scores every reachable unvisited cell as it settles. ──
        Array.Fill(depth, -1, 0, n);
        var fullBox = (2 * r + 1) * (2 * r + 1);
        var minGain = Math.Max(4, (int)(fullBox * MinGainFraction));
        var useHeading = heading.LengthSquared() > 1e-3f;
        var hdir = useHeading ? NumVec2.Normalize(heading) : default;
        var bestScore = float.MinValue;
        var found = false;

        var heap = s.Heap;
        heap.Clear();
        var startLocal = (sy - wy0) * ww + (sx - wx0);
        cost[startLocal] = 0f;
        depth[startLocal] = 0;
        heap.Push(startLocal, 0f);
        while (heap.TryPop(out var li, out var c))
        {
            if (c > cost[li]) continue; // stale entry
            var lx = li % ww; var ly = li / ww;
            var gx = lx + wx0; var gy = ly + wy0;
            if (unvisited[li])
            {
                var gain = BoxGain(gx, gy);
                if (gain >= minGain)
                {
                    var score = gain / (c + r * 0.5f);
                    if (useHeading)
                    {
                        var dir = new NumVec2(gx - player.X + 1e-3f, gy - player.Y);
                        score *= 1f + 0.35f * MathF.Max(0f, NumVec2.Dot(NumVec2.Normalize(dir), hdir));
                    }
                    if (score > bestScore) { bestScore = score; bx = gx; by = gy; found = true; }
                }
            }
            var d = depth[li];
            if (d >= maxDepth) continue;
            if (lx > 0)      Relax(li - 1,  gx - 1, gy,     d, c);
            if (lx < ww - 1) Relax(li + 1,  gx + 1, gy,     d, c);
            if (ly > 0)      Relax(li - ww, gx,     gy - 1, d, c);
            if (ly < wh - 1) Relax(li + ww, gx,     gy + 1, d, c);
        }
        return found;

        void Relax(int li, int gx, int gy, int d, float c)
        {
            if (walkable[gy * width + gx] == 0) return;
            var nc = c + (unvisited[li] ? UnexploredStepCost : 1f);
            if (depth[li] >= 0 && nc >= cost[li]) return;
            cost[li] = nc;
            depth[li] = d + 1;
            heap.Push(li, nc);
        }
    }

    /// <summary>Reusable per-thread scratch for the sweep (sized to the largest window seen so far).</summary>
    private sealed class SweepScratch
    {
        [ThreadStatic] private static SweepScratch? _instance;
        public float[] Cost = Array.Empty<float>();
        public int[] Depth = Array.Empty<int>();
        public int[] Sat = Array.Empty<int>();
        public bool[] Unvisited = Array.Empty<bool>();
        public readonly MinHeap Heap = new();

        public static SweepScratch Get(int cells, int satCells)
        {
            var s = _instance ??= new SweepScratch();
            if (s.Cost.Length < cells)
            {
                s.Cost = new float[cells];
                s.Depth = new int[cells];
                s.Unvisited = new bool[cells];
            }
            if (s.Sat.Length < satCells) s.Sat = new int[satCells]; // (ww+1)×(wh+1): one extra row + column
            return s;
        }
    }

    /// <summary>Array-backed binary min-heap of (index, key) with lazy deletion.</summary>
    private sealed class MinHeap
    {
        private int[] _node = new int[1024];
        private float[] _key = new float[1024];
        private int _count;

        public void Clear() => _count = 0;

        public void Push(int node, float key)
        {
            if (_count == _node.Length) { Array.Resize(ref _node, _count * 2); Array.Resize(ref _key, _count * 2); }
            var i = _count++;
            while (i > 0)
            {
                var p = (i - 1) >> 1;
                if (_key[p] <= key) break;
                _node[i] = _node[p]; _key[i] = _key[p]; i = p;
            }
            _node[i] = node; _key[i] = key;
        }

        public bool TryPop(out int node, out float key)
        {
            if (_count == 0) { node = 0; key = 0; return false; }
            node = _node[0]; key = _key[0];
            var last = --_count;
            if (last == 0) return true;
            var ln = _node[last]; var lk = _key[last];
            var i = 0;
            while (true)
            {
                var c = 2 * i + 1;
                if (c >= last) break;
                if (c + 1 < last && _key[c + 1] < _key[c]) c++;
                if (_key[c] >= lk) break;
                _node[i] = _node[c]; _key[i] = _key[c]; i = c;
            }
            _node[i] = ln; _key[i] = lk;
            return true;
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
