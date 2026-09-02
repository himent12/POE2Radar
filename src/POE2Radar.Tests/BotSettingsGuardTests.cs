using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// Bot master must not be armable over HTTP. Armed state is F3 + /state read-only.
/// The dashboard may edit skill rotation + range + move tunables, never BotEnabled.
/// </summary>
public sealed class BotSettingsGuardTests
{
    [Fact]
    public void RadarState_exposes_bot_status_fields_default_off()
    {
        var s = RadarState.Empty;
        Assert.False(s.Bot);
        Assert.Equal("", s.BotNote);
    }

    [Fact]
    public void Dashboard_has_no_control_that_arms_the_bot()
    {
        var page = DashboardHtml.Page;
        Assert.DoesNotContain("botEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-set=\"bot\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-set=\"botEnabled\"", page, StringComparison.Ordinal);
        Assert.Contains("cannot be armed from this page", page, StringComparison.Ordinal);
        Assert.Contains("id=\"botState\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"combatSkills\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"combatRange\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"pathMoveState\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"questFollowState\"", page, StringComparison.Ordinal);
        Assert.Contains("Combat / Bot", page, StringComparison.Ordinal);
    }
}
