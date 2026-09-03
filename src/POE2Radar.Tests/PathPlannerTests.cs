using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Planner quality: corridor-centred routes, wide corners, no diagonal squeezing, corridor smoothing.</summary>
public sealed class PathPlannerTests
{
    private static Poe2Live.TerrainData Grid(int w, int h, Func<int, int, bool> walkable)
    {
        var a = new byte[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) a[y * w + x] = walkable(x, y) ? (byte)1 : (byte)0;
        return new Poe2Live.TerrainData(a, w, h);
    }

    [Fact]
    public void Clearance_grid_grades_cells_by_wall_distance()
    {
        var t = Grid(9, 9, (x, y) => x >= 1 && x <= 7 && y >= 1 && y <= 7);
        var c = ClearanceGrid.Build(t);
        Assert.Equal(0, c.Read(0, 4));
        Assert.Equal(ClearanceGrid.Hug, c.Read(1, 4));
        Assert.Equal(ClearanceGrid.Near, c.Read(2, 4));
        Assert.Equal(ClearanceGrid.Open, c.Read(4, 4));
    }

    [Fact]
    public void Route_runs_down_the_corridor_centre()
    {
        // 5-wide corridor y=2..6 across a 40-wide map; start/goal on the top wall-hugging row.
        var t = Grid(40, 9, (x, y) => y >= 2 && y <= 6);
        var pts = new PathPlanner().Plan(t, (2, 2), (37, 2));
        Assert.NotEmpty(pts);
        // Every interior waypoint should sit on the centre row (y=4), not hug y=2.
        foreach (var (x, y) in pts.Skip(1).Take(pts.Count - 2))
            Assert.True(y == 4, $"waypoint ({x},{y}) hugs the wall");
    }

    [Fact]
    public void No_diagonal_squeeze_between_wall_corners()
    {
        // Two rooms joined only by a diagonal gap: (4,4) and (5,5) walkable, (5,4) and (4,5) walls.
        var t = Grid(10, 10, (x, y) =>
            (x <= 4 && y <= 4) || (x >= 5 && y >= 5));
        var pts = new PathPlanner().Plan(t, (1, 1), (8, 8));
        Assert.Empty(pts); // unreachable without cutting the corner
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
        // L-shaped 5-wide corridor. The inner corner cell is (5,5)-ish; a scraping path passes within 1 cell
        // of it. Check that no waypoint touches a Hug cell except the endpoints.
        var t = Grid(30, 30, (x, y) => (y >= 2 && y <= 6 && x <= 8) || (x >= 4 && x <= 8 && y >= 2));
        var pts = new PathPlanner().Plan(t, (1, 4), (6, 27));
        var c = ClearanceGrid.Build(t);
        Assert.NotEmpty(pts);
        foreach (var (x, y) in pts.Skip(1).Take(pts.Count - 2))
            Assert.True(c.Read(x, y) >= ClearanceGrid.Near, $"waypoint ({x},{y}) scrapes a wall");
    }
}
