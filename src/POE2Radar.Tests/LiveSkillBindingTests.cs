using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class LiveSkillBindingTests
{
    [Fact]
    public void Zero_energy_shield_is_an_empty_pool_not_evidence_of_offset_drift()
    {
        var empty = new VitalStruct();
        Assert.True(empty.LooksValid(allowEmpty: true));
        Assert.False(empty.LooksValid()); // zero life/mana still do not anchor offset discovery
        empty.Current = 1; Assert.False(empty.LooksValid(allowEmpty: true));
        empty.Current = 0; empty.ReservedFlat = 5; Assert.False(empty.LooksValid(allowEmpty: true));
    }

    [Fact]
    public void Weapon_granted_chaos_bolt_resolves_uniquely_from_live_effect()
    {
        var found = SkillCatalog.FindByEffect("WeaponGrantedChaosboltPlayer");
        Assert.NotNull(found); Assert.Equal("Chaos Bolt", found.Value.Gem.Name);
        Assert.Null(SkillCatalog.FindByEffect("UnknownEffect"));
    }
    private static SkillBarSnapshot Bar(params (int Slot, string Effect)[] effects) => new(true,
        Enumerable.Range(1, 13).Select(i => new SkillBarSlot(i, effects.FirstOrDefault(e => e.Slot == i).Effect ?? "", "", (uint)i)).ToArray());

    [Fact]
    public void Live_bar_overrides_saved_slot_but_preserves_tuning_and_uses_chords()
    {
        var c = AutoBuildCatalogTests.Character("SkillGemSpark") with { SkillBar = Bar((9, "SparkPlayer")) };
        var old = new CombatSkill { SourceCharacter = "League:Hero", SourceMetadata = c.Skills[0].Metadata,
            SourceSlot = 4, Key = 81, CooldownMs = 1777 };
        var input = GameInputConfig.Parse(GameInputConfigTests.Config());
        var p = AutoBuild.Propose(c, new[] { old }, input);
        var row = Assert.Single(p.Suggestions);
        Assert.True(row.Recommended); Assert.Equal(9, row.Skill.SourceSlot); Assert.Equal(2, row.Skill.Modifiers);
        Assert.Equal(1777, row.Skill.CooldownMs); Assert.True(row.Skill.SourceLiveBinding);
        Assert.True(AutoBuild.TrySelect(p, new[] { new BuildSelection(c.Skills[0].Metadata, 81, 2) }, out var selected, out var error), error);
        Assert.Equal(9, Assert.Single(selected).SourceSlot);
        Assert.True(AutoBuild.BindingsMatch(selected, c.SkillBar, input));
        Assert.False(AutoBuild.BindingsMatch(selected, Bar((4, "SparkPlayer")), input));
        Assert.False(AutoBuild.BindingsMatch(selected, c.SkillBar, GameInputConfig.Parse(GameInputConfigTests.Config(q: 69))));
        Assert.False(AutoBuild.BindingsMatch(selected, null, input));
    }

    [Fact]
    public void Unequipped_bar_skill_and_wrong_manual_key_cannot_be_selected()
    {
        var c = AutoBuildCatalogTests.Character("SkillGemSpark", "SkillGemFireball") with { SkillBar = Bar((4, "SparkPlayer")) };
        var p = AutoBuild.Propose(c, Array.Empty<CombatSkill>(), GameInputConfig.Parse(GameInputConfigTests.Config()));
        Assert.False(p.Suggestions[1].Recommended); Assert.Equal(0, p.Suggestions[1].Skill.Key);
        Assert.False(AutoBuild.TrySelect(p, new[] { new BuildSelection(c.Skills[1].Metadata, 81, 0, 4) }, out _, out _));
        Assert.False(AutoBuild.TrySelect(p, new[] { new BuildSelection(c.Skills[0].Metadata, 87) }, out _, out _));
    }

    [Fact]
    public void Duplicate_bar_assignment_prefers_remembered_slot()
    {
        var c = AutoBuildCatalogTests.Character("SkillGemSpark") with { SkillBar = Bar((4, "SparkPlayer"), (9, "SparkPlayer")) };
        var saved = new CombatSkill { SourceCharacter = "League:Hero", SourceMetadata = c.Skills[0].Metadata, SourceSlot = 9 };
        var p = AutoBuild.Propose(c, new[] { saved }, GameInputConfig.Parse(GameInputConfigTests.Config()));
        Assert.Equal(9, Assert.Single(p.Suggestions).Skill.SourceSlot);
    }

    [Fact]
    public void Unbound_remembered_slot_uses_another_live_assignment()
    {
        var c = AutoBuildCatalogTests.Character("SkillGemSpark") with { SkillBar = Bar((4, "SparkPlayer"), (9, "SparkPlayer")) };
        var saved = new CombatSkill { SourceCharacter = "League:Hero", SourceMetadata = c.Skills[0].Metadata, SourceSlot = 4 };
        var input = GameInputConfig.Parse(GameInputConfigTests.Config().Replace("use_bound_skill4=81", "use_bound_skill4=0"));
        Assert.False(input.Find("use_bound_skill4")!.Usable);
        var row = Assert.Single(AutoBuild.Propose(c, new[] { saved }, input).Suggestions);
        Assert.True(row.Recommended);
        Assert.Equal(9, row.Skill.SourceSlot);
    }

    [Fact]
    public void Manual_confirmation_after_live_bar_failure_does_not_reuse_live_guard()
    {
        var c = AutoBuildCatalogTests.Character("SkillGemSpark");
        var saved = new CombatSkill { SourceCharacter = "League:Hero", SourceMetadata = c.Skills[0].Metadata,
            SourceSlot = 4, Key = 81, SourceLiveBinding = true };
        var p = AutoBuild.Propose(c, new[] { saved }, GameInputConfig.Parse(GameInputConfigTests.Config()));
        Assert.True(AutoBuild.TrySelect(p, new[] { new BuildSelection(c.Skills[0].Metadata, 81) }, out var selected, out var error), error);
        Assert.False(Assert.Single(selected).SourceLiveBinding);
        Assert.Equal(0, selected[0].SourceSlot);
        Assert.True(saved.SourceLiveBinding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Basic_attack_can_be_selected_without_the_detected_spender(bool includeEscape)
    {
        var c = AutoBuildCatalogTests.Character("SkillGemLightningArrow", "SkillGemPlayerDefaultBow", "SkillGemEscapeShot")
            with { SkillBar = Bar((4, "EscapeShotPlayer"), (9, "MeleeBowPlayer")) };
        var p = AutoBuild.Propose(c, Array.Empty<CombatSkill>(), GameInputConfig.Parse(GameInputConfigTests.Config()));
        var bindings = new List<BuildSelection> { new(c.Skills[1].Metadata, 81, 2, 9) };
        if (includeEscape) bindings.Add(new(c.Skills[2].Metadata, 81, 0, 4));
        Assert.True(AutoBuild.TrySelect(p, bindings, out var selected, out var error), error);
        Assert.Equal(0, selected[0].ManaBelowPct);
        Assert.True(selected[0].SourceLiveBinding);
    }
}
