using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BossBindingTests
{
    private static GameInputSnapshot Input(string mode, params GameBinding[] keys) => new(true,mode,"fixture","1",keys,Array.Empty<string>());
    private static readonly SkillBarSnapshot Bar = new(true,new[] {new SkillBarSlot(1,"","Move",1)},"");
    [Fact] public void Mouse_ranger_uses_actual_move_only_and_dodge_not_wasd_letters()
    {
        var settings=new RadarSettings {MoveMethod="Click",MoveClickKey=1,CombatDodgeKey=32};
        var input=Input("Mouse",new("use_bound_skill1",1,0),new("use_dodge_roll",32,0),new("use_bound_skill5",87,0));
        Assert.True(RadarApp.ControlsMatch(settings,input,Bar));
        Assert.False(RadarApp.ControlsMatch(settings,input,Bar with {Slots=new[] {new SkillBarSlot(1,"LightningArrowPlayer","LightningArrow",1)}}));
        settings.CombatDodgeKey=81;Assert.False(RadarApp.ControlsMatch(settings,input,Bar));
        settings.CombatDodgeKey=32;settings.MoveMethod="WASD";Assert.False(RadarApp.ControlsMatch(settings,input,Bar));
    }
    [Fact] public void Roll_landing_uses_actual_quantized_keys_and_inverse_calibration()
    {
        var settings = new RadarSettings { MoveKeyW=73, MoveKeyA=74, MoveKeyS=75, MoveKeyD=76, MoveAxisRotationDeg=90, BossDodgeDistance=40 };
        var point = RadarApp.DodgeLanding(new(50,50),new ushort[] {73},settings);
        Assert.NotNull(point); Assert.InRange(point.Value.X,89.99f,90.01f); Assert.InRange(point.Value.Y,49.99f,50.01f);
        Assert.Null(RadarApp.DodgeLanding(new(50,50),Array.Empty<ushort>(),settings));
    }
    [Fact] public void Changed_or_modified_movement_controls_fail_closed()
    {
        var settings=new RadarSettings {MoveMethod="WASD",MoveKeyW=73,MoveKeyA=74,MoveKeyS=75,MoveKeyD=76,CombatDodgeKey=88};
        var input=Input("WASD",new("move_up",73,0),new("move_left",74,0),new("move_down",75,0),new("move_right",76,0),new("use_dodge_roll",88,0));
        Assert.True(RadarApp.ControlsMatch(settings,input,Bar));
        Assert.False(RadarApp.ControlsMatch(settings,input with {Complete=false},Bar));
        Assert.False(RadarApp.ControlsMatch(settings,input with {Bindings=input.Bindings.Select(b=>b.Action=="move_up"?b with {Modifiers=2}:b).ToArray()},Bar));
    }
}
