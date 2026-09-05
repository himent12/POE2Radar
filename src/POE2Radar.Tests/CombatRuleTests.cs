using System.Reflection;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

public sealed class CombatRuleTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static Poe2Live.EntityDot Enemy(int hp = 10, int max = 10, float x = 2, uint id = 1)
        => new(id, 0, new(x, 0), default, Poe2Live.EntityCategory.Monster,
            "Metadata/Monsters/Test", hp, max, false, 0, Poe2Live.Rarity.Normal, false);
    private static CombatAssist.Snapshot State(CombatAssist.Skill first, params CombatAssist.Skill[] rest)
            => new(true, true, true, default, new[] { Enemy() }, 35, Now,
                Array.Empty<DateTime>(), rest.Prepend(first).ToArray(), 0);
    private static CombatAssist.Skill Recovery => new(81, 5000, MinTargets: 0,
        RequireTarget: false, Priority: true, AimMode: "Cursor", ManaBelowPct: 30);

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    public void Low_mana_recovery_works_without_enemies(float mana, bool expected)
    {
        var state = State(Recovery) with { Entities = Array.Empty<Poe2Live.EntityDot>(), PlayerManaPct = mana };
        var result = CombatAssist.Decide(state);
        Assert.Equal(expected, result.ShouldTap);
        Assert.False(result.HasTarget);
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, true)]
    public void Minimum_mana_is_inclusive(float mana, bool expected)
        => Assert.Equal(expected, CombatAssist.Decide(State(new(81, 0, MinManaPct: 20)) with { PlayerManaPct = mana }).ShouldTap);

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 29, true)]
    [InlineData(true, 30, false)]
    public void Shield_trigger_requires_a_real_pool(bool hasEs, float es, bool expected)
        => Assert.Equal(expected, CombatAssist.Decide(State(new(81, 0, EsBelowPct: 30)) with { HasEs = hasEs, PlayerEsPct = es }).ShouldTap);

    [Theory]
    [InlineData("life")]
    [InlineData("mana")]
    [InlineData("minMana")]
    [InlineData("es")]
    public void Resource_conditions_fail_closed_when_vitals_unreadable(string rule)
    {
        var skill = rule switch
        {
            "life" => new CombatAssist.Skill(81, 0, HpBelowPct: 50),
            "mana" => new CombatAssist.Skill(81, 0, ManaBelowPct: 50),
            "minMana" => new CombatAssist.Skill(81, 0, MinManaPct: 10),
            _ => new CombatAssist.Skill(81, 0, EsBelowPct: 50)
        };
        Assert.False(CombatAssist.Decide(State(skill) with
        {
            VitalsKnown = false,
            PlayerHpPct = 20,
            PlayerManaPct = 20,
            PlayerEsPct = 20,
            HasEs = true
        }).ShouldTap);
    }

    [Fact]
    public void Nonfinite_resource_values_do_not_fire()
        => Assert.False(CombatAssist.Decide(State(Recovery) with { PlayerManaPct = float.NaN }).ShouldTap);

    [Fact]
    public void All_conditions_must_match()
    {
        var state = State(new(81, 0, HpBelowPct: 40, MinManaPct: 25)) with { PlayerHpPct = 30, PlayerManaPct = 20 };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
        Assert.True(CombatAssist.Decide(state with { PlayerManaPct = 25 }).ShouldTap);
    }

    [Fact]
    public void Priority_precedes_rotation_without_advancing_its_cursor()
    {
        var state = State(new(82, 0), Recovery) with { PlayerManaPct = 20 };
        var decision = CombatAssist.Decide(state);
        Assert.Equal(1, decision.SkillIndex);
        Assert.Equal(0, decision.NextIndex);
        decision = CombatAssist.Decide(state with { LastFireUtc = new[] { DateTime.MinValue, Now } });
        Assert.Equal(0, decision.SkillIndex);
    }

    [Fact]
    public void Priority_recovery_remains_available_while_fleeing()
    {
        var state = State(new(82, 0), Recovery) with { Fleeing = true, PlayerManaPct = 20 };
        Assert.Equal(1, CombatAssist.Decide(state).SkillIndex);
        Assert.False(CombatAssist.Decide(state with { PlayerManaPct = 100 }).ShouldTap);
    }

    [Theory]
    [InlineData("disarmed")]
    [InlineData("unfocused")]
    [InlineData("loading")]
    [InlineData("busy")]
    [InlineData("dead")]
    public void Recovery_never_bypasses_safety_or_active_combo(string gate)
    {
        var state = State(Recovery) with { PlayerManaPct = 20 };
        state = gate switch
        {
            "disarmed" => state with { Armed = false },
            "unfocused" => state with { Focused = false },
            "loading" => state with { InGame = false },
            "busy" => state with { Busy = true },
            _ => state with { PlayerHpPct = 0 }
        };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
    }

    [Fact]
    public void Escape_aim_requires_a_hostile_direction()
    {
        var state = State(Recovery with { AimMode = "Away" }) with { PlayerManaPct = 20 };
        Assert.True(CombatAssist.Decide(state).HasTarget);
        Assert.False(CombatAssist.Decide(state with { Entities = Array.Empty<Poe2Live.EntityDot>() }).ShouldTap);
    }

    [Fact]
    public void Enemy_rules_still_apply_to_optional_target_skills()
    {
        var state = State(Recovery with { MinTargets = 1 }) with { Entities = Array.Empty<Poe2Live.EntityDot>(), PlayerManaPct = 20 };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
        Assert.False(CombatAssist.Decide(state with { Skills = new[] { Recovery with { RareOnly = true } } }).ShouldTap);
    }

    [Theory]
    [InlineData(1, 10, true)]
    [InlineData(2, 10, false)]
    [InlineData(1, 0, false)]
    public void Finisher_requires_known_enemy_hp_below_threshold(int hp, int max, bool expected)
        => Assert.Equal(expected, CombatAssist.Decide(State(new(81, 0, TargetHpBelowPct: 20)) with { Entities = new[] { Enemy(hp, max) } }).ShouldTap);

    [Fact]
    public void Pack_count_uses_actual_skill_range()
    {
        var state = State(new(81, 0, Range: 50, MinTargets: 2)) with { Entities = new[] { Enemy(), Enemy(x: 45, id: 2) } };
        Assert.True(CombatAssist.Decide(state).ShouldTap);
        Assert.False(CombatAssist.Decide(state with { Skills = new[] { new CombatAssist.Skill(81, 0, Range: 5, MinTargets: 2) } }).ShouldTap);
    }

    private static List<CombatSkill> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(ApiServer).GetMethod("TryParseCombatSkills", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = { doc.RootElement, null };
        Assert.True((bool)method.Invoke(null, args)!);
        return Assert.IsType<List<CombatSkill>>(args[1]);
    }

    [Fact]
    public void Settings_round_trip_all_rules_and_combo_controls()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var expected = new CombatSkill
        {
            Name = "Escape",
            Key = 81,
            HpBelowPct = 35,
            ManaBelowPct = 50,
            MinManaPct = 10,
            EsBelowPct = 20,
            TargetHpBelowPct = 40,
            RequireTarget = false,
            MinTargets = 0,
            Priority = true,
            AimMode = "Away",
            Enabled = false,
            RareOnly = true,
            Repeat = 3,
            HoldMs = 200,
            RepeatGapMs = 300,
            NextDelayMs = 500,
            DodgeAfter = true
        };
        var json = JsonSerializer.Serialize(new[] { expected }, options);
        var actual = Assert.Single(Parse(json));
        Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual, options));
    }

    [Fact]
    public void Parser_clamps_conditions_and_preserves_legacy_defaults()
    {
        var legacy = Assert.Single(Parse("[{\"key\":81}]"));
        Assert.True(legacy.RequireTarget);
        Assert.Equal("Target", legacy.AimMode);
        Assert.False(legacy.Priority);
        var skill = Assert.Single(Parse("[{\"key\":81,\"manaBelowPct\":200,\"minManaPct\":-10,\"esBelowPct\":150,\"minTargets\":-5,\"aimMode\":\"invalid\"}]"));
        Assert.Equal(100f, skill.ManaBelowPct);
        Assert.Equal(100f, skill.EsBelowPct);
        Assert.Equal(0f, skill.MinManaPct);
        Assert.Equal(0, skill.MinTargets);
        Assert.Equal("Target", skill.AimMode);
        Assert.Empty(Parse("[{\"key\":999}]"));
    }

    [Fact]
    public void Dashboard_keeps_full_skill_objects_and_exposes_presets()
    {
        Assert.Contains("...sk, key:sk.key", DashboardHtml.Page);
        Assert.Contains("Mana recovery", DashboardHtml.Page);
        Assert.Contains("Low-life escape", DashboardHtml.Page);
        Assert.Contains("Any low resource (OR)", DashboardHtml.Page);
        Assert.Contains("Minimum mana and enemy rules always apply", DashboardHtml.Page);
        Assert.Contains("cannot be armed from this page", DashboardHtml.Page);
    }
    [Theory]
    [InlineData(20, 100, true)]
    [InlineData(100, 20, true)]
    [InlineData(100, 100, false)]
    [InlineData(35, 25, false)]
    public void Any_low_resource_matches_either_trigger(float hp, float es, bool expected)
    {
        var skill = new CombatAssist.Skill(81, 5000, MinTargets: 0, RequireTarget: false,
            AimMode: "Cursor", HpBelowPct: 35, EsBelowPct: 25, AnyLowResource: true);
        var state = State(skill) with { PlayerHpPct = hp, PlayerEsPct = es, HasEs = true };
        Assert.Equal(expected, CombatAssist.Decide(state).ShouldTap);
    }

    [Fact]
    public void Any_low_resource_still_requires_mana_budget_and_enemy_rules()
    {
        var skill = new CombatAssist.Skill(81, 0, HpBelowPct: 35, ManaBelowPct: 30,
            MinManaPct: 20, MinTargets: 2, AnyLowResource: true);
        var state = State(skill) with { PlayerHpPct = 20, PlayerManaPct = 10 };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
        Assert.False(CombatAssist.Decide(state with { PlayerManaPct = 25 }).ShouldTap);
        Assert.True(CombatAssist.Decide(state with { PlayerManaPct = 25, Entities = new[] { Enemy(), Enemy(id: 2) } }).ShouldTap);
        Assert.False(CombatAssist.Decide(state with { VitalsKnown = false }).ShouldTap);
    }

    [Fact]
    public void Any_low_resource_does_not_treat_absent_es_as_empty()
    {
        var state = State(new(81, 0, HpBelowPct: 35, EsBelowPct: 25, AnyLowResource: true))
            with
        { HasEs = false, PlayerEsPct = 0, PlayerHpPct = 100 };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
        Assert.True(CombatAssist.Decide(state with { PlayerHpPct = 20 }).ShouldTap);
    }

    [Fact]
    public void Any_mode_with_no_triggers_keeps_normal_rotation()
        => Assert.True(CombatAssist.Decide(State(new(81, 0, AnyLowResource: true))).ShouldTap);

    [Fact]
    public void Any_low_resource_setting_survives_round_trip_and_defaults_to_all()
    {
        var skill = Assert.Single(Parse("[{\"key\":81,\"anyLowResource\":true,\"hpBelowPct\":35,\"esBelowPct\":25}]"));
        Assert.True(skill.AnyLowResource);
        var restored = JsonSerializer.Deserialize<CombatSkill>(JsonSerializer.Serialize(skill))!;
        Assert.True(restored.AnyLowResource);
        Assert.False(Assert.Single(Parse("[{\"key\":81}]")).AnyLowResource);
    }
    [Theory]
    [InlineData("Target", false, 0)]
    [InlineData("Away", false, 0)]
    [InlineData("Cursor", true, 0)]
    [InlineData("Cursor", false, 50)]
    public void Enemy_dependent_rules_honor_range_even_when_target_checkbox_is_off(string aim, bool rare, float finisher)
    {
        var skill = new CombatAssist.Skill(81, 0, Range: 5, MinTargets: 0, RequireTarget: false,
            AimMode: aim, RareOnly: rare, TargetHpBelowPct: finisher);
        var enemy = Enemy(hp: 1, x: 10) with { Rarity = Poe2Live.Rarity.Rare };
        var state = State(skill) with { Entities = new[] { enemy } };
        Assert.False(CombatAssist.Decide(state).ShouldTap);
        Assert.True(CombatAssist.Decide(state with { Entities = new[] { enemy with { Grid = new(3, 0) } } }).ShouldTap);
    }
}
