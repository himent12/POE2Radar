using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Single entry point for route planning. Given the live terrain grid and a start/goal in grid cells,
/// returns a short list of clean waypoints, or an empty list if no path exists.
///
/// <para>Pipeline: <see cref="NavGrid"/> (cached per terrain: clearance field + connected regions) →
/// snap start to the nearest walkable cell, snap goal to the nearest walkable cell IN THE START'S REGION
/// (unreachable goals are rejected in O(1), never searched) → clearance-weighted A* → thick-LOS string
/// pulling + corner relaxation (<see cref="PathSmoother"/>).</para>
///
/// <para>Owns and reuses a single <see cref="AStar"/> instance sized to the grid; rebuilt only when the grid
/// dimensions change. Not thread-safe — one caller at a time (the background replanner's worker).</para>
/// </summary>
public sealed class PathPlanner
{
    /// <summary>Snap radius (cells) for a start that is not on walkable ground (mid-transition / bad read).</summary>
    public const int StartSnapRadius = 12;
    /// <summary>Snap radius (cells) for a goal off walkable ground or in another region (mob on a ledge, a
    /// landmark centroid inside a wall).</summary>
    public const int GoalSnapRadius = 48;
    /// <summary>Preferred clearance for smoothed segments: 2 cells off any wall.</summary>
    public const int PreferredClearanceRaw = 2 * NavGrid.RawPerCell;
    /// <summary>Minimum clearance (merely walkable) used only where the preferred one cannot advance.</summary>
    public const int MinClearanceRaw = NavGrid.RawPerCell;

    private AStar? _astar;
    private int _gridWidth;
    private int _gridHeight;

    /// <summary>Diagnostics from the last <see cref="Plan"/>: A* expansions (0 when rejected before search).</summary>
    public int LastExpanded { get; private set; }

    public IReadOnlyList<(int x, int y)> Plan(
        Poe2Live.TerrainData terrain, (int x, int y) start, (int x, int y) goal,
        int maxNodes = int.MaxValue)
    {
        LastExpanded = 0;
        if (terrain.Width <= 0 || terrain.Height <= 0) return Array.Empty<(int, int)>();

        var g = NavGrid.For(terrain);
        if (_astar is null || _gridWidth != terrain.Width || _gridHeight != terrain.Height)
        {
            _astar = new AStar(terrain.Width, terrain.Height);
            _gridWidth = terrain.Width;
            _gridHeight = terrain.Height;
        }

        var ox = Math.Clamp(start.x, 0, g.Width - 1);
        var oy = Math.Clamp(start.y, 0, g.Height - 1);
        var gx = Math.Clamp(goal.x, 0, g.Width - 1);
        var gy = Math.Clamp(goal.y, 0, g.Height - 1);

        if (!SnapStart(g, ox, oy, gx, gy, out var sx, out var sy)) return Array.Empty<(int, int)>();
        var region = g.RegionAt(sx, sy);
        if (!g.TryNearestWalkable(gx, gy, GoalSnapRadius, region, out gx, out gy)) return Array.Empty<(int, int)>();

        var path = _astar.FindPath(g, new PathCell(sx, sy), new PathCell(gx, gy), maxNodes);
        LastExpanded = _astar.LastExpanded;
        if (!path.Found || path.Cells.Count == 0) return Array.Empty<(int, int)>();

        var smoothed = PathSmoother.Smooth(g, path.Cells, PreferredClearanceRaw, MinClearanceRaw);
        // The player was off walkable ground: the route begins where he actually STANDS, with a first leg onto
        // the snapped cell — so the line is anchored to him, the tracker's cursor starts at his position, and
        // movement steers out of the doorway instead of toward a route that starts three cells away.
        var lead = sx != ox || sy != oy ? 1 : 0;
        var result = new (int x, int y)[smoothed.Count + lead];
        if (lead == 1) result[0] = (ox, oy);
        for (var i = 0; i < smoothed.Count; i++) result[i + lead] = (smoothed[i].X, smoothed[i].Y);
        return result;
    }

    /// <summary>Goal's own region (nearest walkable within a few cells, 0 if none) drives the start snap: a
    /// player standing on blocked ground is anchored into the region he can reach the goal from.</summary>
    private static bool SnapStart(NavGrid g, int x, int y, int gx, int gy, out int sx, out int sy)
    {
        var goalRegion = g.TryNearestWalkable(gx, gy, GoalProbeRadius, 0, out var px, out var py) ? g.RegionAt(px, py) : 0;
        return g.TrySnapStart(x, y, StartSnapRadius, goalRegion, out sx, out sy);
    }

    /// <summary>How far around the raw goal to look for its region when choosing the start's region.</summary>
    public const int GoalProbeRadius = 6;

    /// <summary>
    /// O(1) reachability: is there walkable ground within <paramref name="snapRadius"/> of <paramref name="goal"/>
    /// in the same connected region as the ground under (or nearest to) <paramref name="start"/>? Safe from any
    /// thread once the grid is built.
    /// </summary>
    public static bool IsReachable(Poe2Live.TerrainData terrain, (int x, int y) start, (int x, int y) goal, int snapRadius = 4)
    {
        if (terrain.Width <= 0 || terrain.Height <= 0) return false;
        var g = NavGrid.For(terrain);
        var gx = Math.Clamp(goal.x, 0, g.Width - 1);
        var gy = Math.Clamp(goal.y, 0, g.Height - 1);
        if (!SnapStart(g, Math.Clamp(start.x, 0, g.Width - 1), Math.Clamp(start.y, 0, g.Height - 1), gx, gy, out var sx, out var sy)) return false;
        return g.TryNearestWalkable(gx, gy, snapRadius, g.RegionAt(sx, sy), out _, out _);
    }
}
