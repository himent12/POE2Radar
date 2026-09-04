using POE2Radar.Core.Game;
using POE2Radar.Overlay.Navigation;
using NumVec2 = System.Numerics.Vector2;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Farm loop phase machine: clear → portal → enter → town waypoint → menu → new instance → clear.</summary>
public sealed class FarmLoopTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private const string Farm = "G1_2";
    private const string Town = "G1_town";

    private static Poe2Live.EntityDot Obj(uint id, string metadata, float x, float y)
        => new(id, 0, new NumVec2(x, y), default, Poe2Live.EntityCategory.Object, metadata, 0, 0, true, 0, Poe2Live.Rarity.NonMonster, false);

    private static FarmLoop.Snapshot S(FarmLoop.Phase phase, string area, DateTime now, DateTime? since = null,
        bool clearDone = false, IReadOnlyList<Poe2Live.EntityDot>? ents = null, bool menu = false, DateTime? lastInput = null, bool keyBound = true)
        => new(phase, since ?? T0, now, area, Farm, QuestFollow.IsTownOrHideout(area), clearDone,
            ents ?? Array.Empty<Poe2Live.EntityDot>(), NumVec2.Zero, menu, lastInput ?? DateTime.MinValue, keyBound);

    [Fact]
    public void Off_starts_in_the_phase_the_zone_implies()
    {
        Assert.Equal(FarmLoop.Phase.Clearing, FarmLoop.Next(S(FarmLoop.Phase.Off, Farm, T0)).Phase);
        Assert.Equal(FarmLoop.Phase.TownWaypoint, FarmLoop.Next(S(FarmLoop.Phase.Off, Town, T0)).Phase);
        Assert.Equal(FarmLoop.Phase.OpenPortal, FarmLoop.Next(S(FarmLoop.Phase.Off, "G1_3", T0)).Phase);
    }

    [Fact]
    public void Clearing_holds_until_cleared_then_casts_a_portal_and_walks_into_it()
    {
        Assert.Equal(FarmLoop.Phase.Clearing, FarmLoop.Next(S(FarmLoop.Phase.Clearing, Farm, T0)).Phase);
        var s1 = FarmLoop.Next(S(FarmLoop.Phase.Clearing, Farm, T0, clearDone: true));
        Assert.Equal(FarmLoop.Phase.OpenPortal, s1.Phase);
        Assert.True(s1.Reset);

        // No portal yet, no recent cast → cast. Just cast → wait. Portal appears → enter it (by entity id).
        Assert.Equal(FarmLoop.Input.CastPortal, FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0)).Input);
        Assert.Equal(FarmLoop.Input.None, FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0.AddSeconds(1), lastInput: T0)).Input);
        Assert.Equal(FarmLoop.Input.CastPortal, FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0.AddSeconds(5), lastInput: T0)).Input);
        var portal = Obj(7, "Metadata/MiscellaneousObjects/TownPortal", 4, 4);
        var s2 = FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0.AddSeconds(2), ents: [portal], lastInput: T0));
        Assert.Equal(FarmLoop.Phase.EnterPortal, s2.Phase);
        Assert.Equal("e:7", s2.TargetId);
        // The town-side return portal is NOT our portal; a far portal is not ours either.
        Assert.Equal(FarmLoop.Phase.OpenPortal, FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0.AddSeconds(2),
            ents: [Obj(8, "Metadata/MiscellaneousObjects/ReturnToLastTownPortal", 2, 2), Obj(9, "Metadata/MiscellaneousObjects/TownPortal", 200, 200)], lastInput: T0)).Phase);
        // Unbound portal key → never casts, says so.
        var nb = FarmLoop.Next(S(FarmLoop.Phase.OpenPortal, Farm, T0, keyBound: false));
        Assert.Equal(FarmLoop.Input.None, nb.Input);
        Assert.Contains("portal key", nb.Note);
    }

    [Fact]
    public void Portal_gone_or_too_slow_recasts()
    {
        var portal = Obj(7, "Metadata/MiscellaneousObjects/TownPortal", 4, 4);
        Assert.Equal(FarmLoop.Phase.EnterPortal, FarmLoop.Next(S(FarmLoop.Phase.EnterPortal, Farm, T0.AddSeconds(5), ents: [portal])).Phase);
        Assert.Equal(FarmLoop.Phase.OpenPortal, FarmLoop.Next(S(FarmLoop.Phase.EnterPortal, Farm, T0.AddSeconds(5))).Phase);
        Assert.Equal(FarmLoop.Phase.OpenPortal, FarmLoop.Next(S(FarmLoop.Phase.EnterPortal, Farm, T0.AddSeconds(30), ents: [portal])).Phase);
    }

    [Fact]
    public void Town_walks_to_the_waypoint_then_selects_the_area_then_waits_for_the_new_instance()
    {
        // Arriving in town from EnterPortal → TownWaypoint targeting the waypoint entity.
        var wp = Obj(3, "Metadata/MiscellaneousObjects/Waypoint", 10, 10);
        var s1 = FarmLoop.Next(S(FarmLoop.Phase.EnterPortal, Town, T0, ents: [wp]));
        Assert.Equal(FarmLoop.Phase.TownWaypoint, s1.Phase);
        Assert.Equal("e:3", s1.TargetId);
        // Menu shows the area → WaypointMenu; first tick there requests the ctrl-click; once the click is
        // stamped after the phase started → Returning; menu still open 3 s later → click again.
        Assert.Equal(FarmLoop.Phase.WaypointMenu, FarmLoop.Next(S(FarmLoop.Phase.TownWaypoint, Town, T0.AddSeconds(4), ents: [wp], menu: true)).Phase);
        var since = T0.AddSeconds(4);
        var click = FarmLoop.Next(S(FarmLoop.Phase.WaypointMenu, Town, since.AddMilliseconds(100), since, menu: true, lastInput: T0));
        Assert.Equal(FarmLoop.Input.ClickWaypointArea, click.Input);
        Assert.Null(click.TargetId);
        var ret = FarmLoop.Next(S(FarmLoop.Phase.WaypointMenu, Town, since.AddMilliseconds(600), since, menu: true, lastInput: since.AddMilliseconds(500)));
        Assert.Equal(FarmLoop.Phase.Returning, ret.Phase);
        var rs = since.AddMilliseconds(600);
        Assert.Equal(FarmLoop.Phase.Returning, FarmLoop.Next(S(FarmLoop.Phase.Returning, Town, rs.AddSeconds(1), rs, menu: true)).Phase);
        Assert.Equal(FarmLoop.Phase.WaypointMenu, FarmLoop.Next(S(FarmLoop.Phase.Returning, Town, rs.AddSeconds(4), rs, menu: true)).Phase);
        Assert.Equal(FarmLoop.Phase.TownWaypoint, FarmLoop.Next(S(FarmLoop.Phase.Returning, Town, rs.AddSeconds(45), rs)).Phase);
        // Menu closed without the click landing → back to the waypoint after the menu timeout.
        Assert.Equal(FarmLoop.Phase.WaypointMenu, FarmLoop.Next(S(FarmLoop.Phase.WaypointMenu, Town, since.AddSeconds(1), since, ents: [wp])).Phase);
        Assert.Equal(FarmLoop.Phase.TownWaypoint, FarmLoop.Next(S(FarmLoop.Phase.WaypointMenu, Town, since.AddSeconds(6), since, ents: [wp])).Phase);
        // Zone changes to the farm area → Clearing (fresh run).
        var back = FarmLoop.Next(S(FarmLoop.Phase.Returning, Farm, rs.AddSeconds(10), rs));
        Assert.Equal(FarmLoop.Phase.Clearing, back.Phase);
        Assert.True(back.Reset);
    }

    [Fact]
    public void Wrong_zone_portals_home_and_town_mid_clear_goes_to_the_waypoint()
    {
        Assert.Equal(FarmLoop.Phase.OpenPortal, FarmLoop.Next(S(FarmLoop.Phase.Clearing, "G1_3", T0)).Phase);
        Assert.Equal(FarmLoop.Phase.TownWaypoint, FarmLoop.Next(S(FarmLoop.Phase.Clearing, Town, T0)).Phase);
    }
}
