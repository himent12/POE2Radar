using System.Diagnostics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Planner quality: corridor-centred routes, wide corners, no diagonal squeezing, corridor smoothing,
/// O(1) unreachable rejection, goal snapping into the reachable region, segment safety, and speed.</summary>
public sealed class PathPlannerTests
{
    private static Poe2Live.TerrainData Grid(int w, int h, Func<int, int, bool> walkable)
    {
        var a = new byte[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) a[y * w + x] = walkable(x, y) ? (byte)1 : (byte)0;
        return new Poe2Live.TerrainData(a, w, h);
    }

    [Fact]
    public void Clearance_field_measures_wall_distance()
    {
        var t = Grid(9, 9, (x, y) => x >= 1 && x <= 7 && y >= 1 && y <= 7);
        var g = NavGrid.Build(t);
        Assert.Equal(0, g.ClearanceRaw(0, 4));
        Assert.Equal(1f, g.ClearanceCells(1, 4));
        Assert.Equal(2f, g.ClearanceCells(2, 4));
        Assert.Equal(4f, g.ClearanceCells(4, 4));
        Assert.Equal(1f, g.ClearanceCells(7, 7)); // map edge counts as wall
    }

    [Fact]
    public void Regions_label_connected_components()
    {
        var t = Grid(10, 10, (x, y) => x != 5); // a full-height wall at x=5
        var g = NavGrid.Build(t);
        Assert.Equal(2, g.RegionCount);
        Assert.True(g.SameRegion(1, 1, 4, 8));
        Assert.False(g.SameRegion(1, 1, 6, 1));
        Assert.Equal(0, g.RegionAt(5, 5));
    }

    [Fact]
    public void Route_runs_down_the_corridor_centre()
    {
        // 5-wide corridor y=2..6 across a 40-wide map; start/goal on the top wall-hugging row.
        var t = Grid(40, 9, (x, y) => y >= 2 && y <= 6);
        var pts = new PathPlanner().Plan(t, (2, 2), (37, 2));
        Assert.NotEmpty(pts);
        // Not a single wall-hugging chord: the route must leave the wall row and come back.
        Assert.True(pts.Count >= 4, $"route collapsed to {string.Join(" ", pts)}");
        // The dominant (longest) segment lies on the centre row.
        var longest = Enumerable.Range(0, pts.Count - 1)
            .MaxBy(i => Math.Abs(pts[i + 1].x - pts[i].x) + Math.Abs(pts[i + 1].y - pts[i].y));
        Assert.True(pts[longest].y == 4 && pts[longest + 1].y == 4,
            $"longest segment {pts[longest]}→{pts[longest + 1]} is off-centre in {string.Join(" ", pts)}");
        // Every interior waypoint keeps ≥ 2 cells of clearance (rows 3..5); none on the wall rows.
        var g = NavGrid.Build(t);
        foreach (var (x, y) in pts.Skip(1).Take(pts.Count - 2))
            Assert.True(g.ClearanceCells(x, y) >= 2f, $"waypoint ({x},{y}) hugs the wall");
        // And every segment interior stays ≥ 2 cells off the walls (thick LOS), endpoints excepted.
        for (var i = 0; i + 1 < pts.Count; i++)
            Assert.True(g.HasClearLine(pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y, 2 * NavGrid.RawPerCell, NavGrid.RawPerCell),
                $"segment {pts[i]}→{pts[i + 1]} grazes a wall");
    }

    [Fact]
    public void Door_is_bridged_then_route_returns_to_centre()
    {
        // Two 7-wide halls (y=1..7) joined by a 1-wide, 6-long door at y=4, x=20..25. Start/goal hug the top
        // wall on either side. The relaxed pull may only bridge the door, never chord a hall along the wall.
        var t = Grid(50, 9, (x, y) => (x < 20 || x > 25) ? (y >= 1 && y <= 7) : y == 4);
        var pts = new PathPlanner().Plan(t, (2, 1), (47, 1));
        var g = NavGrid.Build(t);
        Assert.NotEmpty(pts);
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var (ax, ay) = pts[i];
            var (bx, by) = pts[i + 1];
            var inDoor = Math.Min(ax, bx) <= 26 && Math.Max(ax, bx) >= 19; // segment overlaps the door span
            if (inDoor) continue; // the door leg is allowed to be thin
            Assert.True(g.HasClearLine(ax, ay, bx, by, 2 * NavGrid.RawPerCell, NavGrid.RawPerCell),
                $"hall segment ({ax},{ay})→({bx},{by}) hugs the wall in {string.Join(" ", pts)}");
        }
        // Both halls are actually crossed near their centre row, not along y=1.
        Assert.Contains(pts, p => p.x < 19 && Math.Abs(p.y - 4) <= 1);
        Assert.Contains(pts, p => p.x > 26 && Math.Abs(p.y - 4) <= 1);
    }

    [Fact]
    public void Wall_hugging_start_and_goal_do_not_collapse_to_a_chord()
    {
        // Open room; start and goal both on the left wall column. A naive "thin fallback" would emit one
        // vertical wall-hugging chord; the route must bow out into the room.
        var t = Grid(30, 60, (_, _) => true);
        var pts = new PathPlanner().Plan(t, (0, 2), (0, 57));
        Assert.True(pts.Count >= 3, $"route collapsed to {string.Join(" ", pts)}");
        Assert.Contains(pts.Skip(1).Take(pts.Count - 2), p => p.x >= 2);
    }

    [Fact]
    public void No_diagonal_squeeze_between_wall_corners()
    {
        // Two rooms joined only by a diagonal gap: (4,4) and (5,5) walkable, (5,4) and (4,5) walls.
        var t = Grid(10, 10, (x, y) => (x <= 4 && y <= 4) || (x >= 5 && y >= 5));
        var g = NavGrid.Build(t);
        Assert.False(g.SameRegion(1, 1, 8, 8));
        // The goal is unreachable; the planner routes to the nearest cell on OUR side and never crosses.
        var pts = new PathPlanner().Plan(t, (1, 1), (8, 8));
        Assert.NotEmpty(pts);
        Assert.All(pts, p => Assert.True(p.x <= 4 && p.y <= 4, $"({p.x},{p.y}) squeezed through the corner"));
    }

    [Fact]
    public void Unreachable_goal_is_rejected_without_searching()
    {
        var t = Grid(400, 400, (x, y) => x != 200);
        var planner = new PathPlanner();
        var pts = planner.Plan(t, (10, 10), (390, 390));
        Assert.Empty(pts);
        Assert.Equal(0, planner.LastExpanded);
    }

    [Fact]
    public void Goal_inside_a_wall_snaps_to_reachable_ground()
    {
        // Pillar at x=10..12,y=10..12; goal in its middle → snapped to the pillar's edge, still reachable.
        var t = Grid(30, 30, (x, y) => !(x >= 10 && x <= 12 && y >= 10 && y <= 12));
        var pts = new PathPlanner().Plan(t, (2, 2), (11, 11));
        Assert.NotEmpty(pts);
        var (ex, ey) = pts[^1];
        Assert.True(Math.Abs(ex - 11) <= 2 && Math.Abs(ey - 11) <= 2, $"end ({ex},{ey}) not next to the pillar");
        Assert.True(t.Walkable[ey * 30 + ex] != 0);
    }

    [Fact]
    public void Goal_across_a_gap_snaps_to_the_near_side()
    {
        // Chasm at x=15..16; goal on the far side within snap range → route ends at the near edge.
        var t = Grid(40, 20, (x, y) => x < 15 || x > 16);
        var pts = new PathPlanner().Plan(t, (2, 10), (20, 10));
        Assert.NotEmpty(pts);
        Assert.True(pts[^1].x <= 14, $"end ({pts[^1].x},{pts[^1].y}) crossed the chasm");
    }

    [Fact]
    public void Player_in_a_doorway_plans_from_the_hall_not_the_pocket()
    {
        // Hall (y ≥ 12) below a wall band (y 6..11). Inside the band a 3-cell micro pocket at (20..22, 9) and
        // the player standing ON the wall at (21, 11) — closer to the pocket (2 cells) than to the hall (1 cell
        // below is walkable at y=12, but say the threshold row y=12..13 under him is blocked too).
        var t = Grid(40, 40, (x, y) =>
            y >= 14 || (y == 9 && x >= 20 && x <= 22));
        var g = NavGrid.Build(t);
        Assert.True(g.RegionSize[g.RegionAt(21, 9)] < NavGrid.MicroRegionCells);
        var pts = new PathPlanner().Plan(t, (21, 11), (30, 30));
        Assert.NotEmpty(pts);
        // Route is anchored on the player himself, then steps onto hall ground — never into the pocket.
        Assert.Equal((21, 11), pts[0]);
        Assert.True(pts[1].y >= 14, $"snapped start {pts[1]} is not in the hall (route {string.Join(" ", pts)})");
        Assert.Equal((30, 30), pts[^1]);
        Assert.All(pts.Skip(1), p => Assert.True(t.Walkable[p.y * 40 + p.x] != 0));
    }

    [Fact]
    public void Start_off_grid_is_reachable_and_prefers_goal_region()
    {
        // Two halls split by a wall at x=20; player stands IN the wall at (20, 10). Nearest ground is equidistant
        // on both sides → the side holding the goal wins, so the route never starts in the wrong hall.
        var t = Grid(40, 20, (x, y) => x != 20);
        Assert.True(PathPlanner.IsReachable(t, (20, 10), (35, 10)));
        var right = new PathPlanner().Plan(t, (20, 10), (35, 10));
        Assert.True(right.Count >= 2 && right[1].x == 21, $"route {string.Join(" ", right)}");
        var left = new PathPlanner().Plan(t, (20, 10), (3, 10));
        Assert.True(left.Count >= 2 && left[1].x == 19, $"route {string.Join(" ", left)}");
    }

    [Fact]
    public void One_wide_corridor_still_smooths()
    {
        var t = Grid(30, 5, (x, y) => y == 2);
        var pts = new PathPlanner().Plan(t, (1, 2), (28, 2));
        Assert.NotEmpty(pts);
        Assert.True(pts.Count <= 3, $"corridor produced {pts.Count} waypoints");
    }

    [Fact]
    public void Corner_is_taken_wide_not_scraped()
    {
        // L-shaped 5-wide corridor: no interior waypoint within 1 cell of a wall.
        var t = Grid(30, 30, (x, y) => (y >= 2 && y <= 6 && x <= 8) || (x >= 4 && x <= 8 && y >= 2));
        var pts = new PathPlanner().Plan(t, (1, 4), (6, 27));
        var g = NavGrid.Build(t);
        Assert.NotEmpty(pts);
        foreach (var (x, y) in pts.Skip(1).Take(pts.Count - 2))
            Assert.True(g.ClearanceCells(x, y) >= 2f, $"waypoint ({x},{y}) scrapes a wall");
    }

    [Fact]
    public void Every_segment_is_walkable_end_to_end()
    {
        // Pseudo-random pillars; every consecutive waypoint pair must have an unbroken walkable line.
        var rng = new Random(7);
        var blocked = new HashSet<(int, int)>();
        for (var i = 0; i < 120; i++)
        {
            var px = rng.Next(5, 115); var py = rng.Next(5, 115);
            for (var dy = 0; dy < 3; dy++) for (var dx = 0; dx < 3; dx++) blocked.Add((px + dx, py + dy));
        }
        var t = Grid(120, 120, (x, y) => !blocked.Contains((x, y)));
        var g = NavGrid.Build(t);
        var pts = new PathPlanner().Plan(t, (1, 1), (118, 118));
        Assert.NotEmpty(pts);
        Assert.Equal((1, 1), pts[0]);
        Assert.Equal((118, 118), pts[^1]);
        for (var i = 0; i + 1 < pts.Count; i++)
            Assert.True(g.HasClearLine(pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y, NavGrid.RawPerCell),
                $"segment {pts[i]}→{pts[i + 1]} crosses a wall");
    }

    [Fact]
    public void Open_field_route_is_near_straight()
    {
        var t = Grid(200, 200, (_, _) => true);
        var pts = new PathPlanner().Plan(t, (5, 5), (194, 150));
        Assert.True(pts.Count <= 3, $"open field produced {pts.Count} waypoints");
    }

    [Fact]
    public void Large_map_plans_fast()
    {
        // 1500×1500 maze of horizontal walls with staggered gaps — worst case for A* fan-out.
        const int n = 1500;
        var t = Grid(n, n, (x, y) => y % 12 != 0 || (x / 40 + y / 12) % 3 == 0 && x % 40 < 6);
        var planner = new PathPlanner();
        planner.Plan(t, (3, 3), (n - 4, n - 4)); // warm: builds the NavGrid + A* buffers
        var sw = Stopwatch.StartNew();
        var pts = planner.Plan(t, (3, 3), (n - 4, n - 4));
        sw.Stop();
        Assert.NotEmpty(pts);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"plan took {sw.ElapsedMilliseconds} ms ({planner.LastExpanded} expansions)");
    }
}
