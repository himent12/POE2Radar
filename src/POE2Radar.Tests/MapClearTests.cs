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
