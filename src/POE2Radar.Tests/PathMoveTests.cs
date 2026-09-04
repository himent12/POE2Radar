using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class PathMoveTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static PathMove.Snapshot Base(
        bool armed = true,
        bool focused = true,
        bool inGame = true,
        float playerX = 0,
        float playerY = 0,
        float arrive = 3,
        DateTime? now = null,
        DateTime? lastFire = null,
        int cooldownMs = 80,
        string method = "WASD",
        int clickKey = PathMove.VkClick,
        bool pauseForCombat = false,
        params (int x, int y)[] waypoints)
        => new(
            Armed: armed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: new NumVec2(playerX, playerY),
            Waypoints: waypoints,
            ArriveRadius: arrive,
            NowUtc: now ?? T0,
            LastFireUtc: lastFire ?? DateTime.MinValue,
            CooldownMs: cooldownMs,
            Method: method,
            KeyW: PathMove.VkW,
            KeyA: PathMove.VkA,
            KeyS: PathMove.VkS,
            KeyD: PathMove.VkD,
            ClickKey: clickKey,
            PauseForCombat: pauseForCombat);

    [Fact]
    public void NoPath_skips()
    {
        var d = PathMove.Decide(Base(waypoints: []));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("no path", d.Note);
    }

    [Fact]
    public void Arrived_skips()
    {
        var d = PathMove.Decide(Base(playerX: 10, playerY: 10, arrive: 3, waypoints: [(11, 10)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("arrived", d.Note);
    }

    [Fact]
    public void NeedNorth_holdsW()
    {
        var d = PathMove.Decide(Base(playerX: 0, playerY: 0, arrive: 3, waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap); // WASD is held, never tapped
        Assert.True(d.Moving);
        Assert.Equal(new ushort[] { PathMove.VkW }, d.HoldKeys);
        Assert.Equal("W", d.Note);
        Assert.Equal(0, d.TargetX);
        Assert.Equal(20, d.TargetY);
    }

    [Fact]
    public void Diagonal_holdsTwoKeys()
    {
        var d = PathMove.Decide(Base(waypoints: [(20, 20)]));
        Assert.Equal("WD", d.Note);
        Assert.Contains(PathMove.VkW, d.HoldKeys!);
        Assert.Contains(PathMove.VkD, d.HoldKeys!);
        // Shallow angle (10°) stays single-axis.
        d = PathMove.Decide(Base(waypoints: [(20, 3)]));
        Assert.Equal(new ushort[] { PathMove.VkD }, d.HoldKeys);
        // Diagonals off → dominant axis only.
        d = PathMove.Decide(Base(waypoints: [(20, 20)]) with { Diagonals = false });
        Assert.Single(d.HoldKeys!);
    }

    [Fact]
    public void Stopped_states_release_all_keys()
    {
        Assert.Empty(PathMove.Decide(Base(waypoints: [])).HoldKeys!);
        Assert.Empty(PathMove.Decide(Base(pauseForCombat: true, waypoints: [(0, 20)])).HoldKeys!);
        Assert.Empty(PathMove.Decide(Base(focused: false, waypoints: [(0, 20)])).HoldKeys!);
        Assert.Empty(PathMove.Decide(Base(playerX: 10, playerY: 10, waypoints: [(11, 10)])).HoldKeys!);
        Assert.False(PathMove.Decide(Base(pauseForCombat: true, waypoints: [(0, 20)])).Moving);
    }

    [Fact]
    public void LookAhead_targets_farthest_visible_waypoint()
    {
        // Straight route north; look-ahead 12 → aims at (0,12), not the first node (0,4).
        var d = PathMove.Decide(Base(waypoints: [(0, 4), (0, 8), (0, 12), (0, 16), (0, 30)]) with { LookAhead = 12 });
        Assert.Equal((0, 12), (d.TargetX, d.TargetY));
        // L-shaped route with a wall blocking the diagonal shortcut → stops at the corner.
        var w = 32; var h = 32;
        var walk = new byte[w * h]; Array.Fill(walk, (byte)1);
        for (var y = 1; y < 10; y++) for (var x = 1; x < 10; x++) walk[y * w + x] = 0; // block the inside of the L
        var pts = new (int x, int y)[] { (0, 4), (0, 8), (0, 10), (4, 10), (8, 10) };
        d = PathMove.Decide(Base(waypoints: pts) with { LookAhead = 20, Walkable = walk, Width = w, Height = h });
        Assert.Equal((0, 10), (d.TargetX, d.TargetY));
        // Without terrain the LOS check is skipped → farthest within range.
        d = PathMove.Decide(Base(waypoints: pts) with { LookAhead = 20 });
        Assert.Equal((8, 10), (d.TargetX, d.TargetY));
    }

    [Fact]
    public void AxisRotation_remaps_keys()
    {
        // +Y with a 90° rotation becomes −X → A.
        var d = PathMove.Decide(Base(waypoints: [(0, 20)]) with { AxisRotationDeg = 90 });
        Assert.Equal(new ushort[] { PathMove.VkA }, d.HoldKeys);
    }

    [Fact]
    public void Never_aims_at_a_waypoint_behind_the_player()
    {
        // Route east along y=0; player overshot node (10,0) and sits at (14,2) — node (10,0) is 4.5 away
        // (outside arrive 3) but BEHIND. Old logic aimed there (A key). Now the aim is ahead (D).
        var d = PathMove.Decide(Base(playerX: 14, playerY: 2, waypoints: [(0, 0), (10, 0), (20, 0), (30, 0)]) with { LookAhead = 8 });
        Assert.Contains(PathMove.VkD, d.HoldKeys!);
        Assert.DoesNotContain(PathMove.VkA, d.HoldKeys!);
        Assert.True(d.TargetX >= 20, $"aimed at ({d.TargetX},{d.TargetY})");
    }

    [Fact]
    public void NextWaypointIndex_projects_onto_route()
    {
        var pts = new (int x, int y)[] { (0, 0), (10, 0), (10, 10), (20, 10) };
        Assert.Equal(1, PathMove.NextWaypointIndex(pts, new NumVec2(2, 0), 9f));
        Assert.Equal(0, PathMove.NextWaypointIndex(pts, new NumVec2(-8, 0), 9f));    // before the route start
        Assert.Equal(2, PathMove.NextWaypointIndex(pts, new NumVec2(10, 3), 9f));     // past the corner
        Assert.Equal(3, PathMove.NextWaypointIndex(pts, new NumVec2(12, 10), 9f));    // on the last leg
        Assert.Equal(-1, PathMove.NextWaypointIndex(pts, new NumVec2(19, 10), 9f));   // goal reached
        // Switchback: route goes out and comes back near the player — the EARLIER segment wins on ties.
        var sw = new (int x, int y)[] { (0, 0), (10, 0), (10, 1), (0, 1) };
        Assert.Equal(1, PathMove.NextWaypointIndex(sw, new NumVec2(3, 0.5f), 1f));
    }

    [Fact]
    public void Sector_hysteresis_keeps_previous_keys_near_boundary()
    {
        // 25° above +X: nominally "D" (sector 0 spans ±22.5°) is exceeded → WD. With WD as previous keys the
        // heading of 20° (inside D's sector but within 8° of the WD edge) still keeps WD.
        var wd = new ushort[] { PathMove.VkW, PathMove.VkD };
        var d = PathMove.Decide(Base(waypoints: [(100, (int)MathF.Round(100 * MathF.Tan(20f * MathF.PI / 180f)))]) with { PrevHoldKeys = wd, LookAhead = 200 });
        Assert.Equal(wd, d.HoldKeys);
        // Well inside D (5°) → switches to D even with WD held before.
        d = PathMove.Decide(Base(waypoints: [(100, 9)]) with { PrevHoldKeys = wd, LookAhead = 200 });
        Assert.Equal(new ushort[] { PathMove.VkD }, d.HoldKeys);
    }

    [Fact]
    public void LookAhead_respects_clearance_in_tight_corridor()
    {
        // 1-wide diagonal-ish corridor: cell-centre LOS to (6,6) passes, but with clearance the line grazes
        // walls → aim stays at the first node.
        var w = 12; var h = 12;
        var walk = new byte[w * h];
        for (var i = 0; i < 8; i++) { walk[i * w + i] = 1; if (i + 1 < w) walk[i * w + i + 1] = 1; }
        var pts = new (int x, int y)[] { (4, 4), (6, 6), (7, 7) };
        // Thick LOS fails everywhere in the corridor → thin LOS at half look-ahead (6): (4,4) is 5.7 away, (6,6) is 8.5.
        var d = PathMove.Decide(Base(waypoints: pts) with { LookAhead = 12, Walkable = walk, Width = w, Height = h });
        Assert.Equal((4, 4), (d.TargetX, d.TargetY));
        Assert.False(PathMove.HasLineOfSight(walk, w, h, new NumVec2(0, 0), (6, 6), clearance: 1));
        Assert.True(PathMove.HasLineOfSight(walk, w, h, new NumVec2(0, 0), (6, 6)));
    }

    [Fact]
    public void Dither_time_slices_between_neighbouring_sectors()
    {
        // Heading 30°: between D (0°) and WD (45°). Over 4 consecutive 120 ms slots we must see both key sets.
        var target = (100, (int)MathF.Round(100 * MathF.Tan(30f * MathF.PI / 180f)));
        var seen = new HashSet<int>();
        for (var slot = 0; slot < 4; slot++)
        {
            var d = PathMove.Decide(Base(now: T0.AddMilliseconds(slot * 120), waypoints: [target]) with { LookAhead = 200 });
            seen.Add(d.HoldKeys!.Count);
        }
        Assert.Contains(1, seen);
        Assert.Contains(2, seen);
    }

    [Fact]
    public void Direction_helpers_roundtrip_and_perpendicular()
    {
        var keys = PathMove.KeysFor((1, 1), PathMove.VkW, PathMove.VkA, PathMove.VkS, PathMove.VkD);
        Assert.Equal((1, 1), PathMove.DirectionOf(keys, PathMove.VkW, PathMove.VkA, PathMove.VkS, PathMove.VkD));
        var perp = PathMove.KeysFor((-1, 1), PathMove.VkW, PathMove.VkA, PathMove.VkS, PathMove.VkD);
        Assert.Equal(new ushort[] { PathMove.VkW, PathMove.VkA }, perp);
    }

    [Fact]
    public void LineOfSight_from_a_blocked_origin_cell_still_sees_out()
    {
        // Player standing on a door-threshold cell the grid marks blocked: the line out of it is clear as long as
        // every cell AFTER the origin is; a wall further along still blocks.
        var walk = new byte[8 * 8]; Array.Fill(walk, (byte)1);
        walk[0] = 0;
        Assert.True(PathMove.HasLineOfSight(walk, 8, 8, new NumVec2(0, 0), (7, 0)));
        walk[4] = 0;
        Assert.False(PathMove.HasLineOfSight(walk, 8, 8, new NumVec2(0, 0), (7, 0)));
    }

    [Fact]
    public void LineOfSight_bresenham()
    {
        var walk = new byte[8 * 8]; Array.Fill(walk, (byte)1);
        Assert.True(PathMove.HasLineOfSight(walk, 8, 8, new NumVec2(0, 0), (7, 7)));
        walk[3 * 8 + 3] = 0;
        Assert.False(PathMove.HasLineOfSight(walk, 8, 8, new NumVec2(0, 0), (7, 7)));
        Assert.True(PathMove.HasLineOfSight(walk, 8, 8, new NumVec2(0, 0), (7, 0)));
    }

    [Fact]
    public void Unfocused_skips()
    {
        var d = PathMove.Decide(Base(focused: false, waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("paused (PoE2 not focused)", d.Note);
    }

    [Fact]
    public void Disarmed_skips()
    {
        var d = PathMove.Decide(Base(armed: false, waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("OFF (F5)", d.Note);
    }

    [Fact]
    public void ClickMethod_tapsClickKeyTowardWaypoint()
    {
        var d = PathMove.Decide(Base(method: "Click", waypoints: [(0, 20)]));
        Assert.True(d.ShouldTap);
        Assert.Equal(PathMove.VkClick, d.Vk);
        Assert.Equal("click", d.Note);
        Assert.Equal(0, d.TargetX);
        Assert.Equal(20, d.TargetY);
    }

    [Fact]
    public void ClickToMoveAlias_tapsConfiguredClickKey()
    {
        const int vkF = 0x46;
        var d = PathMove.Decide(Base(method: "ClickToMove", clickKey: vkF, waypoints: [(30, 0)]));
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)vkF, d.Vk);
        Assert.Equal("click", d.Note);
        Assert.Equal(30, d.TargetX);
        Assert.Equal(0, d.TargetY);
    }

    [Fact]
    public void PauseForCombat_skipsClickTowardQuest()
    {
        var d = PathMove.Decide(Base(method: "Click", pauseForCombat: true, waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("combat", d.Note);
    }

    [Fact]
    public void ClickMethod_arrived_skips()
    {
        var d = PathMove.Decide(Base(method: "Click", playerX: 10, playerY: 10, arrive: 3, waypoints: [(11, 10)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("arrived", d.Note);
    }

    [Fact]
    public void ClickMethod_cooldown_skips()
    {
        var d = PathMove.Decide(Base(
            method: "Click",
            now: T0,
            lastFire: T0.AddMilliseconds(-40),
            cooldownMs: 80,
            waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void UnknownMethod_skips()
    {
        var d = PathMove.Decide(Base(method: "Teleport", waypoints: [(0, 20)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("armed", d.Note);
    }
}
