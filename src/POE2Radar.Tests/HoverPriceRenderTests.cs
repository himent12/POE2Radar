using POE2Radar.Overlay;
using Xunit;

namespace POE2Radar.Tests;

public sealed class HoverPriceRenderTests
{
    [Fact]
    public void Rare_comparison_details_render_with_coverage_and_sample_counts()
    {
        using var window = OverlayWindow.CreateHeadless(640, 480);
        using var renderer = new OverlayRenderer(window);
        var context = InsMenuRenderTests.Ctx(0) with
        {
            WindowWidth = 640, WindowHeight = 480,
            HoverPrice = new HoverPriceLabel(600, 450, 48, 48, "Comparable asks: 30 ex",
                "Gold Ring\nMedian of 10 cheapest seller samples · 2 ex–180 ex\n2 matched affixes (±20% rolls); 0 unsupported\nDPS, defences, item level, sockets, quality and corruption not filtered\nListings, not completed sales.\nCached comparison · 0m ago\nOfficial trade · Forbidden Rites\nCtrl+D: open comparisons", false),
        };
        renderer.RenderHoverPricePreview(context);
        var directory = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "rare-comparison.png"), window.SnapshotPng());
        }
    }

    [Theory]
    [InlineData(1280, 800, 500, 250)]
    [InlineData(640, 480, 615, 450)]
    [InlineData(320, 240, 1, 1)]
    [InlineData(64, 64, 60, 60)]
    public void Hover_details_wrap_and_render_at_viewport_edges(int width, int height, int x, int y)
    {
        using var window = OverlayWindow.CreateHeadless(width, height);
        using var renderer = new OverlayRenderer(window);
        var context = InsMenuRenderTests.Ctx(0) with
        {
            WindowWidth = width, WindowHeight = height,
            HoverPrice = new HoverPriceLabel(x, y, 48, 48, "Estimated value: 18 div",
                "Rite of Passage\n263 listings · rolls may change value · identified reference\nRecent trend: -12.5%\n3 market variants: 18–90 div · 280 listings\npoe.ninja · HC Forbidden Rites · cached 12m ago\nCtrl+D: compare listings (adjust mods on trade)", true),
        };
        renderer.RenderHoverPricePreview(context);
        var png = window.SnapshotPng();
        Assert.True(png.Length > 100);
        var directory = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"hover-price-{width}.png"), png);
        }
    }
}
