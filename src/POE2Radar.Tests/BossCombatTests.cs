using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Input;
using Xunit;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Tests;

public sealed class BossCombatTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly NavGrid Open = NavGrid.Build(Enumerable.Repeat((byte)1, 100 * 100).ToArray(), 100, 100);
    private static Poe2Live.EntityDot Boss(uint id = 10, int hp = 1000, Vector2? at = null)
        => new(id, 1, at ?? new(50, 50), default, Poe2Live.EntityCategory.Monster, "Boss", hp, 1000, false, 2, Poe2Live.Rarity.Unique, false);
    private static BossCombat.Snapshot S(int ms = 0, int hp = 100, Vector2? player = null, params Poe2Live.EntityDot[] enemies)
        => new(1, Now.AddMilliseconds(ms), true, true, player ?? new(30, 50),
            enemies.Length == 0 ? new[] { Boss() } : enemies, new(hp, 100, 100, 100, 0, 0), Open, new(35, 12, 12, 1800, 1200, 8000));

    [Fact] public void Normal_packs_and_melee_use_existing_mapping_policy()
    {
        var c = new BossCombat();
        Assert.False(c.Update(S(enemies: new[] { Boss() with { Rarity = Poe2Live.Rarity.Rare } })).Active);
        Assert.False(c.Update(S() with { Options = new(35, 0) }).Active);
        Assert.False(c.Update(S() with { Excluded = new uint[] { 10 } }).Active);
    }
    [Fact] public void Stable_range_opens_long_casts_without_periodic_dodges()
    {
        var c = new BossCombat();
        Assert.False(c.Update(S()).LongWindow);
        Assert.True(c.Update(S(1200)).LongWindow);
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(15000)).Intent);
    }
    [Fact] public void Movement_closes_long_window_until_settled_again()
    {
        var c = new BossCombat(); c.Update(S());
        Assert.True(c.Update(S(1200)).LongWindow);
        Assert.False(c.Update(S(1300, player: new(31,50))).LongWindow);
        Assert.False(c.Update(S(2200, player: new(31,50))).LongWindow);
        Assert.True(c.Update(S(2500, player: new(31,50))).LongWindow);
    }
    [Fact] public void Too_close_repositions_with_hysteresis_then_resumes_attacking()
    {
        var c = new BossCombat();
        Assert.Equal(BossCombat.Intent.Reposition, c.Update(S(player: new(40, 50))).Intent);
        Assert.Equal(BossCombat.Intent.Reposition, c.Update(S(100, player: new(36, 50))).Intent);
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(200, player: new(33, 50))).Intent);
        Assert.True(c.Update(S(1500, player: new(33, 50))).LongWindow);
    }
    [Fact] public void Damage_interrupts_once_then_dodge_gap_prevents_roll_spam()
    {
        var c = new BossCombat(); c.Update(S());
        var danger = c.Update(S(100, hp: 80));
        Assert.Equal(BossCombat.Intent.Dodge, danger.Intent); Assert.True(danger.Interrupt);
        c.DidDodge(Now.AddMilliseconds(100));
        Assert.False(c.Update(S(200, hp: 80)).Interrupt);
        var second = c.Update(S(300, hp: 60));
        Assert.Equal(BossCombat.Intent.Reposition, second.Intent);
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(1300, hp: 60)).Intent);
        Assert.True(c.Update(S(2500, hp: 60)).LongWindow);
    }
    [Fact] public void Damage_outside_observation_window_is_not_a_burst()
    {
        var c = new BossCombat(); c.Update(S());
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(1000, hp: 50)).Intent);
    }
    [Fact] public void Tiny_ranger_shield_and_mana_spending_do_not_trigger_dodge()
    {
        var c = new BossCombat();
        c.Update(S() with { Vitals = new(487,487,168,168,13,13) });
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(100) with { Vitals = new(487,487,50,168,0,13) }).Intent);
    }
    [Fact] public void Substantial_shield_loss_is_a_damage_signal()
    {
        var c = new BossCombat(); c.Update(S() with { Vitals = new(100,100,100,100,50,50) });
        Assert.Equal(BossCombat.Intent.Dodge, c.Update(S(100) with { Vitals = new(100,100,100,100,25,50) }).Intent);
    }
    [Fact] public void Missing_boss_waits_keeps_identity_and_reacquires_without_switching_to_add()
    {
        var c = new BossCombat(); c.Update(S());
        var missing = c.Update(S(500) with { Entities = new[] { Boss(11) with { Rarity = Poe2Live.Rarity.Normal } } });
        Assert.Equal(BossCombat.Intent.Wait, missing.Intent); Assert.Equal(10u, missing.TargetId); Assert.True(missing.Interrupt);
        Assert.False(c.Update(S(600) with { Entities = Array.Empty<Poe2Live.EntityDot>() }).Interrupt);
        Assert.Equal(10u, c.Update(S(1000)).TargetId);
        Assert.False(c.Update(S(9500) with { Entities = Array.Empty<Poe2Live.EntityDot>() }).Active);
    }
    [Fact] public void Unknown_life_is_not_death_but_observed_death_ends_encounter()
    {
        var c = new BossCombat(); c.Update(S());
        Assert.Equal(BossCombat.Intent.Wait, c.Update(S(100, enemies: new[] { Boss() with { HpMax = 0, HpCur = 0 } })).Intent);
        Assert.False(c.Update(S(200, enemies: new[] { Boss(hp: 0) })).Active);
    }
    [Fact] public void Zone_transition_and_disable_drop_old_encounter_and_damage_history()
    {
        var c = new BossCombat(); c.Update(S());
        Assert.False(c.Update(S(100, hp: 60) with { Area = 2, Entities = Array.Empty<Poe2Live.EntityDot>() }).Active);
        Assert.Equal(BossCombat.Intent.Attack, c.Update(S(200, hp: 60) with { Area = 2 }).Intent);
        Assert.False(c.Update(S(300) with { Enabled = false }).Active);
        Assert.Equal(0u, c.TargetId);
    }
    [Fact] public void Stale_world_and_unreadable_resources_suspend_input()
    {
        var c = new BossCombat(); c.Update(S());
        Assert.True(c.Update(S(100) with { Fresh = false }).Interrupt);
        Assert.Equal(BossCombat.Intent.Wait, c.Update(S(200) with { Vitals = null }).Intent);
        Assert.False(c.Update(S(300)).LongWindow);
    }
    [Fact] public void No_damage_uses_short_probes_without_declaring_immunity_or_forgetting_boss()
    {
        var c = new BossCombat(); c.Update(S()); c.DidAttack(Now.AddMilliseconds(3900));
        var wait = c.Update(S(4500));
        Assert.Equal(BossCombat.Intent.Wait, wait.Intent); Assert.Contains("unknown", wait.Note);
        var probe = c.Update(S(5500)); Assert.Equal(BossCombat.Intent.Attack, probe.Intent); Assert.False(probe.LongWindow);
        var damaged = c.Update(S(5600, enemies: new[] { Boss(hp: 900) }));
        Assert.Equal(BossCombat.Intent.Attack, damaged.Intent);
    }
    [Fact] public void Retreat_chooses_lateral_space_instead_of_backing_through_wall()
    {
        var grid = Enumerable.Repeat((byte)1, 10000).ToArray();
        for (var y = 0; y < 100; y++) for (var x = 0; x < 30; x++) grid[y * 100 + x] = 0;
        var nav = NavGrid.Build(grid,100,100);
        var player = new Vector2(32,50);
        Assert.True(BossCombat.TryPosition(nav, player, new(40,50), 16, 27, new[] { Boss(at: new(40,50)) }, out var point));
        Assert.True(point.X >= 31); Assert.True(MathF.Abs(point.Y - 50) > 3);
        Assert.True(BossCombat.ClearLine(nav, player, point));
    }
    [Fact] public void Full_roll_path_must_fit_even_when_short_reposition_is_clear()
    {
        Assert.False(BossCombat.SafeEnemyPath(new(30,50),new(70,50),new[] {Boss()}));
        Assert.True(BossCombat.SafeEnemyPath(new(40,50),new(10,50),new[] {Boss()}));
        var cells = new byte[10000];
        for (var y=25;y<75;y++) for(var x=25;x<75;x++) cells[y*100+x]=1;
        var nav=NavGrid.Build(cells,100,100);
        Assert.True(BossCombat.TryPosition(nav,new(50,50),new(65,50),16,27,new[] {Boss(at:new(65,50))},out _));
        Assert.False(BossCombat.TryPosition(nav,new(50,50),new(65,50),16,27,new[] {Boss(at:new(65,50))},out _,travelDistance:40));
        var c=new BossCombat();
        c.Update(S(player:new(50,50),enemies:new[] {Boss(at:new(65,50))}) with {Nav=nav});
        Assert.Equal(BossCombat.Intent.Reposition,c.Update(S(100,hp:80,player:new(50,50),enemies:new[] {Boss(at:new(65,50))}) with {Nav=nav}).Intent);
    }
    [Fact] public void No_terrain_never_sends_a_blind_retreat()
    {
        var c = new BossCombat();
        Assert.Equal(BossCombat.Intent.Wait, c.Update(S(player: new(40,50)) with { Nav = null }).Intent);
    }
    [Fact] public void Observed_oil_is_avoided_but_generic_effects_are_not_invented_hazards()
    {
        var oil = Boss(30) with { Category=Poe2Live.EntityCategory.Other,
            Metadata="Metadata/Effects/Spells/crossbow_oilgrenade/RudjaOilGround", Grid=new(30,50) };
        var c=new BossCombat(); var d=c.Update(S(enemies:new[] {Boss(),oil}));
        Assert.Equal(BossCombat.Intent.Reposition,d.Intent); Assert.True(d.Interrupt);
        Assert.Equal(0,BossCombat.HazardRisk(d.Destination,new[] {oil},6));
        Assert.False(BossCombat.SafeHazardPath(new(20,50),new(40,50),new[] {oil},6));
        Assert.False(BossCombat.IsKnownHazard(oil with {Metadata="Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect"}));
        Assert.False(c.Update(S(100,enemies:new[] {Boss(),oil})).Interrupt);
    }
    [Fact] public void Capacity_changes_do_not_look_like_damage_and_low_life_still_flees()
    {
        var c=new BossCombat();c.Update(S());
        Assert.NotEqual(BossCombat.Intent.Dodge,c.Update(S(100) with {Vitals=new(100,150,100,100,0,0)}).Intent);
        var low=c.Update(S(1000,hp:25)); Assert.Equal(BossCombat.Intent.Reposition,low.Intent);
        Assert.Contains("low life",low.Note);
        Assert.Equal(BossCombat.Intent.Reposition,c.Update(S(1100,hp:45)).Intent);
        Assert.Equal(BossCombat.Intent.Attack,c.Update(S(1200,hp:55)).Intent);
    }

    [Fact] public void Lifeless_arena_visuals_cannot_be_selected_as_attacks()
    {
        var blocker=Boss() with {HpMax=0,HpCur=0,Metadata="Metadata/Monsters/MudBurrower/Arena_Blocker_Visual"};
        Assert.False(CombatAssist.IsHostile(blocker));
        Assert.False(CombatAssist.TryPickTarget(new[]{blocker},new(30,50),35,out _));
    }

}
