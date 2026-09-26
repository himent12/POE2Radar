using POE2Radar.Core.Game;
using POE2Radar.Overlay.Navigation;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BackgroundReplannerTests
{
    private static Poe2Live.TerrainData Open(int w, int h)
    {
        var walkable = new byte[w * h];
        Array.Fill(walkable, (byte)1);
        return new Poe2Live.TerrainData(walkable, w, h);
    }

    [Fact]
    public async Task Results_carry_the_terrain_they_were_planned_on()
    {
        // The caller drops a route whose terrain isn't the current area's: target ids repeat across zones.
        using var replanner = new BackgroundReplanner();
        var oldZone = Open(40, 40);
        replanner.Enqueue(new BackgroundReplanner.Request("waypoint", oldZone, (2, 2), (30, 30)));
        List<BackgroundReplanner.Result> results = [];
        for (var i = 0; i < 200 && results.Count == 0; i++)
        {
            if (replanner.TryDrainResults(out var drained)) results = drained;
            else await Task.Delay(10);
        }
        var result = Assert.Single(results);
        Assert.Equal("waypoint", result.TargetId);
        Assert.Same(oldZone, result.Terrain);
    }
}
