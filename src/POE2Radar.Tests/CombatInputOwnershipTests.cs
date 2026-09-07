using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class CombatInputOwnershipTests
{
    [Fact]
    public void Modifier_chord_brackets_the_cast_and_releases_before_recovery()
    {
        var app = App();
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 700, Modifiers: 3),
            new CombatAssist.Decision(true, 81, 0, 0, "cast"), Now);
        var steps = ((IEnumerable)Field("_macro").GetValue(app)!).Cast<ITuple>().ToArray();
        Assert.Equal(new[] { "Aim", "ModifierDown", "ModifierDown", "KeyDown", "KeyUp", "KeyUp", "KeyUp", "Done" },
            steps.Select(s => s[0]!.ToString()));
        Assert.Equal(new ushort[] { 17, 16, 81, 81, 16, 17 }, steps.Skip(1).Take(6).Select(s => (ushort)s[1]!));
    }

    [Fact]
    public void Modified_and_unmodified_keys_have_independent_cooldowns()
    {
        var app = App();
        Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt")[81 | (2 << 8)] = Now;
        Call(app, "EnsureCombatClocks", new List<CombatSkill> { new() { Key = 81 }, new() { Key = 81, Modifiers = 2 } });
        Assert.Equal(new[] { DateTime.MinValue, Now }, Get<DateTime[]>(app, "_combatFiredAt"));
    }

    [Fact]
    public void Boss_openings_filter_snipe_and_buffs_without_overwriting_manual_timing()
    {
        var app = App();
        Field("_bossDecision").SetValue(app, new BossCombat.Decision(BossCombat.Intent.Attack, 10));
        var source = new CombatSkill { Name = "Snipe", HoldMs = 2100, Repeat = 3 };
        var channel = new CombatAssist.Skill(69, 0, HoldMs: 2100, Repeat: 3);
        CombatAssist.Skill Filter(CombatAssist.Skill skill) => (CombatAssist.Skill)Call(app, "BossSkill", skill, source, default(System.Numerics.Vector2))!;
        Assert.False(Filter(channel).Enabled);
        Assert.Equal(1, Filter(channel with { HoldMs = 0, RepeatGapMs = 150 }).Repeat);
        Field("_bossDecision").SetValue(app, new BossCombat.Decision(BossCombat.Intent.Attack, 10, LongWindow: true));
        Assert.True(Filter(channel).Enabled);
        Assert.Equal(3, Filter(channel).Repeat);
        Field("_bossDecision").SetValue(app, new BossCombat.Decision(BossCombat.Intent.Reposition, 10));
        Assert.False(Filter(channel with { HoldMs = 0, Priority = true }).Enabled);
        Assert.True(Filter(channel with { Priority = true, HpBelowPct = 35 }).Enabled);
        Field("_bossDecision").SetValue(app, new BossCombat.Decision(BossCombat.Intent.Dodge, 10));
        Assert.False(Filter(channel with { Priority = true, HpBelowPct = 35 }).Enabled);
        Assert.Equal(2100, source.HoldMs); Assert.Equal(3, source.Repeat);
    }

    [Fact]
    public void Escape_shot_requires_a_verified_backward_landing()
    {
        var app = App();
        Field("_bossDecision").SetValue(app, new BossCombat.Decision(BossCombat.Intent.Attack, 10, LongWindow: true));
        var boss = new Poe2Live.EntityDot(10, 1, new(50,50), default, Poe2Live.EntityCategory.Monster, "Boss", 100,100,false,2,Poe2Live.Rarity.Unique,false);
        Field("_bossEntities").SetValue(app, new[] { boss });
        var nav = POE2Radar.Core.Pathfinding.NavGrid.Build(Enumerable.Repeat((byte)1,10000).ToArray(),100,100);
        Field("_bossNav").SetValue(app, nav);
        var source = new CombatSkill { Name = "Escape Shot" };
        var skill = new CombatAssist.Skill(81, 0, AimMode: "Target");
        CombatAssist.Skill Filter(System.Numerics.Vector2 player, CombatAssist.Skill value) => (CombatAssist.Skill)Call(app,"BossSkill",value,source,player)!;
        Assert.True(Filter(new(30,50),skill).Enabled);
        Assert.False(Filter(new(5,50),skill).Enabled); // backward jump would cross arena edge
        Assert.False(Filter(new(30,50),skill with { AimMode = "Away" }).Enabled);
        Field("_bossNav").SetValue(app, null);
        Assert.False(Filter(new(30,50),skill).Enabled);
    }

    private static readonly DateTime Now = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // No live client, window, or input dispatch. Scheduling and empty-queue recovery only.
    private static RadarApp App()
    {
        var app = (RadarApp)RuntimeHelpers.GetUninitializedObject(typeof(RadarApp));
        Field("_settings").SetValue(app, new RadarSettings { CombatTapHoldMs = 60 });
        foreach (var name in new[] { "_macro", "_macroHeld", "_heldKeys", "_combatKeyFiredAt", "_combatWatch", "_imprisonedIds" })
            Field(name).SetValue(app, Activator.CreateInstance(Field(name).FieldType));
        Field("_combatFiredAt").SetValue(app, Array.Empty<DateTime>());
        return app;
    }
    private static FieldInfo Field(string name) => typeof(RadarApp).GetField(name, Private)!;
    private static T Get<T>(RadarApp app, string name) => (T)Field(name).GetValue(app)!;
    private static object? Call(RadarApp app, string method, params object?[] args)
        => typeof(RadarApp).GetMethod(method, Private)!.Invoke(app, args);
    private static bool TickEmptyMacro(RadarApp app, DateTime now)
        => (bool)Call(app, "RunComboMacro", now, default(System.Numerics.Vector2), null, Array.Empty<Poe2Live.EntityDot>())!;
    private static PathMove.Decision Flee(RadarApp app, string method)
        => PathMove.Decide(new(true, true, true, default, new[] { (-20, -20) }, 2, Now,
            DateTime.MinValue, 0, method, 87, 65, 83, 68, 1, PauseForCombat: Get<bool>(app, "_comboBusy")));

    [Theory]
    [InlineData("WASD")]
    [InlineData("Click")]
    public void Simple_priority_cast_owns_input_immediately_and_through_recovery(string method)
    {
        var app = App();
        var skill = new CombatAssist.Skill(81, 5000, Priority: true, RepeatGapMs: 330);
        Assert.True(Flee(app, method).Moving);
        Call(app, "ClaimCombatInput", skill, Now);
        Assert.True(Get<bool>(app, "_comboBusy"));
        Assert.True(Get<bool>(app, "_macroPriority"));
        Assert.False(Flee(app, method).Moving);
        Assert.False(Flee(app, method).ShouldTap);
        Assert.Empty(Flee(app, method).HoldKeys ?? Array.Empty<ushort>());
        Assert.True(TickEmptyMacro(app, Now.AddMilliseconds(329)));
        Assert.False(Flee(app, method).Moving);
        Assert.False(TickEmptyMacro(app, Now.AddMilliseconds(330)));
        Assert.True(Flee(app, method).Moving);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(1, 1000)]
    public void Macro_claims_input_before_its_first_queued_step(int repeat, int hold)
    {
        var app = App();
        var skill = new CombatAssist.Skill(81, 5000, Repeat: repeat, HoldMs: hold, Priority: true);
        var decision = new CombatAssist.Decision(true, 81, 0, 0, "priority", HasTarget: true);
        Call(app, "StartComboMacro", skill, decision, Now);
        Assert.True(Get<bool>(app, "_comboBusy"));
        Assert.False(Flee(app, "Click").ShouldTap);
        Assert.NotEmpty(((IEnumerable)Field("_macro").GetValue(app)!).Cast<object>());
        Assert.Empty(Get<List<ushort>>(app, "_macroHeld"));
    }

    [Theory]
    [InlineData(0, 330, 200, 470)]
    [InlineData(1000, 330, 200, 200)]
    [InlineData(0, 0, 0, 0)]
    public void Final_macro_step_waits_for_cast_recovery_and_then_delay(int hold, int interval, int delay, int expected)
    {
        var app = App();
        var skill = new CombatAssist.Skill(81, 5000, Repeat: 2, HoldMs: hold, RepeatGapMs: interval, NextDelayMs: delay, Priority: true);
        Call(app, "StartComboMacro", skill, new CombatAssist.Decision(true, 81, 0, 0, "priority"), Now);
        var last = (ITuple)((IEnumerable)Field("_macro").GetValue(app)!).Cast<object>().Last();
        Assert.Equal("Done", last[0]!.ToString());
        Assert.Equal(TimeSpan.FromMilliseconds(expected), (TimeSpan)last[2]!);
    }

    [Fact]
    public void Abort_clears_movement_lock_and_priority_ownership()
    {
        var app = App();
        Call(app, "ClaimCombatInput", new CombatAssist.Skill(81, 5000, Priority: true), Now);
        Call(app, "AbortComboMacro");
        Assert.False(Get<bool>(app, "_comboBusy"));
        Assert.False(Get<bool>(app, "_macroPriority"));
        Assert.Equal(DateTime.MinValue, Get<DateTime>(app, "_comboLockUntil"));
        Assert.False(TickEmptyMacro(app, Now));
        Assert.True(Flee(app, "WASD").Moving);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Scheduling_then_abort_does_not_spend_cooldown_or_advance_rotation(int repeat)
    {
        var app = App();
        var clocks = Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt");
        var prior = Now.AddSeconds(-10);
        clocks[81] = prior;
        Field("_combatNextIndex").SetValue(app, 1);
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000, Repeat: repeat),
            new CombatAssist.Decision(true, 81, 1, 2, "scheduled"), Now);
        Assert.Equal(prior, clocks[81]);
        Assert.Equal(1, Get<int>(app, "_combatNextIndex"));
        Call(app, "AbortComboMacro");
        Assert.Equal(prior, clocks[81]);
        Assert.Equal(1, Get<int>(app, "_combatNextIndex"));
        Assert.False(Get<bool>(app, "_macroCastPending"));
    }

    [Fact]
    public void First_dispatched_press_commits_clock_at_press_time_only_once()
    {
        var app = App();
        var clocks = Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt");
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000, Repeat: 3, AimMode: "Cursor"),
            new CombatAssist.Decision(true, 81, 0, 1, "scheduled"), Now);
        var pressedAt = Now.AddMilliseconds(70);
        var calls = 0;
        Action<ushort> send = key => { Assert.Equal((ushort)81, key); if (calls++ == 0) Assert.Empty(clocks); };
        Assert.True((bool)Call(app, "DispatchCombatKeyDown", (ushort)81, pressedAt, send)!);
        Assert.Equal(pressedAt, clocks[81]);
        Assert.Equal(1, Get<int>(app, "_combatNextIndex"));
        Assert.True((bool)Call(app, "DispatchCombatKeyDown", (ushort)81, pressedAt.AddMilliseconds(300), send)!);
        Assert.Equal(2, calls);
        Assert.Equal(pressedAt, clocks[81]);
        // Fake input: release the bookkeeping only, never invoke native KeyUp.
        Get<List<ushort>>(app, "_macroHeld").Clear();
        Call(app, "AbortComboMacro");
        Assert.Equal(pressedAt, clocks[81]);
    }

    [Fact]
    public void Dispatch_exception_does_not_charge_cooldown()
    {
        var app = App();
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000),
            new CombatAssist.Decision(true, 81, 0, 1, "scheduled"), Now);
        Action<ushort> fail = _ => throw new InvalidOperationException("input failed");
        Assert.Throws<TargetInvocationException>(() => Call(app, "DispatchCombatKeyDown", (ushort)81, Now.AddMilliseconds(60), fail));
        Assert.Empty(Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt"));
        Assert.Empty(Get<List<ushort>>(app, "_macroHeld"));
        Assert.Equal(0, Get<int>(app, "_combatNextIndex"));
    }

    [Fact]
    public void Target_disappearing_before_aim_cancels_without_cooldown()
    {
        var app = App();
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000),
            new CombatAssist.Decision(true, 81, 0, 1, "scheduled", HasTarget: true, TargetId: 10), Now);
        Assert.False(TickEmptyMacro(app, Now.AddMilliseconds(60)));
        Assert.Empty(Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt"));
        Assert.False(Get<bool>(app, "_comboBusy"));
    }

    [Fact]
    public void Zone_change_cancels_even_cursor_only_queued_casts()
    {
        var app = App();
        Field("_areaHash").SetValue(app, (uint)100);
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000, AimMode: "Cursor"),
            new CombatAssist.Decision(true, 81, 0, 1, "scheduled"), Now);
        Field("_areaHash").SetValue(app, (uint)101);
        Assert.False(TickEmptyMacro(app, Now.AddMilliseconds(60)));
        Assert.Empty(Get<Dictionary<int, DateTime>>(app, "_combatKeyFiredAt"));
        Assert.False(Get<bool>(app, "_macroCastPending"));
    }

    [Fact]
    public void Reordered_and_duplicate_slots_use_the_dispatched_key_clock()
    {
        var app = App();
        Call(app, "StartComboMacro", new CombatAssist.Skill(81, 5000),
            new CombatAssist.Decision(true, 81, 0, 1, "scheduled"), Now);
        var pressedAt = Now.AddMilliseconds(60);
        Call(app, "DispatchCombatKeyDown", (ushort)81, pressedAt, (Action<ushort>)(_ => { }));
        Call(app, "EnsureCombatClocks", (object)new[] { new CombatSkill { Key = 82 }, new CombatSkill { Key = 81 }, new CombatSkill { Key = 81 } });
        Assert.Equal(new[] { DateTime.MinValue, pressedAt, pressedAt }, Get<DateTime[]>(app, "_combatFiredAt"));
    }
    [Fact]
    public void Interrupted_held_channel_releases_key_and_modifiers_then_allows_movement()
    {
        var app = App(); var held = new HashSet<ushort>();
        app.CombatKeyDown = k => Assert.True(held.Add(k));
        app.CombatKeyUp = k => Assert.True(held.Remove(k));
        Call(app, "StartComboMacro", new CombatAssist.Skill(69, 2000, HoldMs: 1200, Modifiers: 2, AimMode: "Cursor"),
            new CombatAssist.Decision(true, 69, 0, 1, "channel"), Now);
        TickEmptyMacro(app, Now); TickEmptyMacro(app, Now.AddMilliseconds(10));
        Assert.Contains((ushort)69, held); Assert.Contains((ushort)17, held);
        Call(app, "AbortComboMacro");
        Assert.Empty(held); Assert.True(Flee(app, "Click").Moving);
        Assert.False(TickEmptyMacro(app, Now.AddSeconds(2)));
        Assert.Equal(Now.AddMilliseconds(10), Get<Dictionary<int,DateTime>>(app, "_combatKeyFiredAt")[69 | (2 << 8)]);
    }

    [Fact]
    public void Manual_binding_edit_aborts_real_channel_and_releases_old_key()
    {
        var app = App(); var held = new HashSet<ushort>();
        app.CombatKeyDown = k => held.Add(k); app.CombatKeyUp = k => held.Remove(k);
        var skills = new[] { new CombatSkill { Key = 69, HoldMs = 1200 } };
        Call(app, "SyncCombatSettings", (object)skills);
        Call(app, "StartComboMacro", new CombatAssist.Skill(69,2000,HoldMs:1200,AimMode:"Cursor"),
            new CombatAssist.Decision(true,69,0,1,"cast"),Now);
        TickEmptyMacro(app,Now); Assert.Contains((ushort)69,held);
        skills[0].Key = 82; Call(app,"SyncCombatSettings",(object)skills);
        Assert.Empty(held); Assert.False(Get<bool>(app,"_comboBusy"));
    }

    [Fact]
    public void Boss_target_loss_interrupts_held_channel_without_retargeting_an_add()
    {
        var app = App(); var held = new HashSet<ushort>();
        app.CombatKeyDown = k => held.Add(k); app.CombatKeyUp = k => held.Remove(k);
        Call(app,"StartComboMacro",new CombatAssist.Skill(69,2000,HoldMs:1200,AimMode:"Cursor"),
            new CombatAssist.Decision(true,69,0,1,"cast",TargetId:10),Now);
        TickEmptyMacro(app,Now);
        Field("_macroAimMode").SetValue(app,"Target");
        Field("_bossDecision").SetValue(app,new BossCombat.Decision(BossCombat.Intent.Wait,10));
        Assert.False(TickEmptyMacro(app,Now.AddMilliseconds(100))); Assert.Empty(held);
    }

    [Fact]
    public void Claiming_cast_releases_movement_and_clears_run_linger()
    {
        var app=App(); var released=new List<ushort>(); app.CombatKeyUp=released.Add;
        Get<HashSet<ushort>>(app,"_heldKeys").UnionWith(new ushort[] {87,32});
        Field("_runHoldUntil").SetValue(app,Now.AddSeconds(1));
        Call(app,"ClaimCombatInput",new CombatAssist.Skill(69,2000),Now);
        Assert.Contains((ushort)87,released); Assert.Contains((ushort)32,released);
        Assert.Empty(Get<HashSet<ushort>>(app,"_heldKeys"));
        Assert.Equal(DateTime.MinValue,Get<DateTime>(app,"_runHoldUntil"));
    }

    [Fact]
    public void Boss_reposition_finishes_current_press_but_does_not_start_remaining_repeats()
    {
        var app=App(); var held=new HashSet<ushort>();var presses=0;
        app.CombatKeyDown=k=>{held.Add(k);presses++;}; app.CombatKeyUp=k=>held.Remove(k);
        Call(app,"StartComboMacro",new CombatAssist.Skill(87,900,Repeat:3,RepeatGapMs:150,AimMode:"Cursor"),
            new CombatAssist.Decision(true,87,0,1,"cast"),Now);
        TickEmptyMacro(app,Now); Assert.Single(held);
        Field("_bossDecision").SetValue(app,new BossCombat.Decision(BossCombat.Intent.Reposition,10));
        Assert.True(TickEmptyMacro(app,Now.AddMilliseconds(30)));Assert.Single(held);
        TickEmptyMacro(app,Now.AddMilliseconds(60));Assert.Empty(held);
        Assert.False(TickEmptyMacro(app,Now.AddMilliseconds(210))); Assert.Equal(1,presses);
        Assert.True(Flee(app,"WASD").Moving);
    }

}
