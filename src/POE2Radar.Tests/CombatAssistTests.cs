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

    private static Poe2Live.EntityDot Monster(float x, float y, byte reaction, int hpCur = 10, int hpMax = 10,
        uint id = 1, Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal)
        => new(
            Id: id,
            Address: 0,
            Grid: new NumVec2(x, y),
            World: default,
            Category: Poe2Live.EntityCategory.Monster,
            Metadata: "Metadata/Monsters/Test",
            HpCur: hpCur,
            HpMax: hpMax,
            Poi: false,
            Reaction: reaction,
            Rarity: rarity,
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
    public void Decision_carries_aim_target()
    {
        var d = CombatAssist.Decide(Base(entities: [Monster(4, 3, reaction: 0, id: 42)]));
        Assert.True(d.ShouldTap);
        Assert.True(d.HasTarget);
        Assert.Equal(42u, d.TargetId);
        Assert.Equal(new NumVec2(4, 3), d.TargetGrid);
    }

    [Fact]
    public void Idle_on_cooldown_still_reports_target()
    {
        var d = CombatAssist.Decide(Base(
            now: T0, lastFire: T0.AddMilliseconds(-100), cooldownMs: 400,
            entities: [Monster(4, 3, reaction: 0, id: 42)]));
        Assert.False(d.ShouldTap);
        Assert.True(d.HasTarget);
        Assert.Equal(42u, d.TargetId);
    }

    [Fact]
    public void Target_prefers_rarity_then_distance()
    {
        var ents = new[]
        {
            Monster(2, 0, reaction: 0, id: 1),
            Monster(10, 0, reaction: 0, id: 2, rarity: Poe2Live.Rarity.Rare),
            Monster(20, 0, reaction: 0, id: 3, rarity: Poe2Live.Rarity.Rare),
            Monster(1, 0, reaction: 1, id: 4, rarity: Poe2Live.Rarity.Unique), // friendly unique: ignored
        };
        Assert.True(CombatAssist.TryPickTarget(ents, NumVec2.Zero, 35, out var t, mode: CombatAssist.TargetMode.Rarity));
        Assert.Equal(2u, t.Id);
        // Nearest wins among equals.
        Assert.True(CombatAssist.TryPickTarget([Monster(9, 0, reaction: 0, id: 8), Monster(3, 0, reaction: 0, id: 9)], NumVec2.Zero, 35, out t, mode: CombatAssist.TargetMode.Rarity));
        Assert.Equal(9u, t.Id);
        // Default mode is plain nearest.
        Assert.True(CombatAssist.TryPickTarget(ents, NumVec2.Zero, 35, out t));
        Assert.Equal(1u, t.Id);
    }

    [Fact]
    public void Target_modes_lowest_and_highest_hp()
    {
        var ents = new[]
        {
            Monster(2, 0, reaction: 0, id: 1, hpCur: 90, hpMax: 100),
            Monster(6, 0, reaction: 0, id: 2, hpCur: 10, hpMax: 100),
            Monster(9, 0, reaction: 0, id: 3, hpCur: 100, hpMax: 100),
        };
        Assert.True(CombatAssist.TryPickTarget(ents, NumVec2.Zero, 35, out var t, mode: CombatAssist.TargetMode.LowestHp));
        Assert.Equal(2u, t.Id);
        Assert.True(CombatAssist.TryPickTarget(ents, NumVec2.Zero, 35, out t, mode: CombatAssist.TargetMode.HighestHp));
        Assert.Equal(3u, t.Id);
    }

    [Fact]
    public void MinTargets_gates_aoe_skill()
    {
        var skills = new[] { new CombatAssist.Skill(VkQ, 0, MinTargets: 3), new CombatAssist.Skill(VkW, 0) };
        var two = new[] { Monster(2, 0, reaction: 0, id: 1), Monster(4, 0, reaction: 0, id: 2) };
        var d = CombatAssist.Decide(Base(skills: skills, lastFires: new[] { DateTime.MinValue, DateTime.MinValue }, entities: two));
        Assert.Equal((ushort)VkW, d.Vk);
        var three = two.Append(Monster(6, 0, reaction: 0, id: 3)).ToArray();
        d = CombatAssist.Decide(Base(skills: skills, lastFires: new[] { DateTime.MinValue, DateTime.MinValue }, entities: three));
        Assert.Equal((ushort)VkQ, d.Vk);
        Assert.Equal(3, d.HostilesInRange);
    }

    [Fact]
    public void RareOnly_and_HpBelow_and_Disabled_gates()
    {
        var skills = new[]
        {
            new CombatAssist.Skill(VkQ, 0, RareOnly: true),
            new CombatAssist.Skill(VkW, 0, HpBelowPct: 40),
            new CombatAssist.Skill(VkE, 0, Enabled: false),
            new CombatAssist.Skill(VkR, 0),
        };
        var ready = new[] { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        // Normal mob, full HP → Q (rare only) skipped, W (hp<40) skipped, E disabled → R.
        var d = CombatAssist.Decide(Base(skills: skills, lastFires: ready, entities: [Monster(3, 0, reaction: 0)]));
        Assert.Equal((ushort)VkR, d.Vk);
        // Rare mob → Q fires.
        d = CombatAssist.Decide(Base(skills: skills, lastFires: ready, entities: [Monster(3, 0, reaction: 0, rarity: Poe2Live.Rarity.Rare)]));
        Assert.Equal((ushort)VkQ, d.Vk);
        // Low player HP → W fires (cursor at 1).
        d = CombatAssist.Decide(Base(skills: skills, lastFires: ready, nextIndex: 1, entities: [Monster(3, 0, reaction: 0)]) with { PlayerHpPct = 25 });
        Assert.Equal((ushort)VkW, d.Vk);
    }

    [Fact]
    public void KeyboardOnly_skips_mouse_bound_skills()
    {
        var skills = new[] { new CombatAssist.Skill(0x01, 0), new CombatAssist.Skill(VkQ, 0) };
        var d = CombatAssist.Decide(Base(skills: skills, lastFires: new[] { DateTime.MinValue, DateTime.MinValue }, entities: [Monster(3, 0, reaction: 0)]) with { KeyboardOnly = true });
        Assert.Equal((ushort)VkQ, d.Vk);
        d = CombatAssist.Decide(Base(skills: new[] { new CombatAssist.Skill(0x01, 0) }, entities: [Monster(3, 0, reaction: 0)]) with { KeyboardOnly = true });
        Assert.False(d.ShouldTap);
        Assert.True(d.HasTarget);
    }

    [Fact]
    public void Busy_macro_blocks_new_decisions()
    {
        var d = CombatAssist.Decide(Base(nextIndex: 1, entities: [Monster(3, 0, reaction: 0)]) with { Busy = true });
        Assert.False(d.ShouldTap);
        Assert.Equal("casting", d.Note);
        Assert.Equal(1, d.NextIndex);
    }

    [Fact]
    public void Priority_order_always_prefers_first_ready_skill()
    {
        var skills = new[] { new CombatAssist.Skill(VkQ, 400), new CombatAssist.Skill(VkW, 400) };
        var ready = new[] { DateTime.MinValue, DateTime.MinValue };
        var host = new[] { Monster(3, 0, reaction: 0) };
        var d = CombatAssist.Decide(Base(skills: skills, lastFires: ready, nextIndex: 1, entities: host) with { PriorityOrder = true });
        Assert.Equal((ushort)VkQ, d.Vk);
        // Q cooling → W, and NextIndex is untouched (no rotation cursor in priority mode).
        d = CombatAssist.Decide(Base(now: T0, skills: skills, lastFires: new[] { T0.AddMilliseconds(-100), DateTime.MinValue }, nextIndex: 0, entities: host) with { PriorityOrder = true });
        Assert.Equal((ushort)VkW, d.Vk);
        Assert.Equal(0, d.NextIndex);
    }

    [Fact]
    public void Ignored_ids_are_skipped_for_targeting()
    {
        var ents = new[] { Monster(2, 0, reaction: 0, id: 1), Monster(6, 0, reaction: 0, id: 2) };
        var d = CombatAssist.Decide(Base(entities: ents) with { IgnoreIds = new HashSet<uint> { 1 } });
        Assert.True(d.ShouldTap);
        Assert.Equal(2u, d.TargetId);
        var only = CombatAssist.Decide(Base(entities: [ents[0]]) with { IgnoreIds = new HashSet<uint> { 1 } });
        Assert.False(only.ShouldTap);
        Assert.False(only.HasTarget);
        Assert.False(CombatAssist.HasHostileInRange([ents[0]], NumVec2.Zero, 35, new HashSet<uint> { 1 }));
    }

    [Fact]
    public void PerSkillRange_checks_the_chosen_target()
    {
        // Rare at 20 outranks the normal at 3; Q (range 10) can't reach the rare → skip to W.
        var skills = new[] { new CombatAssist.Skill(VkQ, 0, Range: 10), new CombatAssist.Skill(VkW, 0) };
        var d = CombatAssist.Decide(Base(
            skills: skills, lastFires: new[] { DateTime.MinValue, DateTime.MinValue },
            entities: [Monster(3, 0, reaction: 0, id: 1), Monster(20, 0, reaction: 0, id: 2, rarity: Poe2Live.Rarity.Rare)]) with { Mode = CombatAssist.TargetMode.Rarity });
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)VkW, d.Vk);
        Assert.Equal(2u, d.TargetId);
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
