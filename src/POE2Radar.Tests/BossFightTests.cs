using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Boss-mode dodge trigger: timed rolls while engaged, an immediate roll on a player-life spike,
/// nothing when no boss is around; remembers the boss position for the post-respawn walk back.</summary>
public sealed class BossFightTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static BossFight New() => new() { DodgeIntervalMs = 4000, SpikePct = 12f, SpikeWindowMs = 600, MinGapMs = 900 };

    [Fact]
    public void No_boss_never_dodges_and_clears_engagement()
    {
        var b = New();
        Assert.False(b.WantDodge(false, 0, default, 50f, T0, out var why));
        Assert.Equal("", why);
        Assert.False(b.Engaged);
    }

    [Fact]
    public void Timer_rolls_every_interval_from_engagement_not_on_first_frame()
    {
        var b = New();
        Assert.False(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0, out var why));
        Assert.StartsWith("next timed roll", why);
        Assert.False(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0.AddSeconds(3.9), out _));
        Assert.True(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0.AddSeconds(4), out why));
        Assert.Equal("boss timer", why);
        // Stays wanted until the executor reports the roll, then the clock restarts.
        Assert.True(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0.AddSeconds(4.5), out _));
        b.Rolled(T0.AddSeconds(4.5));
        Assert.False(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0.AddSeconds(5), out _));
        Assert.True(b.WantDodge(true, 7, new NumVec2(10, 10), 100f, T0.AddSeconds(8.5), out _));
    }

    [Fact]
    public void Life_spike_inside_window_triggers_a_roll()
    {
        var b = New();
        b.WantDodge(true, 7, default, 90f, T0, out _);
        b.Rolled(T0); // arm the min-gap clock from a known point
        Assert.False(b.WantDodge(true, 7, default, 88f, T0.AddSeconds(1), out _));
        Assert.False(b.WantDodge(true, 7, default, 80f, T0.AddSeconds(1.2), out _));  // -8 → under the spike
        Assert.True(b.WantDodge(true, 7, default, 74f, T0.AddSeconds(1.4), out var why)); // -14 within 0.6 s
        Assert.Equal("hp spike -14%", why);
    }

    [Fact]
    public void Slow_drain_outside_the_window_is_not_a_spike()
    {
        var b = New();
        b.DodgeIntervalMs = 0;
        b.WantDodge(true, 7, default, 90f, T0, out _);
        b.Rolled(T0);
        var hp = 90f;
        for (var i = 1; i <= 10; i++)
        {
            hp -= 3f; // 3 % per 300 ms = well over 12 % in total but never 12 within 600 ms
            Assert.False(b.WantDodge(true, 7, default, hp, T0.AddMilliseconds(1000 + i * 300), out var why));
            Assert.DoesNotContain("hp spike", why);
        }
    }

    [Fact]
    public void Min_gap_stops_one_hit_from_chaining_rolls()
    {
        var b = New();
        b.WantDodge(true, 7, default, 90f, T0, out _);
        b.Rolled(T0);
        Assert.False(b.WantDodge(true, 7, default, 90f, T0.AddSeconds(0.7), out _));
        Assert.True(b.WantDodge(true, 7, default, 70f, T0.AddSeconds(1), out _));
        b.Rolled(T0.AddSeconds(1));
        Assert.False(b.WantDodge(true, 7, default, 50f, T0.AddSeconds(1.5), out _)); // 0.5 s < min gap
        Assert.True(b.WantDodge(true, 7, default, 30f, T0.AddSeconds(2), out _));
    }

    [Fact]
    public void Boss_position_is_remembered_across_disengage_for_the_walk_back()
    {
        var b = New();
        b.WantDodge(true, 42, new NumVec2(120, 80), 100f, T0, out _);
        b.WantDodge(false, 0, default, 100f, T0.AddSeconds(1), out _);
        Assert.True(b.HasBoss);
        Assert.Equal(42u, b.BossId);
        Assert.Equal(new NumVec2(120, 80), b.BossGrid);
        b.Reset();
        Assert.False(b.HasBoss);
    }
}
