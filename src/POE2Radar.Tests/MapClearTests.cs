using POE2Radar.Overlay.Navigation;
using NumVec2 = System.Numerics.Vector2;
using Xunit;

namespace POE2Radar.Tests;

public sealed class MapClearTests
{
    private static byte[] FullWalkable(int w, int h)
    {
        var g = new byte[w * h];
        Array.Fill(g, (byte)1);
        return g;
    }

    private static HashSet<long> VisitedDisc(byte[] walk, int w, int h, int px, int py, int r)
    {
        var set = new HashSet<long>();
        MapClear.StampVisited(set, walk, w, h, new NumVec2(px, py), r);
        return set;
    }

    [Fact]
    public void Town_returns_none()
    {
        var walk = FullWalkable(8, 8);
        var visited = VisitedDisc(walk, 8, 8, 2, 2, 1);
        var id = MapClear.PickTarget("G1_town", new NumVec2(2, 2), walk, 8, 8, visited, [], null);
        Assert.Null(id);
    }

    [Fact]
    public void Unique_beats_nearby_hostile()
    {
        var walk = FullWalkable(32, 32);
        var visited = VisitedDisc(walk, 32, 32, 0, 0, 2);
        var mobs = new MapClear.MobHint[]
        {
            new("e:1", new NumVec2(1, 0), Unique: false),
            new("e:9", new NumVec2(20, 20), Unique: true),
        };
        var id = MapClear.PickTarget("G1_2", NumVec2.Zero, walk, 32, 32, visited, mobs, null);
        Assert.Equal("e:9", id);
    }

    [Fact]
    public void Nearest_unique_when_two_bosses()
    {
        var walk = FullWalkable(32, 32);
        var visited = VisitedDisc(walk, 32, 32, 0, 0, 1);
        var mobs = new MapClear.MobHint[]
        {
            new("e:far", new NumVec2(30, 30), Unique: true),
            new("e:near", new NumVec2(4, 0), Unique: true),
        };
        var id = MapClear.PickTarget("G1_2", NumVec2.Zero, walk, 32, 32, visited, mobs, "c:8,8");
        Assert.Equal("e:near", id);
    }

    [Fact]
    public void Hostile_beats_frontier()
    {
        var walk = FullWalkable(16, 16);
        var visited = VisitedDisc(walk, 16, 16, 2, 2, 1);
        var mobs = new MapClear.MobHint[]
        {
            new("e:7", new NumVec2(10, 10), Unique: false),
        };
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 16, 16, visited, mobs, "c:4,2");
        Assert.Equal("e:7", id);
    }

    [Fact]
    public void Frontier_is_nearest_unvisited_neighbor()
    {
        var walk = FullWalkable(8, 8);
        var visited = VisitedDisc(walk, 8, 8, 2, 2, 1);
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 8, 8, visited, [], null);
        Assert.NotNull(id);
        Assert.True(MapClear.TryParseCell(id, out var x, out var y));
        Assert.DoesNotContain(MapClear.Key(x, y), visited);
        Assert.Equal(1, walk[y * 8 + x]);
        // Radius-1 disc around (2,2) covers cardinal neighbors; nearest frontier is a
        // diagonal-adjacent cell at chebyshev 2 / euclidean ~1.41 (e.g. (1,3)).
        var d = (x - 2) * (x - 2) + (y - 2) * (y - 2);
        Assert.True(d >= 2 && d <= 5, $"frontier ({x},{y}) dist²={d}");
    }

    [Fact]
    public void Keeps_current_cell_while_unvisited()
    {
        var walk = FullWalkable(16, 16);
        var visited = VisitedDisc(walk, 16, 16, 2, 2, 1);
        const string hold = "c:10,10";
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 16, 16, visited, [], hold);
        Assert.Equal(hold, id);
    }

    [Fact]
    public void Drops_current_cell_once_visited()
    {
        var walk = FullWalkable(16, 16);
        var visited = VisitedDisc(walk, 16, 16, 2, 2, 1);
        visited.Add(MapClear.Key(10, 10));
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 16, 16, visited, [], "c:10,10");
        Assert.NotEqual("c:10,10", id);
        Assert.NotNull(id);
    }

    [Fact]
    public void Fully_visited_returns_none()
    {
        var walk = FullWalkable(4, 4);
        var visited = new HashSet<long>();
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                visited.Add(MapClear.Key(x, y));
        var id = MapClear.PickTarget("G1_2", new NumVec2(1, 1), walk, 4, 4, visited, [], null);
        Assert.Null(id);
    }

    [Fact]
    public void Empty_visited_without_mobs_returns_none()
    {
        var walk = FullWalkable(8, 8);
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 8, 8, new HashSet<long>(), [], null);
        Assert.Null(id);
    }

    [Fact]
    public void Stamp_marks_walkable_disc_only()
    {
        var walk = FullWalkable(8, 8);
        walk[2 * 8 + 3] = 0; // wall at (3,2)
        var visited = new HashSet<long>();
        MapClear.StampVisited(visited, walk, 8, 8, new NumVec2(2, 2), 1);
        Assert.Contains(MapClear.Key(2, 2), visited);
        Assert.Contains(MapClear.Key(2, 1), visited);
        Assert.DoesNotContain(MapClear.Key(3, 2), visited);
        Assert.DoesNotContain(MapClear.Key(0, 0), visited);
    }

    [Fact]
    public void Stamp_does_not_cross_walls()
    {
        // Vertical wall at x=3 splits the grid; player at (2,2) radius 3 must not stamp x>=4.
        var walk = FullWalkable(8, 8);
        for (var y = 0; y < 8; y++) walk[y * 8 + 3] = 0;
        var visited = new HashSet<long>();
        MapClear.StampVisited(visited, walk, 8, 8, new NumVec2(2, 2), 3);
        Assert.Contains(MapClear.Key(2, 2), visited);
        Assert.Contains(MapClear.Key(0, 2), visited);
        Assert.DoesNotContain(MapClear.Key(3, 2), visited);
        Assert.DoesNotContain(MapClear.Key(4, 2), visited);
        Assert.DoesNotContain(MapClear.Key(5, 2), visited);
        // So the frontier never targets the sealed-off side: nothing adjacent to visited is over there.
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 8, 8, visited, [], null);
        Assert.NotNull(id);
        Assert.True(MapClear.TryParseCell(id, out var fx, out _));
        Assert.True(fx < 3, $"frontier x={fx} crossed the wall");
    }

    [Fact]
    public void Stamp_falls_back_to_disc_when_player_cell_unwalkable_and_no_seed()
    {
        var walk = new byte[8 * 8]; // all walls except a far cell
        walk[7 * 8 + 7] = 1;
        var visited = new HashSet<long>();
        MapClear.StampVisited(visited, walk, 8, 8, new NumVec2(1, 1), 2);
        Assert.Empty(visited);
    }

    [Fact]
    public void Far_hostile_beyond_aggro_range_yields_to_frontier()
    {
        var walk = FullWalkable(64, 64);
        var visited = VisitedDisc(walk, 64, 64, 2, 2, 1);
        var mobs = new MapClear.MobHint[] { new("e:7", new NumVec2(50, 50), Unique: false) };
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 64, 64, visited, mobs, null, maxMobDistance: 30f);
        Assert.NotNull(id);
        Assert.True(MapClear.TryParseCell(id, out _, out _));
        // Unlimited (0) still chases it.
        Assert.Equal("e:7", MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 64, 64, visited, mobs, null, maxMobDistance: 0f));
    }

    [Fact]
    public void Unique_ignores_aggro_cap()
    {
        var walk = FullWalkable(64, 64);
        var visited = VisitedDisc(walk, 64, 64, 2, 2, 1);
        var mobs = new MapClear.MobHint[] { new("e:boss", new NumVec2(50, 50), Unique: true) };
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 64, 64, visited, mobs, null, maxMobDistance: 30f);
        Assert.Equal("e:boss", id);
    }

    [Fact]
    public void StampDisc_marks_through_walls()
    {
        var walk = FullWalkable(8, 8);
        for (var y = 0; y < 8; y++) walk[y * 8 + 3] = 0;
        var visited = new HashSet<long>();
        MapClear.StampDisc(visited, walk, 8, 8, 2, 2, 3);
        Assert.Contains(MapClear.Key(4, 2), visited);
        Assert.DoesNotContain(MapClear.Key(3, 2), visited);
    }

    [Fact]
    public void Frontier_uses_walking_distance_not_straight_line()
    {
        // Wall at x=5 from y=0..6 (gap at y=7). Player at (4,3); cell (6,3) is 2 away as the crow flies
        // but ~10 by foot; cell (4,7)... everything on the left gets visited first via BFS ordering.
        var w = 12; var h = 9;
        var walk = FullWalkable(w, h);
        for (var y = 0; y <= 6; y++) walk[y * w + 5] = 0;
        var visited = new HashSet<long>();
        // Visit the whole left side except one far corner cell (0,0) AND leave the right side unvisited.
        for (var y = 0; y < h; y++) for (var x = 0; x < 5; x++) if (!(x == 0 && y == 0)) visited.Add(MapClear.Key(x, y));
        for (var y = 7; y < h; y++) visited.Add(MapClear.Key(5, y)); // the gap itself is visited
        var id = MapClear.PickTarget("G1_2", new NumVec2(4, 3), walk, w, h, visited, [], null);
        Assert.NotNull(id);
        Assert.True(MapClear.TryParseCell(id, out var fx, out var fy));
        // Straight-line nearest would be (6,3) (dist 2) but it is 10 steps by foot around the wall.
        // BFS picks the cell with the shortest WALK: (6,7) via the gap is 6 steps, (0,0) is 7.
        Assert.NotEqual((6, 3), (fx, fy));
        Assert.Equal((6, 7), (fx, fy));
    }

    [Fact]
    public void Frontier_never_targets_disconnected_cells()
    {
        var walk = FullWalkable(10, 10);
        for (var y = 0; y < 10; y++) walk[y * 10 + 5] = 0; // full wall, right side unreachable
        var visited = new HashSet<long>();
        for (var y = 0; y < 10; y++) for (var x = 0; x < 5; x++) visited.Add(MapClear.Key(x, y));
        Assert.Null(MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 10, 10, visited, [], null));
    }

    [Fact]
    public void Sweep_prefers_big_unexplored_region_over_nearest_sliver()
    {
        // 60x40 open field. Player at (30,20) with a radius-8 disc stamped. To the left a thin 3-cell strip
        // is unvisited (rest of the left visited); to the right everything is unexplored.
        var w = 60; var h = 40;
        var walk = FullWalkable(w, h);
        var visited = new HashSet<long>();
        for (var y = 0; y < h; y++) for (var x = 0; x < 30; x++) visited.Add(MapClear.Key(x, y));
        for (var y = 18; y <= 22; y++) for (var x = 18; x <= 20; x++) visited.Remove(MapClear.Key(x, y)); // sliver at ~10 cells left
        MapClear.StampVisited(visited, walk, w, h, new NumVec2(30, 20), 8);
        Assert.True(MapClear.TryBestSweepTarget(walk, w, h, visited, new NumVec2(30, 20), 8, default, out var bx, out var by));
        Assert.True(bx > 30, $"sweep went to ({bx},{by}) instead of the open right side");
        // Plain nearest would have chosen the edge of the disc / the sliver.
        var id = MapClear.PickTarget("G1_2", new NumVec2(30, 20), walk, w, h, visited, [], null, stampRadius: 8);
        Assert.True(MapClear.TryParseCell(id, out var px, out _) && px > 30);
    }

    [Fact]
    public void Sweep_ignores_slivers_and_reports_cleared()
    {
        // Fully explored field except a 2x2 nook: not worth walking to → cleared (null), not a target.
        var w = 40; var h = 40;
        var walk = FullWalkable(w, h);
        var visited = new HashSet<long>();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) visited.Add(MapClear.Key(x, y));
        visited.Remove(MapClear.Key(30, 30)); visited.Remove(MapClear.Key(31, 30));
        visited.Remove(MapClear.Key(30, 31)); visited.Remove(MapClear.Key(31, 31));
        Assert.Null(MapClear.PickTarget("G1_2", new NumVec2(5, 5), walk, w, h, visited, [], null, stampRadius: 10));
        // But a real unexplored pocket (12x12) is still targeted.
        for (var y = 20; y < 32; y++) for (var x = 20; x < 32; x++) visited.Remove(MapClear.Key(x, y));
        var id = MapClear.PickTarget("G1_2", new NumVec2(5, 5), walk, w, h, visited, [], null, stampRadius: 10);
        Assert.NotNull(id);
        Assert.True(MapClear.TryParseCell(id, out var tx, out var ty) && tx >= 20 && ty >= 20);
    }

    [Fact]
    public void Sweep_prefers_route_through_fog_over_backtracking_through_cleared_ground()
    {
        // Corridor map: player at the junction (20,20). Left arm x<20 fully explored ending in a fog pocket at
        // x 0..6; right arm x>20 unexplored all the way. Both pockets are the same straight-line distance, but
        // walking right clears as it goes (cheap steps) → the sweep must pick the right side.
        var w = 60; var h = 41;
        var walk = new byte[w * h];
        for (var x = 0; x < w; x++) for (var y = 18; y <= 22; y++) walk[y * w + x] = 1;
        var visited = new HashSet<long>();
        for (var x = 7; x <= 21; x++) for (var y = 18; y <= 22; y++) visited.Add(MapClear.Key(x, y));
        Assert.True(MapClear.TryBestSweepTarget(walk, w, h, visited, new NumVec2(20, 20), 6, default, out var bx, out _));
        Assert.True(bx > 20, $"picked ({bx}) — backtracked through cleared corridor");
    }

    [Fact]
    public void Sweep_never_targets_unreachable_and_falls_back_when_cleared()
    {
        var walk = FullWalkable(20, 20);
        for (var y = 0; y < 20; y++) walk[y * 20 + 10] = 0;
        var visited = new HashSet<long>();
        for (var y = 0; y < 20; y++) for (var x = 0; x < 10; x++) visited.Add(MapClear.Key(x, y));
        Assert.False(MapClear.TryBestSweepTarget(walk, 20, 20, visited, new NumVec2(3, 3), 6, default, out _, out _));
        Assert.Null(MapClear.PickTarget("G1_2", new NumVec2(3, 3), walk, 20, 20, visited, [], null, stampRadius: 6));
    }

    [Fact]
    public void TryParseCell_roundtrip()
    {
        var id = MapClear.CellId(12, -3);
        Assert.Equal("c:12,-3", id);
        Assert.True(MapClear.TryParseCell(id, out var x, out var y));
        Assert.Equal(12, x);
        Assert.Equal(-3, y);
        Assert.False(MapClear.TryParseCell("e:9", out _, out _));
        Assert.False(MapClear.TryParseCell("c:nope", out _, out _));
    }
}
