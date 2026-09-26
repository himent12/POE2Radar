using POE2Radar.Overlay;
using Xunit;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Tests;

public sealed class AtlasSceneRenderTests
{
    [Fact]
    public void Atlas_frame_renders()
    {
        using var win = OverlayWindow.CreateHeadless(1920, 1080);
        using var r = new OverlayRenderer(win);
        var marks = new List<AtlasMark>();
        for (var i = 0; i < 60; i++)
            marks.Add(new AtlasMark(100 + (i % 10) * 170, 80 + (i / 10) * 150, i % 3 == 0, i % 2 == 0, false, true, i % 13, i % 4,
                i % 5 == 0 ? "Map " + i : null, i % 7 == 0 ? "#FF8800" : null, Arrow: i == 7, Nav: i % 11 == 0,
                ContentIcons: i % 4 == 0 ? new[] { "AtlasIconContentRitual", "AtlasIconContentStrongBox" } : null));
        marks.Add(new AtlasMark(5000, 5000, true, false, false, true, 1, 0, "Far", null, Arrow: true));
        var routes = new List<AtlasRouteInfo>();
        for (var k = 0; k < 4; k++)
        {
            var pts = new List<NumVec2>();
            for (var j = 0; j < 8; j++) pts.Add(new NumVec2(100 + j * 170, 80 + ((j + k) % 6) * 150));
            routes.Add(new AtlasRouteInfo(pts, k == 0 ? null : "#44CCFF", 7 + k));
        }
        var ctx = InsMenuRenderTests.Ctx(0) with
        {
            WindowWidth = 1920, WindowHeight = 1080, AtlasOpen = true, AtlasNodes = marks,
            AtlasScale = 1f, AtlasScaleY = 1f, AtlasAutoRoutes = routes,
            AtlasRoute = new List<NumVec2> { new(100, 80), new(440, 380), new(780, 530) },
            AtlasStart = new NumVec2(100, 80), AtlasEnd = new NumVec2(780, 530), AtlasCurrent = new NumVec2(270, 230),
            InsMenu = null,
        };
        r.Render(ctx);
        r.Render(ctx);
        var outFile = Environment.GetEnvironmentVariable("POE2RADAR_BENCH_OUT");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POE2RADAR_BENCH")))
        {
            for (var i = 0; i < 20; i++) r.Render(ctx);
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) r.Render(ctx);
            var line = $"AtlasBenchmark 1920x1080 (61 nodes, 4 auto-routes, content icons): {sw.Elapsed.TotalMilliseconds / 200:0.000} ms/frame, alloc {(GC.GetAllocatedBytesForCurrentThread() - a0) / 200.0:0} B/frame";
            Console.WriteLine(line);
            if (!string.IsNullOrEmpty(outFile)) File.AppendAllText(outFile, line + Environment.NewLine);
        }
        var dir = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "atlas.png"), win.SnapshotPng()); }
    }
}
