using System.Reflection;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

public sealed class AutoBuildTests
{
    private const string Arrow = "Metadata/Items/Gem/SkillGemStormcallerArrow";
    private const string Escape = "Metadata/Items/Gems/SkillGemEscapeShot";
    private const string Basic = "Metadata/Items/Gem/SkillGemPlayerDefaultBow";
    private static CharacterLoadout Character(int es = 16) => new(true, "Archer", "League", 7,
        new Poe2Live.Vitals(185, 185, 92, 92, es, es),
        new[] { new LoadoutItem(3, "Weapon set 1", "Metadata/Items/Weapons/TwoHandWeapons/Bows/FourBow2", "Shortbow") },
        new[] { new LoadoutItem(47, "Skills", Arrow, "Stormcaller Arrow"),
            new LoadoutItem(47, "Skills", Escape, "Escape Shot"), new LoadoutItem(75, "Granted", Basic, "Bow Shot") }, Array.Empty<string>());
    private static BuildProposal Proposal(CharacterLoadout? c = null, params CombatSkill[] existing)
        => AutoBuild.Propose(c ?? Character(), existing);

    [Fact]
    public void Bow_loadout_generates_attack_escape_and_mana_fallback()
    {
        var p = Proposal();
        Assert.Equal(35f, p.CombatRange);
        Assert.Equal(12f, p.KeepDistance);
        Assert.Equal("Emergency escape", p.Suggestions[0].Role);
        Assert.Equal(15f, p.Suggestions.Single(s => s.Metadata == Arrow).Skill.MinManaPct);
        Assert.Equal(15f, p.Suggestions.Single(s => s.Metadata == Basic).Skill.ManaBelowPct);
        Assert.All(p.Suggestions, s => { Assert.Equal(0, s.Skill.Key); Assert.False(s.Skill.Enabled); });
    }

    [Fact]
    public void Escape_shot_aims_at_enemy_because_it_jumps_backward()
        => Assert.Equal("Target", Proposal().Suggestions.Single(s => s.Metadata == Escape).Skill.AimMode);

    [Theory]
    [InlineData(0, false)]
    [InlineData(16, false)]
    [InlineData(100, true)]
    public void Tiny_shield_does_not_cause_constant_escape(int es, bool trigger)
        => Assert.Equal(trigger, Proposal(Character(es)).Suggestions.Single(s => s.Metadata == Escape).Skill.AnyLowResource);

    [Fact]
    public void Only_exact_per_character_bindings_are_reused()
    {
        var saved = new CombatSkill { Key = 82, Name = "Custom name", SourceMetadata = Arrow, SourceCharacter = "League:Archer" };
        Assert.Equal(82, Proposal(null, saved).Suggestions.Single(s => s.Metadata == Arrow).Skill.Key);
        saved.SourceCharacter = "League:SomeoneElse";
        Assert.Equal(0, Proposal(null, saved).Suggestions.Single(s => s.Metadata == Arrow).Skill.Key);
        Assert.Equal("Custom name", saved.Name);
    }

    [Fact]
    public void Unknown_skills_are_reported_disabled_not_guessed()
    {
        var c = Character() with { Skills = new[] { new LoadoutItem(47, "Skills", "Metadata/Items/Gems/Unknown", "Unknown") } };
        var suggestion = Assert.Single(Proposal(c).Suggestions);
        Assert.False(suggestion.Supported);
        Assert.False(suggestion.Skill.Enabled);
    }

    [Fact]
    public void Bag_items_never_supply_the_weapon_archetype()
    {
        var c = Character() with { Equipment = new[] { Character().Equipment[0] with { InventoryId = 1 } } };
        Assert.Equal("Unknown weapon", Proposal(c).Archetype);
        Assert.All(Proposal(c).Suggestions, s => Assert.False(s.Supported));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(115)]
    [InlineData(999)]
    public void Invalid_or_hotkey_bindings_are_rejected(int key)
        => Assert.False(AutoBuild.TrySelect(Proposal(), new[] { (Arrow, key) }, out _, out _));

    [Fact]
    public void Selection_requires_complete_scan_main_attack_and_unique_keys()
    {
        Assert.False(AutoBuild.TrySelect(Proposal(Character() with { Complete = false }), new[] { (Arrow, 81) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(Proposal(), new[] { (Escape, 81) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(Proposal(), new[] { (Arrow, 81), (Escape, 81) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(Proposal(), new[] { (Arrow, 81), (Arrow, 82) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(Proposal(), Array.Empty<(string, int)>(), out _, out _));
        Assert.False(AutoBuild.TrySelect(Proposal(), Enumerable.Repeat((Arrow, 81), 9).ToArray(), out _, out _));
    }

    [Fact]
    public void Selection_clones_without_mutating_preview()
    {
        var p = Proposal();
        Assert.True(AutoBuild.TrySelect(p, new[] { (Arrow, 82), (Escape, 81) }, out var selected, out var error), error);
        Assert.All(selected, s => Assert.True(s.Enabled));
        Assert.Equal(0, p.Suggestions.Single(s => s.Metadata == Arrow).Skill.Key);
        Assert.Equal("League:Archer", selected[0].SourceCharacter);
    }

    [Fact]
    public void Regular_skill_edits_preserve_confirmed_binding_identity()
    {
        using var doc = JsonDocument.Parse("[{\"key\":81,\"sourceMetadata\":\"Metadata/Items/Gems/Test\",\"sourceCharacter\":\"League:Archer\"}]");
        var parser = typeof(ApiServer).GetMethod("TryParseCombatSkills", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = { doc.RootElement, null };
        Assert.True((bool)parser.Invoke(null, args)!);
        var skill = Assert.Single((List<CombatSkill>)args[1]!);
        Assert.Equal("League:Archer", skill.SourceCharacter);
        Assert.Equal("Metadata/Items/Gems/Test", skill.SourceMetadata);
    }

    [Fact]
    public void Dashboard_describes_limits_and_never_arms_combat()
    {
        Assert.Contains("Scan &amp; propose build", DashboardHtml.Page);
        Assert.Contains("Keys need one-time confirmation", DashboardHtml.Page);
        Assert.Contains("Apply selected build", DashboardHtml.Page);
        Assert.DoesNotContain("combatAssistEnabled", DashboardHtml.Page, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Preview_fingerprint_tracks_identity_equipment_and_capacity_not_current_life()
    {
        var method = typeof(ApiServer).GetMethod("LoadoutKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Key(CharacterLoadout c) => (string)method.Invoke(null, new object[] { c })!;
        var character = Character();
        var key = Key(character);
        Assert.Equal(key, Key(character with { Vitals = character.Vitals!.Value with { HpCur = 10 } }));
        Assert.NotEqual(key, Key(character with { Character = "Other" }));
        Assert.NotEqual(key, Key(character with { Vitals = character.Vitals!.Value with { HpUnreserved = 500 } }));
        Assert.NotEqual(key, Key(character with { Equipment = Array.Empty<LoadoutItem>() }));
        Assert.NotEqual(key, Key(character with { Skills = Array.Empty<LoadoutItem>() }));
    }
}
