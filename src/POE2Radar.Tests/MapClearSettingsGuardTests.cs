using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// Map-clear must not be armable over HTTP. Armed state is F2 + /state read-only.
/// Stamp radius is a dashboard tunable.
/// </summary>
public sealed class MapClearSettingsGuardTests
{
    [Fact]
    public void RadarState_exposes_map_clear_status_fields_default_off()
    {
        var s = RadarState.Empty;
        Assert.False(s.MapClear);
        Assert.Equal("", s.MapClearNote);
    }

    [Fact]
    public void Dashboard_has_no_control_that_arms_map_clear()
    {
        var page = DashboardHtml.Page;
        Assert.DoesNotContain("mapClearEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-set=\"mapClear\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-set=\"mapClearEnabled\"", page, StringComparison.Ordinal);
        Assert.Contains("F2 toggles map-clear", page, StringComparison.Ordinal);
        Assert.Contains("cannot be armed from this page", page, StringComparison.Ordinal);
        Assert.Contains("id=\"mapClearState\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"mapClearStampRadius\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"kClear\"", page, StringComparison.Ordinal);
    }
}
