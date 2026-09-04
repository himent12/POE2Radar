namespace POE2Radar.Core.Pathfinding;

/// <summary>One cell on a path — a grid coordinate.</summary>
public readonly record struct PathCell(int X, int Y);

public readonly record struct Path(bool Found, float Cost, IReadOnlyList<PathCell> Cells)
{
    public static readonly Path NoPath = new(false, 0f, Array.Empty<PathCell>());
}

/// <summary>
/// 8-connected A* over a <see cref="NavGrid"/>. Hot loop works on raw arrays (no interface reads), a
/// custom array-backed binary heap (no per-call allocation — all buffers are sized once to the grid and
/// reused via a generation stamp), and precomputed neighbour index offsets.
///
/// <para>Cost model: stepping INTO a cell costs <c>stepLength × NavGrid.StepMultiplier[clearance]</c> —
/// 1.0 in the open, rising smoothly toward walls — so routes are optimal for "shortest while staying off the
/// walls". Diagonal steps cost √2 and are refused when either orthogonal neighbour is blocked (no corner
/// cutting). The octile heuristic is admissible against this cost (multiplier ≥ 1), so the result is optimal
/// up to the 0.1 % tie-break inflation.</para>
/// </summary>
public sealed class AStar
{
    private const float Sqrt2 = 1.4142136f;
    private const float TieBreak = 1.001f;

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _gScore;
    private readonly int[]   _cameFrom;
    private readonly int[]   _openGen;    // generation stamp: cell has a g-score this search
    private readonly int[]   _closedGen;  // generation stamp: cell has been expanded this search
    private int _currentGen;

    // Binary min-heap (lazy deletion: stale entries are skipped when popped).
    private int[]   _heapNode;
    private float[] _heapKey;
    private int     _heapCount;

    public int Width  => _width;
    public int Height => _height;
    /// <summary>Nodes expanded by the last search (diagnostics).</summary>
    public int LastExpanded { get; private set; }

    public AStar(int width, int height)
    {
        _width  = width;
        _height = height;
        var n = width * height;
        _gScore    = new float[n];
        _cameFrom  = new int[n];
        _openGen   = new int[n];
        _closedGen = new int[n];
        var heapCap = Math.Clamp(n / 8, 1024, 1 << 20);
        _heapNode = new int[heapCap];
        _heapKey  = new float[heapCap];
    }

    /// <summary>
    /// Pathfind between two WALKABLE cells (the planner snaps beforehand). Returns <see cref="Path.NoPath"/>
    /// when the goal is unreachable or the node budget (<paramref name="maxNodes"/> expansions) is exhausted.
    /// <paramref name="heuristicWeight"/> &gt; 1 trades optimality for speed (weighted A*); 1 = optimal.
    /// </summary>
    public Path FindPath(NavGrid g, PathCell start, PathCell goal, int maxNodes = int.MaxValue, float heuristicWeight = 1f)
    {
        if (g.Width != _width || g.Height != _height)
            throw new ArgumentException($"Grid dims {g.Width}x{g.Height} != A* dims {_width}x{_height}");
        if (!g.IsWalkable(start.X, start.Y) || !g.IsWalkable(goal.X, goal.Y)) return Path.NoPath;
        if (!g.SameRegion(start.X, start.Y, goal.X, goal.Y)) return Path.NoPath;

        unchecked { _currentGen++; }
        if (_currentGen == 0) { Array.Clear(_openGen); Array.Clear(_closedGen); _currentGen = 1; }
        _heapCount = 0;
        LastExpanded = 0;

        var w = _width;
        var h = _height;
        var walk = g.Walkable;
        var clear = g.Clearance;
        var mult = NavGrid.StepMultiplier;
        var hw = heuristicWeight * TieBreak;

        var startIdx = start.Y * w + start.X;
        var goalIdx  = goal.Y * w + goal.X;
        var gx = goal.X;
        var gy = goal.Y;

        _gScore[startIdx] = 0f;
        _cameFrom[startIdx] = -1;
        _openGen[startIdx] = _currentGen;
        Push(startIdx, Octile(start.X, start.Y, gx, gy) * hw);

        var expanded = 0;
        while (_heapCount > 0)
        {
            var cur = Pop();
            if (_closedGen[cur] == _currentGen) continue; // stale heap entry
            _closedGen[cur] = _currentGen;

            if (cur == goalIdx)
            {
                LastExpanded = expanded;
                return Reconstruct(cur, _gScore[cur]);
            }
            if (++expanded > maxNodes) break;

            var cx = cur % w;
            var cy = cur / w;
            var curG = _gScore[cur];

            // Orthogonal openness, reused by the diagonal corner-cut checks.
            var canL = cx > 0     && walk[cur - 1] != 0;
            var canR = cx < w - 1 && walk[cur + 1] != 0;
            var canU = cy > 0     && walk[cur - w] != 0;
            var canD = cy < h - 1 && walk[cur + w] != 0;

            if (canL) Relax(cur - 1,     cx - 1, cy,     1f,    curG);
            if (canR) Relax(cur + 1,     cx + 1, cy,     1f,    curG);
            if (canU) Relax(cur - w,     cx,     cy - 1, 1f,    curG);
            if (canD) Relax(cur + w,     cx,     cy + 1, 1f,    curG);
            if (canL && canU) Relax(cur - w - 1, cx - 1, cy - 1, Sqrt2, curG);
            if (canR && canU) Relax(cur - w + 1, cx + 1, cy - 1, Sqrt2, curG);
            if (canL && canD) Relax(cur + w - 1, cx - 1, cy + 1, Sqrt2, curG);
            if (canR && canD) Relax(cur + w + 1, cx + 1, cy + 1, Sqrt2, curG);

            void Relax(int nIdx, int nx, int ny, float step, float fromG)
            {
                if (walk[nIdx] == 0 || _closedGen[nIdx] == _currentGen) return;
                var tentative = fromG + step * mult[clear[nIdx]];
                if (_openGen[nIdx] == _currentGen && tentative >= _gScore[nIdx]) return;
                _gScore[nIdx]   = tentative;
                _cameFrom[nIdx] = cur;
                _openGen[nIdx]  = _currentGen;
                Push(nIdx, tentative + Octile(nx, ny, gx, gy) * hw);
            }
        }

        LastExpanded = expanded;
        return Path.NoPath;
    }

    private static float Octile(int x, int y, int gx, int gy)
    {
        var dx = Math.Abs(x - gx);
        var dy = Math.Abs(y - gy);
        return (dx + dy) + (Sqrt2 - 2f) * Math.Min(dx, dy);
    }

    private Path Reconstruct(int goalIdx, float cost)
    {
        var count = 0;
        for (var i = goalIdx; i != -1; i = _cameFrom[i]) count++;
        var cells = new PathCell[count];
        var k = count;
        for (var i = goalIdx; i != -1; i = _cameFrom[i])
            cells[--k] = new PathCell(i % _width, i / _width);
        return new Path(true, cost, cells);
    }

    // ── Heap ────────────────────────────────────────────────────────────────────────────────

    private void Push(int node, float key)
    {
        if (_heapCount == _heapNode.Length)
        {
            Array.Resize(ref _heapNode, _heapNode.Length * 2);
            Array.Resize(ref _heapKey,  _heapKey.Length  * 2);
        }
        var i = _heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (_heapKey[parent] <= key) break;
            _heapNode[i] = _heapNode[parent];
            _heapKey[i]  = _heapKey[parent];
            i = parent;
        }
        _heapNode[i] = node;
        _heapKey[i]  = key;
    }

    private int Pop()
    {
        var top = _heapNode[0];
        var last = --_heapCount;
        if (last == 0) return top;
        var node = _heapNode[last];
        var key  = _heapKey[last];
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= last) break;
            if (child + 1 < last && _heapKey[child + 1] < _heapKey[child]) child++;
            if (_heapKey[child] >= key) break;
            _heapNode[i] = _heapNode[child];
            _heapKey[i]  = _heapKey[child];
            i = child;
        }
        _heapNode[i] = node;
        _heapKey[i]  = key;
        return top;
    }
}
