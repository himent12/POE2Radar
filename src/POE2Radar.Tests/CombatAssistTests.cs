using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class CombatAssistTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Poe2Live.EntityDot Monster(float x, float y, byte reaction, int hpCur = 10, int hpMax = 10)
        => new(
            Id: 1,
            Address: 0,
            Grid: new NumVec2(x, y),
            World: default,
            Category: Poe2Live.EntityCategory.Monster,
            Metadata: "Metadata/Monsters/Test",
            HpCur: hpCur,
            HpMax: hpMax,
            Poi: false,
            Reaction: reaction,
            Rarity: Poe2Live.Rarity.Normal,
            Opened: false);

    private static CombatAssist.Snapshot Base(
        bool armed = true,
        bool focused = true,
        bool inGame = true,
        float playerX = 0,
        float playerY = 0,
        float range = 35,
        DateTime? now = null,
        DateTime? lastFire = null,
        int cooldownMs = 400,
        params Poe2Live.EntityDot[] entities)
        => new(
            Armed: armed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: new NumVec2(playerX, playerY),
            Entities: entities,
            Range: range,
            NowUtc: now ?? T0,
            LastFireUtc: lastFire ?? DateTime.MinValue,
            CooldownMs: cooldownMs);

    [Fact]
    public void HostileInRange_taps()
    {
        var d = CombatAssist.Decide(Base(entities: Monster(10, 0, reaction: 0)));
        Assert.True(d.ShouldTap);
        Assert.Equal("fired", d.Note);
    }

    [Fact]
    public void FriendlyOnly_skips()
    {
        var d = CombatAssist.Decide(Base(entities: Monster(5, 0, reaction: 1)));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void HostileOutOfRange_skips()
    {
        var d = CombatAssist.Decide(Base(range: 35, entities: Monster(100, 0, reaction: 0)));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void Disarmed_skips()
    {
        var d = CombatAssist.Decide(Base(armed: false, entities: Monster(1, 0, reaction: 0)));
        Assert.False(d.ShouldTap);
        Assert.Equal("OFF (F4)", d.Note);
    }

    [Fact]
    public void Unfocused_skips()
    {
        var d = CombatAssist.Decide(Base(focused: false, entities: Monster(1, 0, reaction: 0)));
        Assert.False(d.ShouldTap);
        Assert.Equal("paused (PoE2 not focused)", d.Note);
    }

    [Fact]
    public void CooldownNotElapsed_skips()
    {
        var d = CombatAssist.Decide(Base(
            now: T0,
            lastFire: T0.AddMilliseconds(-100),
            cooldownMs: 400,
            entities: Monster(1, 0, reaction: 0)));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void FriendlyBitPattern_usesMaskedReaction()
    {
        // (0x81 & 0x7F) == 1 → friendly even with the high bit set.
        var d = CombatAssist.Decide(Base(entities: Monster(1, 0, reaction: 0x81)));
        Assert.False(d.ShouldTap);
    }

    [Fact]
    public void DeadHostile_skips()
    {
        var d = CombatAssist.Decide(Base(entities: Monster(1, 0, reaction: 0, hpCur: 0, hpMax: 10)));
        Assert.False(d.ShouldTap);
    }

    [Fact]
    public void NotInGame_skips()
    {
        var d = CombatAssist.Decide(Base(inGame: false, entities: Monster(1, 0, reaction: 0)));
        Assert.False(d.ShouldTap);
        Assert.Equal("paused (not in game)", d.Note);
    }
}
