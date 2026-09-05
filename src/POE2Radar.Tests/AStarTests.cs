using POE2Radar.Core.Pathfinding;
using Xunit;

namespace POE2Radar.Tests;

public sealed class AStarTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Expansion_count_respects_budget(int budget)
    {
        var grid = NavGrid.Build(Enumerable.Repeat((byte)1, 20).ToArray(), 20, 1);
        var search = new AStar(20, 1);
        Assert.False(search.FindPath(grid, new(0, 0), new(19, 0), budget).Found);
        Assert.Equal(budget, search.LastExpanded);
        Assert.True(search.FindPath(grid, new(0, 0), new(19, 0)).Found);
    }

    [Fact]
    public void Rejected_search_resets_diagnostics()
    {
        var grid = NavGrid.Build(new byte[] { 1, 1, 0, 1 }, 4, 1);
        var search = new AStar(4, 1);
        Assert.True(search.FindPath(grid, new(0, 0), new(1, 0)).Found);
        Assert.True(search.LastExpanded > 0);
        Assert.False(search.FindPath(grid, new(0, 0), new(3, 0)).Found);
        Assert.Equal(0, search.LastExpanded);
        Assert.False(search.FindPath(grid, new(0, 0), new(2, 0)).Found);
        Assert.Equal(0, search.LastExpanded);
    }

    [Fact]
    public void Goal_can_be_returned_at_exact_budget()
    {
        var grid = NavGrid.Build(new byte[] { 1, 1 }, 2, 1);
        var search = new AStar(2, 1);
        Assert.True(search.FindPath(grid, new(0, 0), new(1, 0), 1).Found);
        Assert.Equal(1, search.LastExpanded);
        Assert.True(search.FindPath(grid, new(0, 0), new(0, 0), 0).Found);
        Assert.Equal(0, search.LastExpanded);
    }

    [Fact]
    public void Default_search_matches_dijkstra_cost_on_seeded_maps()
    {
        var random = new Random(1729);
        const int size = 24;
        var search = new AStar(size, size);
        for (var sample = 0; sample < 100; sample++)
        {
            var cells = new byte[size * size];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = random.NextDouble() < 0.18 ? (byte)0 : (byte)1;
            cells[0] = cells[^1] = 1;
            var grid = NavGrid.Build(cells, size, size);
            var expected = search.FindPath(grid, new(0, 0), new(size - 1, size - 1), heuristicWeight: 0f);
            var actual = search.FindPath(grid, new(0, 0), new(size - 1, size - 1));
            Assert.Equal(expected.Found, actual.Found);
            if (expected.Found) Assert.InRange(Math.Abs(expected.Cost - actual.Cost), 0f, 0.0001f);
        }
    }
}
