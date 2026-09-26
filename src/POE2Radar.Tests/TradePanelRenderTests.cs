using POE2Radar.Overlay;
using Xunit;

namespace POE2Radar.Tests;

public sealed class TradePanelRenderTests
{
    [Fact]
    public void Cards_register_action_buttons_per_direction()
    {
        using var window = OverlayWindow.CreateHeadless(1280, 800);
        using var renderer = new OverlayRenderer(window);
        renderer.RenderTradePanelPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null });

        var actions = renderer.LegendRowRects.Select(r => r.Action).ToList();
        Assert.Contains("trade:1:invite", actions);
        Assert.Contains("trade:1:trade", actions);
        Assert.Contains("trade:1:dismiss", actions);
        Assert.Contains("trade:3:visithideout", actions);
        Assert.DoesNotContain("trade:3:kick", actions);
        var directory = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "trade-panel.png"), window.SnapshotPng());
        }
    }

    [Fact]
    public void Overflow_beyond_four_cards_is_summarised_not_drawn()
    {
        using var window = OverlayWindow.CreateHeadless(1280, 800);
        using var renderer = new OverlayRenderer(window);
        var many = Enumerable.Range(1, 7).Select(i => InsMenuRenderTests.SampleTrades[0] with { Id = i }).ToArray();
        renderer.RenderTradePanelPreview(InsMenuRenderTests.Ctx(0) with { InsMenu = null, Trades = many });
        var actions = renderer.LegendRowRects.Select(r => r.Action).ToList();
        Assert.Contains("trade:4:dismiss", actions);
        Assert.DoesNotContain("trade:5:dismiss", actions);
    }
}
