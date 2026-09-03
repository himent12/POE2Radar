using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// One owner of the dodge key at a time: a roll is a fixed press + recovery lockout, never inside a cast's
/// animation window, direction from a WASD sign pair.
/// </summary>
public sealed class RollArbiterTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static RollArbiter New() => new() { PressMs = 60, RecoverMs = 650, DirHoldMs = 300 };

    [Fact]
    public void Grant_then_release_after_press_then_idle_after_recovery()
    {
        var a = New();
        Assert.True(a.TryRequest(RollArbiter.Owner.Mover, (1, 0), T0, out var why));
        Assert.Equal("roll (Mover)", why);
        Assert.True(a.KeyHeld);
        Assert.True(a.Busy);

        var c = a.Tick(T0.AddMilliseconds(30));
        Assert.False(c.Release);
        c = a.Tick(T0.AddMilliseconds(60));
        Assert.True(c.Release);
        Assert.Equal(RollArbiter.Owner.Mover, c.Owner);
        Assert.False(a.KeyHeld);
        Assert.Equal(RollArbiter.Phase.Recovering, a.State);

        // Release is reported exactly once.
        Assert.False(a.Tick(T0.AddMilliseconds(100)).Release);
        Assert.True(a.Busy);
        a.Tick(T0.AddMilliseconds(60 + 650));
        Assert.False(a.Busy);
        Assert.Equal(RollArbiter.Owner.None, a.Current);
    }

    [Fact]
    public void Second_owner_is_refused_while_pressed_or_recovering()
    {
        var a = New();
        Assert.True(a.TryRequest(RollArbiter.Owner.Mover, (1, 0), T0, out _));
        Assert.False(a.TryRequest(RollArbiter.Owner.Combo, (-1, 0), T0.AddMilliseconds(10), out var why));
        Assert.Contains("in progress", why);
        a.Tick(T0.AddMilliseconds(60));
        Assert.False(a.TryRequest(RollArbiter.Owner.Combo, (-1, 0), T0.AddMilliseconds(200), out why));
        Assert.Contains("recovery", why);
        a.Tick(T0.AddMilliseconds(710));
        Assert.True(a.TryRequest(RollArbiter.Owner.Combo, (-1, 0), T0.AddMilliseconds(710), out _));
        Assert.Equal((-1, 0), a.Direction);
    }

    [Fact]
    public void No_roll_inside_a_cast_window()
    {
        var a = New();
        a.NoteCast(T0, 330);
        Assert.False(a.TryRequest(RollArbiter.Owner.Combo, (0, 1), T0.AddMilliseconds(200), out var why));
        Assert.Contains("cast animation", why);
        Assert.True(a.InCast(T0.AddMilliseconds(329)));
        Assert.True(a.TryRequest(RollArbiter.Owner.Combo, (0, 1), T0.AddMilliseconds(330), out _));
    }

    [Fact]
    public void Overlapping_casts_keep_the_later_window()
    {
        var a = New();
        a.NoteCast(T0, 500);
        a.NoteCast(T0.AddMilliseconds(100), 100); // ends earlier → ignored
        Assert.True(a.InCast(T0.AddMilliseconds(450)));
        Assert.False(a.InCast(T0.AddMilliseconds(500)));
    }

    [Fact]
    public void Combo_and_boss_rolls_hold_direction_keys_mover_does_not()
    {
        var a = New();
        Assert.True(a.TryRequest(RollArbiter.Owner.Combo, (0, -1), T0, out _));
        a.Tick(T0.AddMilliseconds(60));
        Assert.False(a.Tick(T0.AddMilliseconds(200)).ReleaseDir);
        var c = a.Tick(T0.AddMilliseconds(300));
        Assert.True(c.ReleaseDir);
        Assert.Equal((0, -1), c.Dir);
        Assert.False(a.Tick(T0.AddMilliseconds(400)).ReleaseDir); // once

        var m = New();
        Assert.True(m.TryRequest(RollArbiter.Owner.Mover, (0, -1), T0, out _));
        m.Tick(T0.AddMilliseconds(60));
        Assert.False(m.Tick(T0.AddMilliseconds(300)).ReleaseDir); // the mover already holds its WASD
    }

    [Fact]
    public void Reset_drops_owner_and_cast_window()
    {
        var a = New();
        a.NoteCast(T0, 1000);
        Assert.True(a.TryRequest(RollArbiter.Owner.Boss, (1, 1), T0.AddSeconds(1), out _));
        a.Reset();
        Assert.False(a.Busy);
        Assert.False(a.InCast(T0.AddMilliseconds(500)));
        Assert.Equal("", a.Note(T0));
    }

    [Fact]
    public void Note_explains_the_wait()
    {
        var a = New();
        a.TryRequest(RollArbiter.Owner.Flee, (1, 0), T0, out _);
        Assert.Equal("rolling (Flee)", a.Note(T0.AddMilliseconds(10)));
        a.Tick(T0.AddMilliseconds(60));
        Assert.StartsWith("roll recovery", a.Note(T0.AddMilliseconds(100)));
        Assert.Contains("(Flee)", a.Note(T0.AddMilliseconds(100)));
    }

    [Fact]
    public void AwayFrom_gives_an_8way_sign_pair()
    {
        Assert.Equal((-1, 0), RollArbiter.AwayFrom(new NumVec2(0, 0), new NumVec2(5, 0)));
        Assert.Equal((0, 1), RollArbiter.AwayFrom(new NumVec2(0, 5), new NumVec2(0, 0)));
        Assert.Equal((-1, -1), RollArbiter.AwayFrom(new NumVec2(0, 0), new NumVec2(4, 4)));
        Assert.Equal((1, 0), RollArbiter.AwayFrom(new NumVec2(3, 3), new NumVec2(3, 3)));
        // Shallow angle: rounds to the dominant axis rather than (0,0).
        Assert.Equal((-1, 0), RollArbiter.AwayFrom(new NumVec2(0, 0), new NumVec2(10, 1)));
    }

    // ── Run hold (key kept down to chain rolls) ──

    [Fact]
    public void Hold_is_granted_when_idle_and_is_not_busy()
    {
        var a = New();
        Assert.True(a.TryHold(RollArbiter.Owner.Mover, T0, out var why));
        Assert.Equal("run (Mover)", why);
        Assert.True(a.Holding);
        Assert.True(a.KeyHeld);
        Assert.False(a.Busy); // casts may still fire while running
        Assert.True(a.TryHold(RollArbiter.Owner.Mover, T0.AddSeconds(1), out _)); // idempotent for the same owner
        Assert.Equal(TimeSpan.FromSeconds(1), a.HeldFor(T0.AddSeconds(1)));
        Assert.False(a.Tick(T0.AddSeconds(5)).Release); // no timer ends a hold
        Assert.True(a.Holding);
    }

    [Fact]
    public void Tap_owner_must_release_the_hold_first_then_wait_the_gap()
    {
        var a = New();
        a.TryHold(RollArbiter.Owner.Mover, T0, out _);
        Assert.False(a.TryRequest(RollArbiter.Owner.Combo, (1, 0), T0.AddSeconds(1), out var why));
        Assert.Contains("release first", why);
        Assert.True(a.ReleaseHold(T0.AddSeconds(1)));
        Assert.False(a.ReleaseHold(T0.AddSeconds(1)));
        Assert.False(a.TryRequest(RollArbiter.Owner.Combo, (1, 0), T0.AddSeconds(1.02), out why)); // < MinGap
        Assert.Equal("key just released", why);
        Assert.True(a.TryRequest(RollArbiter.Owner.Combo, (1, 0), T0.AddSeconds(1.05), out _));
    }

    [Fact]
    public void Hold_refused_during_tap_recovery_cast_window_and_by_another_owner()
    {
        var a = New();
        a.TryRequest(RollArbiter.Owner.Combo, (1, 0), T0, out _);
        Assert.False(a.TryHold(RollArbiter.Owner.Mover, T0.AddMilliseconds(100), out var why));
        Assert.Contains("roll in flight", why);
        a.Tick(T0.AddMilliseconds(60)); a.Tick(T0.AddMilliseconds(710));
        a.NoteCast(T0.AddMilliseconds(710), 300);
        Assert.False(a.TryHold(RollArbiter.Owner.Mover, T0.AddMilliseconds(800), out why));
        Assert.Contains("cast animation", why);
        Assert.True(a.TryHold(RollArbiter.Owner.Flee, T0.AddMilliseconds(1100), out _));
        Assert.False(a.TryHold(RollArbiter.Owner.Mover, T0.AddMilliseconds(1200), out why));
        Assert.Contains("held by Flee", why);
    }

    [Fact]
    public void Tap_release_also_starts_the_gap_clock()
    {
        var a = New(); a.RecoverMs = 0;
        a.TryRequest(RollArbiter.Owner.Combo, (1, 0), T0, out _);
        a.Tick(T0.AddMilliseconds(60)); // release
        a.Tick(T0.AddMilliseconds(61)); // recovery (0 ms) over
        Assert.False(a.Busy);
        Assert.False(a.TryHold(RollArbiter.Owner.Mover, T0.AddMilliseconds(80), out var why));
        Assert.Equal("key just released", why);
        Assert.True(a.TryHold(RollArbiter.Owner.Mover, T0.AddMilliseconds(120), out _));
    }
}
