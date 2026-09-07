using POE2Radar.Overlay;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BossNavigationRegressionTests
{
    [Fact] public void Existing_auto_target_is_promoted_ahead_of_stale_route_after_rearming()
    {
        var selected=new List<string>{"c:715,604","c:950,576","e:164"};
        RadarApp.PromoteAutoTarget(selected,"c:950,576",3);
        Assert.Equal(new[]{"c:950,576","c:715,604","e:164"},selected);
        RadarApp.PromoteAutoTarget(selected,"c:950,576",3);
        Assert.Equal(3,selected.Count);
    }
    [Fact] public void First_distant_smoothed_waypoint_respects_click_lookahead()
    {
        var d=PathMove.Decide(new(true,true,true,new(100,100),new[]{(500,100)},3,
            DateTime.UtcNow,DateTime.MinValue,0,"Click",87,65,83,68,1,LookAhead:12));
        Assert.True(d.ShouldTap);Assert.Equal(112,d.TargetX);Assert.Equal(100,d.TargetY);
    }
}
