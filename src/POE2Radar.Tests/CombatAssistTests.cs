using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class CombatAssistTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private const int VkQ = 0x51;
    private const int VkW = 0x57;
    private const int VkE = 0x45;
    private const int VkR = 0x52;

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
        int attackKey = VkQ,
        int nextIndex = 0,
        IReadOnlyList<CombatAssist.Skill>? skills = null,
        IReadOnlyList<DateTime>? lastFires = null,
        Poe2Live.EntityDot[]? entities = null)
    {
        skills ??= new[] { new CombatAssist.Skill(attackKey, cooldownMs) };
        lastFires ??= new[] { lastFire ?? DateTime.MinValue };
        return new(
            Armed: armed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: new NumVec2(playerX, playerY),
            Entities: entities ?? Array.Empty<Poe2Live.EntityDot>(),
            Range: range,
            NowUtc: now ?? T0,
            LastFireUtc: lastFires,
            Skills: skills,
            NextIndex: nextIndex);
    }

    [Fact]
    public void HostileInRange_taps()
    {
        var d = CombatAssist.Decide(Base(entities: [Monster(10, 0, reaction: 0)]));
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)VkQ, d.Vk);
        Assert.Equal(0, d.SkillIndex);
        Assert.Equal(0, d.NextIndex); // one-skill list wraps onto itself
        Assert.Equal("fired", d.Note);
    }

    [Fact]
    public void FriendlyOnly_skips()
    {
        var d = CombatAssist.Decide(Base(entities: [Monster(5, 0, reaction: 1)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void HostileOutOfRange_skips()
    {
        var d = CombatAssist.Decide(Base(range: 35, entities: [Monster(100, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void Disarmed_skips()
    {
        var d = CombatAssist.Decide(Base(armed: false, entities: [Monster(1, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("OFF (F4)", d.Note);
    }

    [Fact]
    public void Unfocused_skips()
    {
        var d = CombatAssist.Decide(Base(focused: false, entities: [Monster(1, 0, reaction: 0)]));
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
            entities: [Monster(1, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void FriendlyBitPattern_usesMaskedReaction()
    {
        // (0x81 & 0x7F) == 1 → friendly even with the high bit set.
        var d = CombatAssist.Decide(Base(entities: [Monster(1, 0, reaction: 0x81)]));
        Assert.False(d.ShouldTap);
    }

    [Fact]
    public void DeadHostile_skips()
    {
        var d = CombatAssist.Decide(Base(entities: [Monster(1, 0, reaction: 0, hpCur: 0, hpMax: 10)]));
        Assert.False(d.ShouldTap);
    }

    [Fact]
    public void NotInGame_skips()
    {
        var d = CombatAssist.Decide(Base(inGame: false, entities: [Monster(1, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal("paused (not in game)", d.Note);
    }

    [Fact]
    public void EmptyList_skips()
    {
        var d = CombatAssist.Decide(Base(
            skills: Array.Empty<CombatAssist.Skill>(),
            lastFires: Array.Empty<DateTime>(),
            entities: [Monster(1, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("no skills", d.Note);
    }

    [Fact]
    public void Rotation_firesInOrderThenWraps()
    {
        var skills = new[]
        {
            new CombatAssist.Skill(VkQ, 400),
            new CombatAssist.Skill(VkW, 400),
            new CombatAssist.Skill(VkE, 400),
        };
        var ready = new[] { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        var hostiles = new[] { Monster(1, 0, reaction: 0) };

        var d0 = CombatAssist.Decide(Base(skills: skills, lastFires: ready, nextIndex: 0, entities: hostiles));
        Assert.True(d0.ShouldTap);
        Assert.Equal((ushort)VkQ, d0.Vk);
        Assert.Equal(0, d0.SkillIndex);
        Assert.Equal(1, d0.NextIndex);

        var d1 = CombatAssist.Decide(Base(skills: skills, lastFires: ready, nextIndex: 1, entities: hostiles));
        Assert.True(d1.ShouldTap);
        Assert.Equal((ushort)VkW, d1.Vk);
        Assert.Equal(1, d1.SkillIndex);
        Assert.Equal(2, d1.NextIndex);

        var d2 = CombatAssist.Decide(Base(skills: skills, lastFires: ready, nextIndex: 2, entities: hostiles));
        Assert.True(d2.ShouldTap);
        Assert.Equal((ushort)VkE, d2.Vk);
        Assert.Equal(2, d2.SkillIndex);
        Assert.Equal(0, d2.NextIndex);
    }

    [Fact]
    public void Rotation_skipsSkillOnCooldown()
    {
        var skills = new[]
        {
            new CombatAssist.Skill(VkQ, 400),
            new CombatAssist.Skill(VkW, 400),
        };
        // Q still cooling; W ready. Cursor at Q → skip Q, fire W, wrap next to 0.
        var last = new[] { T0.AddMilliseconds(-100), DateTime.MinValue };
        var d = CombatAssist.Decide(Base(
            now: T0,
            skills: skills,
            lastFires: last,
            nextIndex: 0,
            entities: [Monster(1, 0, reaction: 0)]));
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)VkW, d.Vk);
        Assert.Equal(1, d.SkillIndex);
        Assert.Equal(0, d.NextIndex);
    }

    [Fact]
    public void PerSkillCooldown_allCooling_skips()
    {
        var skills = new[]
        {
            new CombatAssist.Skill(VkQ, 400),
            new CombatAssist.Skill(VkR, 800),
        };
        var last = new[] { T0.AddMilliseconds(-100), T0.AddMilliseconds(-200) };
        var d = CombatAssist.Decide(Base(
            now: T0,
            skills: skills,
            lastFires: last,
            nextIndex: 0,
            entities: [Monster(1, 0, reaction: 0)]));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void PerSkillRange_skipsTightSkillWhenHostileIsFarther()
    {
        var skills = new[]
        {
            new CombatAssist.Skill(VkQ, 0, Range: 10),
            new CombatAssist.Skill(VkW, 0, Range: 0),
        };
        // Hostile at 20: inside global 35, outside Q's 10 → skip Q, fire W (uses global).
        var d = CombatAssist.Decide(Base(
            range: 35,
            skills: skills,
            lastFires: new[] { DateTime.MinValue, DateTime.MinValue },
            nextIndex: 0,
            entities: [Monster(20, 0, reaction: 0)]));
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)VkW, d.Vk);
        Assert.Equal(1, d.SkillIndex);
    }

    [Fact]
    public void HasHostileInRange_true_for_alive_hostile()
    {
        Assert.True(CombatAssist.HasHostileInRange(
            [Monster(10, 0, reaction: 0)], new NumVec2(0, 0), 35));
    }

    [Fact]
    public void HasHostileInRange_false_for_friendly_or_far()
    {
        Assert.False(CombatAssist.HasHostileInRange(
            [Monster(5, 0, reaction: 1)], new NumVec2(0, 0), 35));
        Assert.False(CombatAssist.HasHostileInRange(
            [Monster(100, 0, reaction: 0)], new NumVec2(0, 0), 35));
    }
}
