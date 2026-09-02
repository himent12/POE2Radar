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
    public void NeedNorth_tapsW()
    {
        var d = PathMove.Decide(Base(playerX: 0, playerY: 0, arrive: 3, waypoints: [(0, 20)]));
        Assert.True(d.ShouldTap);
        Assert.Equal(PathMove.VkW, d.Vk);
        Assert.Equal("W", d.Note);
        Assert.Equal(0, d.TargetX);
        Assert.Equal(20, d.TargetY);
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
