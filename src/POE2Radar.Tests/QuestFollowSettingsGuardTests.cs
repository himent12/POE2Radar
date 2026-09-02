using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// Quest follow must not be armable over HTTP. Armed state is F3 + /state read-only.
/// </summary>
public sealed class QuestFollowSettingsGuardTests
{
    [Fact]
    public void RadarState_exposes_quest_follow_status_fields_default_off()
    {
        var s = RadarState.Empty;
        Assert.False(s.QuestFollow);
        Assert.Equal("", s.QuestFollowNote);
    }

    [Fact]
    public void Dashboard_has_no_control_that_arms_quest_follow()
    {
        var page = DashboardHtml.Page;
        Assert.DoesNotContain("questFollowEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-set=\"questFollow\"", page, StringComparison.Ordinal);
        Assert.Contains("F3 toggles quest follow", page, StringComparison.Ordinal);
        Assert.Contains("cannot be armed from this page", page, StringComparison.Ordinal);
        Assert.Contains("id=\"questFollowState\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"questUseKey\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"questUseRadius\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"questUseCooldownMs\"", page, StringComparison.Ordinal);
    }
}
