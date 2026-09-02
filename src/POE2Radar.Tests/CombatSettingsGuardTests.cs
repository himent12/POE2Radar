using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// Combat assist must not be armable over HTTP. Armed state is F4 + /state read-only.
/// </summary>
public sealed class CombatSettingsGuardTests
{
    [Fact]
    public void RadarState_exposes_combat_status_fields()
    {
        var s = RadarState.Empty;
        Assert.False(s.CombatAssist);
        Assert.Equal("", s.CombatNote);
    }

    [Fact]
    public void Dashboard_has_no_control_that_arms_combat_assist()
    {
        var page = DashboardHtml.Page;
        Assert.DoesNotContain("combatAssistEnabled", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-set=\"combatAssist\"", page, StringComparison.Ordinal);
        Assert.Contains("cannot be armed from this page", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"combatRange\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"combatAttackKey\"", page, StringComparison.Ordinal);
        Assert.Contains("data-set=\"combatCooldownMs\"", page, StringComparison.Ordinal);
    }
}
