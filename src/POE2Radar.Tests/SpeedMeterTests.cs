using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Speed from the position history; walk / run speeds learn themselves so "is the run key working"
/// is detected instead of tuned.</summary>
public sealed class SpeedMeterTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Feed <paramref name="ms"/> of straight movement at <paramref name="cps"/> cells/s, 33 ms ticks.</summary>
    private static DateTime Walk(SpeedMeter m, DateTime from, float cps, int ms, bool held, ref float x)
    {
        var t = from;
        for (var e = 0; e < ms; e += 33)
        {
            t = from.AddMilliseconds(e);
            x += cps * 0.033f;
            m.Push(new NumVec2(x, 0), t, held);
        }
        return t;
    }

    [Fact]
    public void Measures_speed_over_the_window()
    {
        var m = new SpeedMeter();
        var x = 0f;
        Walk(m, T0, 8f, 500, false, ref x);
        Assert.InRange(m.Speed, 7.5f, 8.5f);
        Assert.True(m.Moving);
        Assert.Equal("walking 8.0 c/s", m.Note.Replace("7.9", "8.0").Replace("8.1", "8.0"));
    }

    [Fact]
    public void Learns_walk_then_run_and_classifies()
    {
        var m = new SpeedMeter();
        var x = 0f;
        var t = Walk(m, T0, 8f, 1000, false, ref x);
        Assert.InRange(m.WalkSpeed, 7.5f, 8.5f);
        Assert.False(m.HasRun);
        Assert.False(m.Running);
        t = Walk(m, t.AddMilliseconds(33), 15f, 1500, true, ref x);
        Assert.True(m.HasRun);
        Assert.InRange(m.RunSpeed, 13f, 16f);
        Assert.True(m.Running);
        Assert.StartsWith("running", m.Note);
        Assert.False(m.RunKeyIgnored(t));
    }

    [Fact]
    public void Held_run_key_at_walking_speed_is_reported_ignored_after_settle()
    {
        var m = new SpeedMeter();
        var x = 0f;
        var t = Walk(m, T0, 8f, 1000, false, ref x);
        // Key goes down but the character keeps walking.
        t = Walk(m, t.AddMilliseconds(33), 8f, 300, true, ref x);
        Assert.False(m.RunKeyIgnored(t)); // still inside RunSettleMs
        t = Walk(m, t.AddMilliseconds(33), 8f, 500, true, ref x);
        Assert.True(m.RunKeyIgnored(t));
        Assert.False(m.HasRun); // walking speed never trains the run speed
        // Standing still with the key held is not "ignored" (nothing to compare).
        for (var i = 0; i < 20; i++) m.Push(new NumVec2(x, 0), t.AddMilliseconds(33 * (i + 1)), true);
        Assert.False(m.RunKeyIgnored(t.AddMilliseconds(660)));
    }

    [Fact]
    public void Never_ignored_before_a_walk_speed_is_known()
    {
        var m = new SpeedMeter();
        var x = 0f;
        var t = Walk(m, T0, 8f, 1500, true, ref x);
        Assert.False(m.RunKeyIgnored(t));
    }

    [Fact]
    public void Teleport_does_not_poison_the_speed()
    {
        var m = new SpeedMeter();
        var x = 0f;
        var t = Walk(m, T0, 8f, 500, false, ref x);
        m.Push(new NumVec2(x + 500f, 0), t.AddMilliseconds(33), false);
        Assert.InRange(m.Speed, 7f, 9f);
        Assert.InRange(m.WalkSpeed, 7f, 9f);
    }

    [Fact]
    public void Fixed_speeds_replace_learning()
    {
        var m = new SpeedMeter { FixedWalkSpeed = 10f, FixedRunSpeed = 20f };
        var x = 0f;
        var t = Walk(m, T0, 8f, 1000, false, ref x);
        Assert.Equal(10f, m.WalkSpeed);     // not overwritten by the measured 8
        Assert.Equal(20f, m.RunSpeed);
        Assert.True(m.WalkIsFixed);
        // Held key, still moving at 8 (< 10 × 1.2) → ignored.
        t = Walk(m, t.AddMilliseconds(33), 8f, 800, true, ref x);
        Assert.True(m.RunKeyIgnored(t));
        // Held key at 18 → running (above the 15 midpoint), not ignored.
        t = Walk(m, t.AddMilliseconds(33), 18f, 800, true, ref x);
        Assert.True(m.Running);
        Assert.False(m.RunKeyIgnored(t));
        // Clearing a fixed value falls back to what was learned meanwhile (nothing for run: it never trained).
        m.FixedRunSpeed = 0f;
        Assert.False(m.HasRun);
    }

    [Fact]
    public void Only_walk_fixed_still_learns_run()
    {
        var m = new SpeedMeter { FixedWalkSpeed = 8f };
        var x = 0f;
        var t = Walk(m, T0, 15f, 1500, true, ref x);
        Assert.True(m.HasRun);
        Assert.InRange(m.RunSpeed, 13f, 16f);
        Assert.False(m.RunIsFixed);
    }
}
