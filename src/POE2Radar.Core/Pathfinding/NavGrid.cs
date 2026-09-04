using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Preprocessed navigation grid built ONCE per terrain (cached by reference in <see cref="For"/>) and shared,
/// read-only, by every consumer — the A* worker, the tick thread's reachability checks and the smoother.
///
/// <para>Three layers over the binary walkable bitmap:</para>
/// <list type="bullet">
/// <item><see cref="Clearance"/> — chamfer (3-4) distance transform to the nearest blocked cell / map edge,
/// in thirds of a cell (<c>raw / 3 = cells</c>). A wall-touching cell is 3, one cell further is 6, … capped at
/// 255. A* turns it into a smooth wall-hugging penalty (<see cref="StepMultiplier"/>) so routes run through
/// corridor centres and swing wide around corners; the smoother uses it as a "thick line" test.</item>
/// <item><see cref="Region"/> — 4-connected component label per walkable cell (0 = blocked). Two cells with
/// different labels are provably unreachable from each other, so a hopeless search is rejected in O(1)
/// instead of flooding the map to the node budget; goal snapping picks a cell in the START's region.</item>
/// <item><see cref="StepMultiplier"/> — per-raw-clearance cost multiplier table (1.0 in the open).</item>
/// </list>
/// </summary>
public sealed class NavGrid
{
    /// <summary>Raw clearance units per cell (chamfer cardinal weight).</summary>
    public const int RawPerCell = 3;
    private const int Cardinal = 3, Diagonal = 4, Cap = 255;

    /// <summary>Clearance (cells) at and beyond which there is no wall penalty at all.</summary>
    public const float SafeClearanceCells = 3.5f;
    /// <summary>Penalty at zero clearance; the multiplier is <c>1 + PenaltyScale × ((safe − d) / safe)²</c>.</summary>
    public const float PenaltyScale = 2.5f;

    public byte[] Walkable { get; }
    public byte[] Clearance { get; }
    public int[] Region { get; }
    public int Width { get; }
    public int Height { get; }
    public int RegionCount { get; }
    /// <summary>Number of walkable cells per region label (index 0 = blocked count, unused).</summary>
    public int[] RegionSize { get; }

    /// <summary>Cost multiplier for stepping INTO a cell with a given raw clearance (index = raw value).</summary>
    public static readonly float[] StepMultiplier = BuildMultipliers();

    private NavGrid(byte[] walkable, byte[] clearance, int[] region, int[] regionSize, int w, int h)
    {
        Walkable = walkable;
        Clearance = clearance;
        Region = region;
        RegionSize = regionSize;
        RegionCount = regionSize.Length - 1;
        Width = w;
        Height = h;
    }

    // ── Cache: one entry, keyed by TerrainData reference (immutable per area). ──
    private static readonly object CacheGate = new();
    private static Poe2Live.TerrainData? _cacheKey;
    private static NavGrid? _cacheGrid;

    /// <summary>Shared grid for <paramref name="terrain"/> — built on first request, then reused by every
    /// thread. Safe to call from any thread; the build is serialised.</summary>
    public static NavGrid For(Poe2Live.TerrainData terrain)
    {
        lock (CacheGate)
        {
            if (ReferenceEquals(_cacheKey, terrain) && _cacheGrid is not null) return _cacheGrid;
            var g = Build(terrain.Walkable, terrain.Width, terrain.Height);
            _cacheKey = terrain;
            _cacheGrid = g;
            return g;
        }
    }

    public static NavGrid Build(Poe2Live.TerrainData t) => Build(t.Walkable, t.Width, t.Height);

    public static NavGrid Build(byte[] walkable, int w, int h)
    {
        if (w <= 0 || h <= 0 || walkable.Length < w * h)
            throw new ArgumentException($"walkable grid {walkable.Length} < {w}x{h}");
        var clearance = DistanceTransform(walkable, w, h);
        var region = new int[w * h];
        var sizes = LabelRegions(walkable, w, h, region);
        return new NavGrid(walkable, clearance, region, sizes, w, h);
    }

    // ── Queries ─────────────────────────────────────────────────────────────────────────────

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public int Index(int x, int y) => y * Width + x;
    public bool IsWalkable(int x, int y) => InBounds(x, y) && Walkable[y * Width + x] != 0;
    /// <summary>Raw clearance (thirds of a cell); 0 off-map or on a blocked cell.</summary>
    public int ClearanceRaw(int x, int y) => InBounds(x, y) ? Clearance[y * Width + x] : 0;
    /// <summary>Clearance in cells (1.0 = touching a wall).</summary>
    public float ClearanceCells(int x, int y) => ClearanceRaw(x, y) / (float)RawPerCell;
    public int RegionAt(int x, int y) => InBounds(x, y) ? Region[y * Width + x] : 0;

    /// <summary>Same connected component (both walkable)? Out-of-bounds / blocked → false.</summary>
    public bool SameRegion(int ax, int ay, int bx, int by)
    {
        var ra = RegionAt(ax, ay);
        return ra != 0 && ra == RegionAt(bx, by);
    }

    /// <summary>
    /// Nearest walkable cell to (x, y) by Chebyshev ring then Euclidean distance within the ring, optionally
    /// restricted to <paramref name="region"/> (0 = any). Returns the cell itself when it already qualifies.
    /// </summary>
    public bool TryNearestWalkable(int x, int y, int maxRadius, int region, out int nx, out int ny)
    {
        nx = x; ny = y;
        if (Qualifies(x, y, region)) return true;
        for (var r = 1; r <= maxRadius; r++)
        {
            var bestD = int.MaxValue;
            for (var dy = -r; dy <= r; dy++)
            {
                var yy = y + dy;
                if ((uint)yy >= (uint)Height) continue;
                var edge = Math.Abs(dy) == r;
                var step = edge ? 1 : 2 * r;
                for (var dx = -r; dx <= r; dx += step)
                {
                    var xx = x + dx;
                    if ((uint)xx >= (uint)Width) continue;
                    if (!Qualifies(xx, yy, region)) continue;
                    var d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; nx = xx; ny = yy; }
                }
            }
            if (bestD != int.MaxValue) return true;
        }
        return false;
    }

    private bool Qualifies(int x, int y, int region)
    {
        if (!InBounds(x, y)) return false;
        var r = Region[y * Width + x];
        return r != 0 && (region == 0 || r == region);
    }

    /// <summary>Regions smaller than this are collision noise (the sliver between two door panels, a cell
    /// inside a crate cluster) — never worth anchoring a route in when real ground is within reach.</summary>
    public const int MicroRegionCells = 32;
    /// <summary>How many rings past the FIRST walkable cell the start snap keeps looking for a better-ranked
    /// region — a preferred region 3 cells further away wins; one 10 cells away does not.</summary>
    public const int SnapPreferenceSlack = 3;

    /// <summary>
    /// Snap a START (the player, who may be standing on ground the grid calls blocked — a door threshold, a
    /// ledge lip) to the walkable cell it should plan from. Scans rings up to <paramref name="maxRadius"/>;
    /// candidates rank: in <paramref name="preferRegion"/> (the goal's region) &gt; in a normal-sized region &gt;
    /// in a micro region, then nearest. So a player wedged in a doorway plans from the hall he is entering, not
    /// from a 3-cell pocket between the panels.
    /// </summary>
    public bool TrySnapStart(int x, int y, int maxRadius, int preferRegion, out int nx, out int ny)
    {
        nx = x; ny = y;
        if (Qualifies(x, y, 0)) return true; // standing on real ground: plan from exactly there, always
        var bestTier = -1;
        var bestD = int.MaxValue;
        var firstRing = int.MaxValue;
        for (var r = 0; r <= maxRadius && r - SnapPreferenceSlack <= firstRing; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                var yy = y + dy;
                if ((uint)yy >= (uint)Height) continue;
                var edge = Math.Abs(dy) == r;
                var step = edge || r == 0 ? 1 : 2 * r;
                for (var dx = -r; dx <= r; dx += step)
                {
                    var xx = x + dx;
                    if ((uint)xx >= (uint)Width) continue;
                    var reg = Region[yy * Width + xx];
                    if (reg == 0) continue;
                    var tier = reg == preferRegion ? 2 : RegionSize[reg] >= MicroRegionCells ? 1 : 0;
                    var d = dx * dx + dy * dy;
                    if (r < firstRing) firstRing = r;
                    if (tier > bestTier || (tier == bestTier && d < bestD)) { bestTier = tier; bestD = d; nx = xx; ny = yy; }
                }
            }
            // Best possible tier found on this ring (or earlier) → later rings are only farther.
            if (bestTier == 2 || (bestTier == 1 && preferRegion == 0)) return true;
        }
        return bestTier >= 0;
    }

    /// <summary>
    /// Thick line-of-sight: every cell the segment (a → b) passes through (supercover — corner crossings
    /// visit BOTH side cells, so nothing squeezes diagonally between two wall corners) must have raw
    /// clearance ≥ <paramref name="minRaw"/>. <c>minRaw = RawPerCell</c> means "merely walkable"; larger
    /// values keep the line that many thirds-of-a-cell away from every wall. The two endpoints only need
    /// <paramref name="endpointMinRaw"/> (default: same as <paramref name="minRaw"/>) — a route may START or
    /// END against a wall (that is where the player / target is) while its interior stays clear.
    /// </summary>
    public bool HasClearLine(int ax, int ay, int bx, int by, int minRaw, int endpointMinRaw = -1)
    {
        if (endpointMinRaw < 0) endpointMinRaw = minRaw;
        if (ClearanceRaw(ax, ay) < endpointMinRaw || ClearanceRaw(bx, by) < endpointMinRaw) return false;
        int x = ax, y = ay;
        var dx = Math.Abs(bx - ax);
        var dy = -Math.Abs(by - ay);
        var sx = ax < bx ? 1 : -1;
        var sy = ay < by ? 1 : -1;
        var err = dx + dy;
        while (x != bx || y != by)
        {
            var e2 = 2 * err;
            var stepX = e2 >= dy;
            var stepY = e2 <= dx;
            // A step that moves in both axes crosses a cell corner: both orthogonal side cells must be clear
            // too (same no-corner-cutting rule A* uses), so the line can never thread a diagonal gap.
            if (stepX && stepY)
            {
                // The corner cells beside the first / last step share the endpoint's wall, so they are held to
                // the endpoint standard (walkable, no corner cut) rather than the interior clearance.
                var atEnd = (x == ax && y == ay) || (x + sx == bx && y + sy == by);
                var side = atEnd ? endpointMinRaw : minRaw;
                if (ClearanceRaw(x + sx, y) < side || ClearanceRaw(x, y + sy) < side) return false;
            }
            if (stepX) { err += dy; x += sx; }
            if (stepY) { err += dx; y += sy; }
            if (x == bx && y == by) return true;          // endpoint already vetted above
            if (ClearanceRaw(x, y) < minRaw) return false;
        }
        return true;
    }

    // ── Builders ────────────────────────────────────────────────────────────────────────────

    /// <summary>Two-pass 3-4 chamfer distance to the nearest blocked cell or map edge (raw thirds-of-a-cell).</summary>
    private static byte[] DistanceTransform(byte[] walkable, int w, int h)
    {
        var d = new int[w * h];
        for (var i = 0; i < w * h; i++) d[i] = walkable[i] == 0 ? 0 : Cap;

        // Off-map reads as 0 (a wall).
        int At(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? d[y * w + x] : 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (d[i] == 0) continue;
                var v = d[i];
                v = Math.Min(v, At(x - 1, y) + Cardinal);
                v = Math.Min(v, At(x, y - 1) + Cardinal);
                v = Math.Min(v, At(x - 1, y - 1) + Diagonal);
                v = Math.Min(v, At(x + 1, y - 1) + Diagonal);
                d[i] = v;
            }
        }
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                if (d[i] == 0) continue;
                var v = d[i];
                v = Math.Min(v, At(x + 1, y) + Cardinal);
                v = Math.Min(v, At(x, y + 1) + Cardinal);
                v = Math.Min(v, At(x + 1, y + 1) + Diagonal);
                v = Math.Min(v, At(x - 1, y + 1) + Diagonal);
                d[i] = v;
            }
        }
        var outp = new byte[w * h];
        for (var i = 0; i < outp.Length; i++) outp[i] = (byte)Math.Min(Cap, d[i]);
        return outp;
    }

    /// <summary>4-connected component labelling (iterative scanline flood fill). Returns per-label sizes.</summary>
    private static int[] LabelRegions(byte[] walkable, int w, int h, int[] region)
    {
        var sizes = new List<int> { 0 };
        var stack = new Stack<int>();
        var label = 0;
        for (var i = 0; i < w * h; i++)
        {
            if (walkable[i] == 0 || region[i] != 0) continue;
            label++;
            var size = 0;
            region[i] = label;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                size++;
                var x = idx % w;
                var y = idx / w;
                if (x > 0)     Visit(idx - 1);
                if (x < w - 1) Visit(idx + 1);
                if (y > 0)     Visit(idx - w);
                if (y < h - 1) Visit(idx + w);
            }
            sizes.Add(size);
        }
        return sizes.ToArray();

        void Visit(int j)
        {
            if (walkable[j] == 0 || region[j] != 0) return;
            region[j] = label;
            stack.Push(j);
        }
    }

    private static float[] BuildMultipliers()
    {
        var t = new float[256];
        for (var raw = 0; raw < 256; raw++)
        {
            var cells = raw / (float)RawPerCell;
            var shortfall = MathF.Max(0f, (SafeClearanceCells - cells) / SafeClearanceCells);
            t[raw] = 1f + PenaltyScale * shortfall * shortfall;
        }
        return t;
    }
}
