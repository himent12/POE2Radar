using POE2Radar.Core.Game;
using POE2Radar.Overlay.Input;
using NumVec2 = System.Numerics.Vector2;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// The fight watchdog is what stops the bot from standing still beside 1–2 mobs it is not hitting:
/// movement is paused only while an engaged hostile is losing HP; otherwise those mobs are ignored
/// for a while and the bot moves on.
/// </summary>
public sealed class CombatWatchTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Poe2Live.EntityDot Mob(uint id, float x, float y, int hp = 100, int hpMax = 100, byte reaction = 0)
        => new(id, 0, new NumVec2(x, y), default, Poe2Live.EntityCategory.Monster, "Metadata/Monsters/Test",
            hp, hpMax, false, reaction, Poe2Live.Rarity.Normal, false);

    private static CombatWatch Watch() => new()
    {
        StallAfter = TimeSpan.FromSeconds(4),
        IgnoreFor = TimeSpan.FromSeconds(20),
    };

    [Fact]
    public void No_hostiles_never_pauses()
    {
        var w = Watch();
        var r = w.Update([Mob(1, 3, 0, reaction: 1)], NumVec2.Zero, 20, T0);
        Assert.False(r.PauseMove);
        Assert.False(r.Fighting);
    }

    [Fact]
    public void Hostile_outside_engage_range_does_not_pause()
    {
        var w = Watch();
        var r = w.Update([Mob(1, 30, 0)], NumVec2.Zero, 20, T0);
        Assert.False(r.PauseMove);
    }

    [Fact]
    public void Hostile_in_engage_range_pauses_while_damaged()
    {
        var w = Watch();
        Assert.True(w.Update([Mob(1, 5, 0, hp: 100)], NumVec2.Zero, 20, T0).PauseMove);
        Assert.True(w.Update([Mob(1, 5, 0, hp: 80)], NumVec2.Zero, 20, T0.AddSeconds(3)).PauseMove);
        // Damage at t=3 resets the stall clock: still fighting at t=6.
        Assert.True(w.Update([Mob(1, 5, 0, hp: 60)], NumVec2.Zero, 20, T0.AddSeconds(6)).PauseMove);
        Assert.True(w.Update([Mob(1, 5, 0, hp: 60)], NumVec2.Zero, 20, T0.AddSeconds(9)).PauseMove);
    }

    [Fact]
    public void Kill_with_pack_still_in_attack_range_holds_the_mover_for_the_retarget_grace()
    {
        // Engaged mob at 5 dies; another hostile sits at 30 — outside engage (20) but inside attack range (40).
        // The mover must stay paused for the grace so the rotation re-aims instead of a run/roll press
        // breaking the combo; after the grace (mob still far) movement resumes; with no attack range given
        // (legacy callers) or no other hostile, it clears at once.
        var w = Watch();
        Assert.True(w.Update([Mob(1, 5, 0), Mob(2, 30, 0)], NumVec2.Zero, 20, T0, attackRange: 40).PauseMove);
        var r = w.Update([Mob(1, 5, 0, hp: 0), Mob(2, 30, 0)], NumVec2.Zero, 20, T0.AddMilliseconds(100), attackRange: 40);
        Assert.True(r.PauseMove);
        Assert.Equal("retargeting", r.Note);
        Assert.True(w.Update([Mob(2, 30, 0)], NumVec2.Zero, 20, T0.AddMilliseconds(500), attackRange: 40).PauseMove);
        Assert.False(w.Update([Mob(2, 30, 0)], NumVec2.Zero, 20, T0.AddMilliseconds(1200), attackRange: 40).PauseMove);

        var w2 = Watch();
        Assert.True(w2.Update([Mob(1, 5, 0), Mob(2, 30, 0)], NumVec2.Zero, 20, T0).PauseMove);
        Assert.False(w2.Update([Mob(2, 30, 0)], NumVec2.Zero, 20, T0.AddMilliseconds(100)).PauseMove);

        var w3 = Watch();
        Assert.True(w3.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0, attackRange: 40).PauseMove);
        Assert.False(w3.Update([Mob(1, 5, 0, hp: 0)], NumVec2.Zero, 20, T0.AddMilliseconds(100), attackRange: 40).PauseMove);
    }

    [Fact]
    public void No_damage_for_stall_window_gives_up_and_ignores_mob()
    {
        var w = Watch();
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0).PauseMove);
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(2)).PauseMove);
        var r = w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(4));
        Assert.False(r.PauseMove);
        Assert.True(r.Stalled);
        Assert.True(w.IsIgnored(1));
        Assert.Contains(1u, w.IgnoredIds);
        // Still ignored → movement free even though it is right there.
        Assert.False(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(10)).PauseMove);
        // Ignore expires → engaged again.
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(25)).PauseMove);
        Assert.False(w.IsIgnored(1));
    }

    [Fact]
    public void Kill_counts_as_progress()
    {
        var w = Watch();
        Assert.True(w.Update([Mob(1, 5, 0), Mob(2, 6, 0)], NumVec2.Zero, 20, T0).PauseMove);
        // Mob 1 dies at t=3.5 → progress → the clock restarts, mob 2 still engaged at t=5.
        Assert.True(w.Update([Mob(1, 5, 0, hp: 0), Mob(2, 6, 0)], NumVec2.Zero, 20, T0.AddSeconds(3.5)).PauseMove);
        Assert.True(w.Update([Mob(2, 6, 0)], NumVec2.Zero, 20, T0.AddSeconds(5)).PauseMove);
    }

    [Fact]
    public void Lifeless_monster_never_holds_the_bot()
    {
        var w = Watch();
        // Arena blockers are categorized Monster but have no Life: never acquire them as combat targets.
        Assert.False(w.Update([Mob(7, 2, 0, hp: 0, hpMax: 0)], NumVec2.Zero, 20, T0).PauseMove);
        var r = w.Update([Mob(7, 2, 0, hp: 0, hpMax: 0)], NumVec2.Zero, 20, T0.AddSeconds(4));
        Assert.False(r.PauseMove);
        Assert.False(w.IsIgnored(7));
    }

    [Fact]
    public void Leaving_combat_resets_stall_clock()
    {
        var w = Watch();
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0).PauseMove);
        Assert.False(w.Update([], NumVec2.Zero, 20, T0.AddSeconds(3)).PauseMove);
        // Re-engage at t=3.5: fresh fight → not stalled at t=6 (only 2.5s in).
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(3.5)).PauseMove);
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(6)).PauseMove);
    }

    [Fact]
    public void Default_never_gives_up_keeps_attacking()
    {
        var w = new CombatWatch(); // StallAfter = 0 → stand and fight
        Assert.True(w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0).PauseMove);
        var r = w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(60));
        Assert.True(r.PauseMove);
        Assert.True(r.Fighting);
        Assert.False(r.Stalled);
        Assert.False(w.IsIgnored(1));
    }

    [Fact]
    public void Low_hp_flees_until_recovered()
    {
        var w = new CombatWatch { FleeBelowPct = 35, FleeRecoverPct = 60 };
        var mobs = new[] { Mob(1, 5, 0) };
        Assert.True(w.Update(mobs, NumVec2.Zero, 20, T0, playerHpPct: 80).PauseMove);
        var low = w.Update(mobs, NumVec2.Zero, 20, T0.AddSeconds(1), playerHpPct: 30);
        Assert.True(low.Flee);
        Assert.False(low.PauseMove);
        Assert.True(w.IsFleeing);
        // Hysteresis: 50% is above the flee line but below recover → still running.
        Assert.True(w.Update(mobs, NumVec2.Zero, 20, T0.AddSeconds(2), playerHpPct: 50).Flee);
        // Recovered → back to fighting.
        var back = w.Update(mobs, NumVec2.Zero, 20, T0.AddSeconds(3), playerHpPct: 65);
        Assert.False(back.Flee);
        Assert.True(back.PauseMove);
    }

    [Fact]
    public void Flee_disabled_when_threshold_zero()
    {
        var w = new CombatWatch { FleeBelowPct = 0 };
        var r = w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0, playerHpPct: 5);
        Assert.False(r.Flee);
        Assert.True(r.PauseMove);
    }

    [Fact]
    public void Flee_ends_when_nothing_in_range()
    {
        var w = new CombatWatch();
        w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0, playerHpPct: 10);
        Assert.True(w.IsFleeing);
        Assert.False(w.Update([], NumVec2.Zero, 20, T0.AddSeconds(1), playerHpPct: 10).Flee);
        Assert.False(w.IsFleeing);
    }

    [Fact]
    public void FleePoint_runs_away_from_pack()
    {
        // Pack to the −X side → flee toward +X.
        var mobs = new[] { Mob(1, 8, 20), Mob(2, 10, 22) };
        Assert.True(CombatWatch.TryFleePoint(mobs, new NumVec2(20, 20), 35, 10, null, 0, 0, out var p));
        Assert.True(p.X > 20 + 8, $"flee x={p.X}");
        Assert.True(NumVec2.Distance(p, new NumVec2(20, 20)) > 9f);
    }

    [Fact]
    public void FleePoint_respects_walls()
    {
        // 40x40 grid; wall at x<=10 blocks the straight "away" direction from a pack on the +X side.
        var walk = new byte[40 * 40];
        Array.Fill(walk, (byte)1);
        for (var y = 0; y < 40; y++) for (var x = 0; x <= 10; x++) walk[y * 40 + x] = 0;
        var mobs = new[] { Mob(1, 25, 20) };
        Assert.True(CombatWatch.TryFleePoint(mobs, new NumVec2(14, 20), 35, 20, walk, 40, 40, out var p));
        Assert.True(walk[(int)MathF.Round(p.Y) * 40 + (int)MathF.Round(p.X)] == 1, $"flee point ({p.X},{p.Y}) not walkable");
        Assert.True(p.X <= 25, "ran into the mob");
        Assert.True(NumVec2.Distance(p, new NumVec2(14, 20)) >= 4f);
    }

    [Fact]
    public void FleePoint_false_without_threats()
    {
        Assert.False(CombatWatch.TryFleePoint([Mob(1, 90, 90)], NumVec2.Zero, 35, 20, null, 0, 0, out _));
        Assert.False(CombatWatch.TryFleePoint([], NumVec2.Zero, 35, 20, null, 0, 0, out _));
    }

    [Fact]
    public void Kites_when_hostile_inside_keep_distance()
    {
        var w = new CombatWatch { KeepDistance = 8 };
        var r = w.Update([Mob(1, 4, 0)], NumVec2.Zero, 20, T0);
        Assert.True(r.Kite);
        Assert.False(r.PauseMove);
        Assert.True(r.Fighting);
        r = w.Update([Mob(1, 12, 0)], NumVec2.Zero, 20, T0.AddSeconds(1));
        Assert.False(r.Kite);
        Assert.True(r.PauseMove);
    }

    [Fact]
    public void Reset_clears_ignores()
    {
        var w = Watch();
        w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0);
        w.Update([Mob(1, 5, 0)], NumVec2.Zero, 20, T0.AddSeconds(4));
        Assert.True(w.IsIgnored(1));
        w.Reset();
        Assert.False(w.IsIgnored(1));
        Assert.Empty(w.IgnoredIds);
    }
}
