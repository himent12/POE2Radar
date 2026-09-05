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
    private static readonly DateTime Now = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // No live client, window, or input dispatch. Scheduling and empty-queue recovery only.
    private static RadarApp App()
    {
        var app = (RadarApp)RuntimeHelpers.GetUninitializedObject(typeof(RadarApp));
        Field("_settings").SetValue(app, new RadarSettings { CombatTapHoldMs = 60 });
        foreach (var name in new[] { "_macro", "_macroHeld", "_heldKeys" })
            Field(name).SetValue(app, Activator.CreateInstance(Field(name).FieldType));
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
}
