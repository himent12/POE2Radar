using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// Path move must not be armable over HTTP. Armed state is F5 + /state read-only.
/// </summary>
public sealed class PathMoveSettingsGuardTests
{
    [Fact]
    public void RadarState_exposes_path_move_status_fields_default_off()
    {
        var s = RadarState.Empty;
        Assert.False(s.PathMove);
        Assert.Equal("", s.PathMoveNote);
    }

    [Fact]
    public void Dashboard_has_no_control_that_arms_path_move()
    {
        var page = DashboardHtml.Page;
        Assert.DoesNotContain("moveEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pathMoveEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-set=\"pathMove\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-set=\"moveEnabled\"", page, StringComparison.Ordinal);
        Assert.Contains("F5 toggles path move", page, StringComparison.Ordinal);
        Assert.Contains("cannot be armed from this page", page, StringComparison.Ordinal);
        Assert.Contains("id=\"pathMoveState\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveArriveRadius\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveCooldownMs\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveMethod\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveKeyW\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveKeyA\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveKeyS\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveKeyD\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"moveClickKey\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"Click\"", page, StringComparison.Ordinal);
    }
}
