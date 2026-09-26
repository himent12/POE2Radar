using System.Diagnostics;
using System.Security.Cryptography;
using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;
using Xunit.Abstractions;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Tests;

/// <summary>
/// Headless render benchmark: a realistic 1920×1080 frame with the big map open (1500×1500 terrain, ~300
/// rule-resolved entities, 40 HP bars, 30 item labels, 3 routes, an expanded nav menu, loot tags, status
/// strip). Prints ms/frame and managed bytes allocated/frame plus a pixel hash (to check a perf change is
/// pixel-identical). Skipped unless POE2RADAR_BENCH is set so CI stays fast:
/// <c>POE2RADAR_BENCH=1 dotnet test src/POE2Radar.Tests -c Release --filter RenderBenchmark --logger "console;verbosity=detailed"</c>.
/// The always-on <see cref="Bench_frame_renders"/> keeps the scene itself compiling + drawing in CI.
/// </summary>
public sealed class RenderBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public RenderBenchmarkTests(ITestOutputHelper output) => _out = output;

    private const int W = 1920, H = 1080;

    internal static RenderContext Scene()
    {
        var rng = new Random(1234);

        // Terrain: 1500×1500 with blobby walkable rooms + corridors (lots of edge pixels, like a real map).
        const int tw = 1500, th = 1500;
        var walk = new byte[tw * th];
        for (var i = 0; i < 220; i++)
        {
            int cx = rng.Next(50, tw - 50), cy = rng.Next(50, th - 50), r = rng.Next(10, 45);
            for (var y = Math.Max(0, cy - r); y < Math.Min(th, cy + r); y++)
                for (var x = Math.Max(0, cx - r); x < Math.Min(tw, cx + r); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) walk[y * tw + x] = 1;
        }
        for (var y = 740; y < 760; y++) for (var x = 100; x < 1400; x++) walk[y * tw + x] = 1;
        for (var x = 740; x < 760; x++) for (var y = 100; y < 1400; y++) walk[y * tw + x] = 1;

        var player = new NumVec2(750, 750);

        // Display rules (one per archetype), resolved by a cheap allocation-free closure like DisplayRules.Resolve.
        DisplayRule R(string shape, string color, float size, string? label = null)
            => new() { Shape = shape, Color = color, Opacity = 0.95f, Size = size, Label = label };
        var rNormal = R("Circle", "#FF3333", 2.6f);
        var rMagic = R("Fang", "#73A6FF", 5.5f);
        var rRare = R("Claw", "#FFD926", 7.5f);
        var rUnique = R("Skull", "#FF7300", 8f, "Boss");
        var rChest = R("Chest", "#FFD926", 5f);
        var rNpc = R("Chat", "#FFD933", 4.2f, "NPC");
        var rTrans = R("Stairs", "#66FF99", 5f, "Exit");
        var rPoi = R("MapPin", "#8CBFFF", 3.6f);
        var rHide = new DisplayRule { Hide = true };

        var ents = new List<Poe2Live.EntityDot>(300);
        for (var i = 0; i < 300; i++)
        {
            var g = player + new NumVec2(rng.Next(-160, 160), rng.Next(-160, 160));
            var cat = i < 240 ? Poe2Live.EntityCategory.Monster
                : (Poe2Live.EntityCategory)(new[] { 2, 3, 4, 5, 6 }[i % 5]);
            var rar = cat == Poe2Live.EntityCategory.Monster
                ? (Poe2Live.Rarity)(i % 17 == 0 ? 3 : i % 7 == 0 ? 2 : i % 3 == 0 ? 1 : 0) : Poe2Live.Rarity.NonMonster;
            ents.Add(new Poe2Live.EntityDot((uint)i, 0x1000 + i, g, new Vector3 { X = g.X * 10.87f, Y = g.Y * 10.87f },
                cat, "Metadata/Monsters/Foo/Bar" + (i % 13), 100, 100, i % 11 == 0, 0, rar, false));
        }
        DisplayRule? Resolve(Poe2Live.EntityDot e) => e.Category switch
        {
            Poe2Live.EntityCategory.Monster => e.Rarity switch
            {
                Poe2Live.Rarity.Unique => rUnique, Poe2Live.Rarity.Rare => rRare,
                Poe2Live.Rarity.Magic => rMagic, _ => rNormal,
            },
            Poe2Live.EntityCategory.Npc => rNpc,
            Poe2Live.EntityCategory.Chest => rChest,
            Poe2Live.EntityCategory.Transition => rTrans,
            Poe2Live.EntityCategory.Object => e.Poi ? rPoi : rHide,
            _ => null,
        };

        var landmarks = new List<Poe2Live.Landmark>();
        for (var i = 0; i < 15; i++)
            landmarks.Add(new Poe2Live.Landmark("Landmark " + i, "Art/Tiles/lm" + i,
                player + new NumVec2(rng.Next(-300, 300), rng.Next(-300, 300)), 10, i % 2 == 0 ? "Curated " + i : null));

        var paths = new List<SelectedPath>();
        for (var p = 0; p < 3; p++)
        {
            var pts = new List<(int x, int y)>();
            int x = 750, y = 750;
            for (var k = 0; k < 60; k++) { x += rng.Next(-2, 6) * (p == 1 ? -1 : 1); y += rng.Next(-2, 6) * (p == 2 ? -1 : 1); pts.Add((x, y)); }
            paths.Add(new SelectedPath(p, pts));
        }

        var legend = new List<LegendEntry>();
        for (var i = 0; i < 12; i++)
            legend.Add(new LegendEntry(new NavTarget("t:lm" + i, "Target " + i, player, "lm" + i, i % 3 == 0), i < 3 ? i : -1, i < 3));

        // Camera: orthographic world→clip, world (0,0) at screen centre, ±1000 world units ≈ edges.
        var cam = new float[16];
        cam[0] = 1f / 1000f; cam[5] = 1f / 1000f; cam[10] = 1f; cam[15] = 1f;

        var hp = new List<HpBarTarget>();
        for (var i = 0; i < 40; i++)
            hp.Add(new HpBarTarget(new Vector3 { X = rng.Next(-900, 900), Y = rng.Next(-900, 900) },
                (float)rng.NextDouble(), 30 + (i % 4) * 10, 0xFFFF3333u, i % 4, 0xFFFFD926u));

        var items = new List<ItemLabel>();
        for (var i = 0; i < 30; i++)
            items.Add(new ItemLabel(new Vector3 { X = rng.Next(-900, 900), Y = rng.Next(-900, 900) },
                "Unique Item " + i, (i * 1.7).ToString("0.0") + " ex", i % 5 == 0, i % 3 == 0));

        var loot = new List<LootTagLabel>();
        for (var i = 0; i < 10; i++)
            loot.Add(new LootTagLabel(200 + i * 30, 100 + i * 40, 120, 20, (i + 1) + " ex", i % 4 == 0));

        return InsMenuRenderTests.Ctx(0) with
        {
            WindowWidth = W, WindowHeight = H,
            PlayerGrid = player,
            PlayerWorld = new Vector3 { X = player.X * 10.87f, Y = player.Y * 10.87f },
            Map = new Poe2Live.MapUi(true, 0f, 0f, 0.5f),
            Entities = ents, Landmarks = landmarks,
            AreaHash = 0xC0FFEE,
            Terrain = new Poe2Live.TerrainData(walk, tw, th),
            CameraMatrix = cam,
            SelectedPaths = paths, Legend = legend, NavMenuExpanded = true,
            HpBarTargets = hp, ItemLabels = items, LootTags = loot,
            Resolve = Resolve, ResolveTile = _ => null,
            InsMenu = null,
        };
    }

    [Fact]
    public void Bench_frame_renders()
    {
        using var win = OverlayWindow.CreateHeadless(W, H);
        using var r = new OverlayRenderer(win);
        r.Render(Scene());
        Assert.NotEqual(0, PixelHash(win).Length);
    }

    /// <summary>The first map frame draws the terrain directly (the layer cache waits for a stable zoom); the
    /// second blits the cached layer. Stationary player → the two must match (bar sub-pixel sampling ties).</summary>
    [Fact]
    public void Terrain_layer_cache_matches_direct_draw()
    {
        using var win = OverlayWindow.CreateHeadless(W, H);
        using var r = new OverlayRenderer(win);
        var ctx = Scene() with { Entities = Array.Empty<Poe2Live.EntityDot>(), HpBarTargets = null, ItemLabels = null,
            LootTags = null, SelectedPaths = Array.Empty<SelectedPath>(), Landmarks = Array.Empty<Poe2Live.Landmark>() };
        r.Render(ctx);
        var direct = Pixels(win);
        r.Render(ctx);
        var cached = Pixels(win);
        int diff = 0, maxd = 0, nonzero = 0;
        for (var i = 0; i < direct.Length; i++)
        {
            if (direct[i] != 0) nonzero++;
            var d = Math.Abs(direct[i] - cached[i]);
            if (d != 0) { diff++; maxd = Math.Max(maxd, d); }
        }
        _out.WriteLine($"terrain cache vs direct: {diff} differing bytes of {nonzero} non-zero, max diff {maxd}");
        Assert.True(diff <= nonzero / 1000, $"{diff} differing bytes (of {nonzero} non-zero)");

        // Player movement (translation only) reuses the cached layer with a whole-pixel offset blit. vs a direct
        // draw at the new position (fresh renderer → its first frame is direct) the terrain may sit up to 0.5 px
        // off, which flips nearest-neighbour samples along wall edges only — a few % of the terrain bytes.
        var moved = ctx with { PlayerGrid = ctx.PlayerGrid + new NumVec2(7.3f, -4.1f) };
        var hits = r.TerrainLayerHits;
        r.Render(moved);
        Assert.Equal(hits + 1, r.TerrainLayerHits);   // drawn from the layer…
        Assert.Equal(1, r.TerrainLayerRebuilds);       // …without re-rasterizing it
        var viaCache = Pixels(win);
        using var r2 = new OverlayRenderer(win);
        r2.Render(moved);
        var viaDirect = Pixels(win);
        int mdiff = 0;
        for (var i = 0; i < viaCache.Length; i++) if (viaCache[i] != viaDirect[i]) mdiff++;
        _out.WriteLine($"moved: cached-offset vs direct: {mdiff} differing bytes");
        Assert.True(mdiff <= nonzero / 10, $"{mdiff} differing bytes after a move");
    }

    private static unsafe byte[] Pixels(OverlayWindow win)
        => new ReadOnlySpan<byte>((void*)win.PixelBuffer, win.PixelRowBytes * win.Height).ToArray();

    [Fact]
    public void RenderBenchmark_map_open_1080p()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POE2RADAR_BENCH"))) return;
        using var win = OverlayWindow.CreateHeadless(W, H);
        using var r = new OverlayRenderer(win);
        var ctx = Scene();

        for (var i = 0; i < 30; i++) r.Render(ctx);   // warmup (JIT, glyph caches, terrain bake)

        const int n = 300;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var gc0 = GC.CollectionCount(0);
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var times = new double[n];
        var total = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            var t = Stopwatch.GetTimestamp();
            r.Render(ctx);
            times[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        total.Stop();
        var a1 = GC.GetAllocatedBytesForCurrentThread();
        Array.Sort(times);

        var msg = $"RenderBenchmark 1920x1080 map-open: mean {total.Elapsed.TotalMilliseconds / n:0.000} ms/frame, " +
                  $"p50 {times[n / 2]:0.000} ms, p95 {times[(int)(n * 0.95)]:0.000} ms, " +
                  $"alloc {(a1 - a0) / (double)n:0} B/frame, gen0 GCs {GC.CollectionCount(0) - gc0}, " +
                  $"pixelhash {PixelHash(win)}";
        _out.WriteLine(msg);
        Console.WriteLine(msg);
        var outFile = Environment.GetEnvironmentVariable("POE2RADAR_BENCH_OUT");
        if (!string.IsNullOrEmpty(outFile)) File.AppendAllText(outFile, msg + Environment.NewLine);
        // Per-layer breakdown: the same frame with one layer removed (delta ≈ that layer's cost).
        // Moving player: the grid position drifts every frame (~1.3 cells — fast, so the terrain layer cache
        // has to re-rasterize a few times in the run); and zooming: the map zoom changes every frame.
        foreach (var (vname, make) in new (string, Func<int, RenderContext>)[]
        {
            ("moving player", i => ctx with { PlayerGrid = ctx.PlayerGrid + new NumVec2(i * 1.2f, i * 0.5f) }),
            ("zooming (zoom changes/frame)", i => ctx with { Map = ctx.Map with { Zoom = 0.45f + 0.1f * MathF.Sin(i * 0.05f) } }),
        })
        {
            var moving = new RenderContext[400];
            for (var i = 0; i < moving.Length; i++) moving[i] = make(i);
            for (var i = 0; i < 10; i++) r.Render(moving[i]);
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            var mt = new double[moving.Length - 10];
            for (var i = 10; i < moving.Length; i++) { var t = Stopwatch.GetTimestamp(); r.Render(moving[i]); mt[i - 10] = Stopwatch.GetElapsedTime(t).TotalMilliseconds; }
            var alloc = (GC.GetAllocatedBytesForCurrentThread() - b0) / (double)mt.Length;
            var mean = mt.Average(); Array.Sort(mt);
            var line = $"  variant {vname,-28}: {mean:0.000} ms/frame, p50 {mt[mt.Length / 2]:0.000}, max {mt[^1]:0.000} ms, alloc {alloc:0} B/frame";
            _out.WriteLine(line);
            if (!string.IsNullOrEmpty(outFile)) File.AppendAllText(outFile, line + Environment.NewLine);
        }
        foreach (var (name, v) in new (string, RenderContext)[]
        {
            ("empty (clear+present only)", ctx with { Active = false }),
            ("no terrain", ctx with { ShowTerrain = false }),
            ("no entities", ctx with { Entities = Array.Empty<Poe2Live.EntityDot>() }),
            ("no hp bars", ctx with { HpBarTargets = null }),
            ("no item labels", ctx with { ItemLabels = null }),
            ("map closed", ctx with { Map = default }),
        })
        {
            for (var i = 0; i < 10; i++) r.Render(v);
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) r.Render(v);
            var line = $"  variant {name,-28}: {sw.Elapsed.TotalMilliseconds / 100:0.000} ms/frame, alloc {(GC.GetAllocatedBytesForCurrentThread() - b0) / 100.0:0} B/frame";
            _out.WriteLine(line);
            if (!string.IsNullOrEmpty(outFile)) File.AppendAllText(outFile, line + Environment.NewLine);
        }
        r.Render(ctx);

        var dir = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "bench-frame.png"), win.SnapshotPng()); }
    }

    private static unsafe string PixelHash(OverlayWindow win)
    {
        var span = new ReadOnlySpan<byte>((void*)win.PixelBuffer, win.PixelRowBytes * win.Height);
        return Convert.ToHexString(SHA256.HashData(span))[..16];
    }
}
