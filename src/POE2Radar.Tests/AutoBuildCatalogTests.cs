using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class AutoBuildCatalogTests
{
    [Fact]
    public void Full_thirteen_slot_bar_can_be_selected_without_truncation()
    {
        var p = Proposal(new[] { "Fireball", "Spark", "IceStrike", "RollingSlam", "LightningArrow", "StormcallerArrow",
            "Contagion", "EssenceDrain", "Arc", "Frostbolt", "IceNova", "FlameWall", "ShockwaveTotem" }.Select(n => "SkillGem" + n).ToArray());
        Assert.Equal(13, p.Suggestions.Count(s => s.Supported));
        Assert.True(AutoBuild.TrySelect(p, p.Suggestions.Select((s, i) => new BuildSelection(s.Metadata, 65 + i)).ToArray(),
            out var skills, out var error), error);
        Assert.Equal(13, skills.Count);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(skills,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        var parser = typeof(POE2Radar.Overlay.Web.ApiServer).GetMethod("TryParseCombatSkills",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object?[] args = { json.RootElement, null };
        Assert.True((bool)parser.Invoke(null, args)!);
        Assert.Equal(13, ((List<CombatSkill>)args[1]!).Count);
    }

    internal static CharacterLoadout Character(params string[] gems) => new(true, "Hero", "League", 30,
        new Poe2Live.Vitals(500, 500, 200, 200, 0, 0),
        new[] { new LoadoutItem(3, "Weapon", "Metadata/Items/Weapons/TwoHandWeapons/Bows/Test", "Bow") },
        gems.Select(g => new LoadoutItem(47, "Skills", "Metadata/Items/Gems/" + g, g)).ToArray(), Array.Empty<string>());
    private static BuildProposal Proposal(params string[] gems) => AutoBuild.Propose(Character(gems), Array.Empty<CombatSkill>());

    [Theory]
    [InlineData("SkillGemFireball", "Main attack", true)]
    [InlineData("SkillGemSnipe", "Main attack", true)]
    [InlineData("SkillGemIceTippedArrows", "Empower attack", true)]
    [InlineData("SkillGemUnearth", "Main attack", true)]
    [InlineData("SkillGemChaosbolt", "Main attack", true)]
    [InlineData("SkillGemLightningSpear", "Main attack", true)]
    [InlineData("SkillGemExplosiveSpear", "Main attack", true)]
    [InlineData("SkillGemParry", "Unsupported", false)]
    [InlineData("SkillGemIceStrike", "Main attack", true)]
    [InlineData("SkillGemRollingSlam", "Main attack", true)]
    [InlineData("SkillGemContagion", "Main attack", true)]
    [InlineData("SkillGemShockwaveTotem", "Summon", true)]
    [InlineData("SkillGemVoltaicMark", "Debuff / warcry", true)]
    [InlineData("SkillGemHeraldOfIce", "Persistent / triggered", false)]
    [InlineData("SkillGemExplosiveShot", "Ammunition", false)]
    [InlineData("SkillGemBoneOffering", "Unsupported", false)]
    [InlineData("SkillGemDetonateDead", "Unsupported", false)]
    [InlineData("SkillGemTempestBell", "Unsupported", false)]
    [InlineData("SkillGemFallingThunder", "Unsupported", false)]
    public void Classifies_by_effect_facts_and_excludes_unobservable_requirements(string id, string role, bool supported)
    {
        var row = Assert.Single(Proposal(id).Suggestions);
        Assert.Equal(role, row.Role); Assert.Equal(supported, row.Supported);
        Assert.False(row.Skill.Enabled);
    }

    [Fact]
    public void Uses_actual_base_cast_time_and_spaces_duration_skills()
    {
        Assert.Equal(1200, Assert.Single(Proposal("SkillGemFireball").Suggestions).Skill.RepeatGapMs);
        Assert.True(Assert.Single(Proposal("SkillGemContagion").Suggestions).Skill.CooldownMs >= 3000);
    }

    [Fact]
    public void Melee_positioning_wins_over_ranged_utility_and_mixed_attacks()
    {
        var p = Proposal("SkillGemIceStrike", "SkillGemSpark");
        Assert.Equal(12, p.CombatRange); Assert.Equal(0, p.KeepDistance);
    }

    [Fact]
    public void Basic_attack_without_spender_works_at_full_mana()
    {
        var row = Assert.Single(Proposal("SkillGemPlayerDefaultBow").Suggestions);
        Assert.Equal("Main attack", row.Role); Assert.Equal(0, row.Skill.ManaBelowPct);
    }

    [Fact]
    public void Saved_slot_follows_changed_keys_and_keeps_user_timing()
    {
        var c = Character("SkillGemSpark");
        var saved = new CombatSkill { SourceMetadata = c.Skills[0].Metadata, SourceCharacter = "League:Hero", SourceSlot = 4,
            Key = 81, CooldownMs = 1777, HoldMs = 555, Range = 30 };
        var p = AutoBuild.Propose(c, new[] { saved }, GameInputConfig.Parse(GameInputConfigTests.Config(q: 69)));
        var skill = Assert.Single(p.Suggestions).Skill;
        Assert.Equal(69, skill.Key); Assert.Equal(1777, skill.CooldownMs); Assert.Equal(555, skill.HoldMs);
        Assert.Equal(81, saved.Key);
        var unavailable = AutoBuild.Propose(c, new[] { saved }, GameInputConfig.Parse(""));
        Assert.Equal(0, Assert.Single(unavailable.Suggestions).Skill.Key);
    }

    [Fact]
    public void Chord_uniqueness_slot_validation_and_control_conflicts()
    {
        var c = Character("SkillGemSpark", "SkillGemFireball");
        var p = AutoBuild.Propose(c, Array.Empty<CombatSkill>(), GameInputConfig.Parse(GameInputConfigTests.Config()));
        var a = c.Skills[0].Metadata; var b = c.Skills[1].Metadata;
        Assert.True(AutoBuild.TrySelect(p, new[] { new BuildSelection(a, 81, 0, 4), new BuildSelection(b, 81, 2, 9) }, out var skills, out var error), error);
        Assert.Equal(2, skills[1].Modifiers); Assert.Equal(9, skills[1].SourceSlot);
        Assert.False(AutoBuild.TrySelect(p, new[] { new BuildSelection(a, 81, 0, 9) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(p, new[] { new BuildSelection(a, 49) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(p, new[] { new BuildSelection(a, 81, 2), new BuildSelection(b, 81, 2) }, out _, out _));
    }
}
