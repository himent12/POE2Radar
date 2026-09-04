namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Turns a cell-by-cell A* path into a short, clean polyline of waypoints in three passes:
/// <list type="number">
/// <item><b>String pulling</b> — greedy farthest-visible over a THICK line-of-sight (every cell the segment
/// crosses must have ≥ <c>preferredRaw</c> clearance). A shortcut that would graze a corner is rejected, so
/// waypoints only sit where the character actually fits. Where the thick test cannot advance at all (a
/// corridor narrower than the preferred clearance) a relaxed pass at <c>minRaw</c> keeps it simplified.</item>
/// <item><b>Relaxation</b> — each interior waypoint is nudged (≤ <see cref="RelaxRadius"/> cells) to the
/// neighbouring cell with the greatest clearance whose two adjacent segments still pass the same thick test,
/// provided the detour is tiny. Corner waypoints move off the apex; the line rounds the corner instead of
/// kissing it.</item>
/// <item><b>Re-pull</b> — a final string pull removes waypoints made redundant by the nudges.</item>
/// </list>
/// </summary>
public static class PathSmoother
{
    /// <summary>Hard cap on one straight segment (cells). Thick LOS already guarantees the segment is safe;
    /// the cap only keeps a route's waypoints dense enough for off-path/progress tracking.</summary>
    public const float MaxSegmentCells = 220f;
    public const int RelaxRadius = 2;
    private const float RelaxMaxExtraCells = 1.5f;
    private const float RelaxMaxExtraFraction = 0.03f;

    public static IReadOnlyList<PathCell> Smooth(NavGrid g, IReadOnlyList<PathCell> path, int preferredRaw, int minRaw)
    {
        if (path.Count <= 2) return path;
        var pulled = StringPull(g, path, preferredRaw, minRaw);
        if (pulled.Count <= 2) return pulled;
        var relaxed = Relax(g, pulled, preferredRaw, minRaw);
        return relaxed ? StringPull(g, pulled, preferredRaw, minRaw) : pulled;
    }

    private static List<PathCell> StringPull(NavGrid g, IReadOnlyList<PathCell> path, int preferredRaw, int minRaw)
    {
        var result = new List<PathCell>(Math.Max(4, path.Count / 6)) { path[0] };
        var current = 0;
        while (current < path.Count - 1)
        {
            var farthest = Farthest(g, path, current, path.Count - 1, preferredRaw, minRaw);
            if (farthest == current + 1 && minRaw < preferredRaw)
            {
                // Preferred clearance cannot advance: we are inside a section narrower than it (a door, a
                // one-wide corridor). The relaxed pull is BOUNDED to that section — it may reach at most the
                // first path cell that regains preferred clearance — so it bridges the squeeze and then hands
                // straight back to the thick pass. It can never replace a long clearance-weighted A* detour
                // with one wall-hugging chord.
                var limit = current + 1;
                while (limit < path.Count - 1 && g.ClearanceRaw(path[limit].X, path[limit].Y) < preferredRaw) limit++;
                farthest = Farthest(g, path, current, limit, minRaw, minRaw);
            }
            result.Add(path[farthest]);
            current = farthest;
        }
        return result;
    }

    /// <summary>Farthest index in (current, last] with a clear thick line from <c>path[current]</c>; interior cells
    /// need <paramref name="minRaw"/>, the two endpoints only <paramref name="endpointRaw"/>.</summary>
    private static int Farthest(NavGrid g, IReadOnlyList<PathCell> path, int current, int last, int minRaw, int endpointRaw)
    {
        var a = path[current];
        for (var i = last; i > current + 1; i--)
        {
            var dx = path[i].X - a.X;
            var dy = path[i].Y - a.Y;
            if (dx * dx + dy * dy > MaxSegmentCells * MaxSegmentCells) continue;
            if (g.HasClearLine(a.X, a.Y, path[i].X, path[i].Y, minRaw, endpointRaw)) return i;
        }
        return current + 1;
    }

    /// <summary>Nudge interior waypoints toward higher clearance. Returns true when anything moved.</summary>
    private static bool Relax(NavGrid g, List<PathCell> pts, int preferredRaw, int minRaw)
    {
        var moved = false;
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var prev = pts[i - 1];
            var next = pts[i + 1];
            var cur  = pts[i];
            // The clearance level the existing segments actually satisfy (preferred, else min). Endpoints of the
            // pair (prev/next) may sit against a wall; only the segment interiors are graded.
            var level = g.HasClearLine(prev.X, prev.Y, cur.X, cur.Y, preferredRaw, minRaw)
                     && g.HasClearLine(cur.X, cur.Y, next.X, next.Y, preferredRaw, minRaw) ? preferredRaw : minRaw;
            var baseLen = Dist(prev, cur) + Dist(cur, next);
            var budget = baseLen * RelaxMaxExtraFraction + RelaxMaxExtraCells;
            var bestClear = g.ClearanceRaw(cur.X, cur.Y);
            var bestLen = baseLen;
            var best = cur;
            for (var dy = -RelaxRadius; dy <= RelaxRadius; dy++)
            {
                for (var dx = -RelaxRadius; dx <= RelaxRadius; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var cx = cur.X + dx;
                    var cy = cur.Y + dy;
                    var c = g.ClearanceRaw(cx, cy);
                    if (c < bestClear) continue;
                    var cand = new PathCell(cx, cy);
                    var len = Dist(prev, cand) + Dist(cand, next);
                    if (len > baseLen + budget) continue;
                    if (c == bestClear && len >= bestLen) continue;
                    if (!g.HasClearLine(prev.X, prev.Y, cx, cy, level, minRaw) || !g.HasClearLine(cx, cy, next.X, next.Y, level, minRaw)) continue;
                    bestClear = c; bestLen = len; best = cand;
                }
            }
            if (best != cur) { pts[i] = best; moved = true; }
        }
        return moved;
    }

    private static float Dist(PathCell a, PathCell b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
