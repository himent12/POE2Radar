using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class BuffKeeperTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private const int VkT = 0x54;
    private const int VkR = 0x52;

    private static Poe2Live.BuffInfo Buff(string name, float left = 10f, float total = 20f, int charges = 0)
        => new(name, left, total, charges);

    private static BuffRule Rule(string trigger = "Missing", string buff = "arcane_surge", int key = VkT,
        int minGapMs = 1500, float refreshBelow = 2f, int intervalMs = 10000, bool nearHostiles = false,
        float hostileRange = 60f, bool skipInTown = true, int minCharges = 0, bool enabled = true)
        => new()
        {
            Enabled = enabled, Name = "r", Key = key, Trigger = trigger, BuffName = buff, MinGapMs = minGapMs,
            RefreshBelowSec = refreshBelow, IntervalMs = intervalMs, OnlyNearHostiles = nearHostiles,
            HostileRange = hostileRange, SkipInTown = skipInTown, MinCharges = minCharges,
        };

    private static BuffKeeper.Snapshot Snap(IReadOnlyList<BuffRule> rules,
        IReadOnlyList<Poe2Live.BuffInfo>? buffs, DateTime? now = null, IReadOnlyList<DateTime>? lastFired = null,
        DateTime lastAny = default, bool armed = true, bool focused = true, bool inGame = true, bool alive = true,
        bool inTown = false, IReadOnlyList<float>? hostiles = null, int globalGapMs = 250)
        => new(now ?? T0, armed, focused, inGame, alive, inTown, hostiles, buffs, rules,
            lastFired ?? new DateTime[rules.Count], lastAny, globalGapMs);

    private static readonly Poe2Live.BuffInfo[] NoBuffs = [];

    [Fact]
    public void Missing_FiresWhenBuffAbsent()
    {
        var d = BuffKeeper.Decide(Snap([Rule()], [Buff("flask_effect_life")]));
        Assert.NotNull(d);
        Assert.Equal(0, d.Value.RuleIndex);
        Assert.Equal(VkT, d.Value.Key);
    }

    [Fact]
    public void Missing_DoesNotFireWhenBuffPresent_CaseInsensitiveSubstring()
    {
        var s = Snap([Rule(buff: "ARCANE")], [Buff("arcane_surge", left: 5)]);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.StartsWith("active 5.0s", BuffKeeper.RuleNotes(s)[0]);
    }

    [Fact]
    public void Missing_AlternativesSeparatedByPipe()
    {
        Assert.Null(BuffKeeper.Decide(Snap([Rule(buff: "nope | herald_of_ice")], [Buff("player_herald_of_ice")])));
        Assert.NotNull(BuffKeeper.Decide(Snap([Rule(buff: "nope|other")], [Buff("player_herald_of_ice")])));
    }

    [Fact]
    public void Missing_GraceAfterPress_DoesNotDoublePress()
    {
        var rules = new[] { Rule(minGapMs: 1500) };
        var s = Snap(rules, NoBuffs, now: T0.AddMilliseconds(1000), lastFired: [T0], lastAny: T0);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.StartsWith("cooldown", BuffKeeper.RuleNotes(s)[0]);
        Assert.NotNull(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(1600), lastFired: [T0], lastAny: T0)));
    }

    [Fact]
    public void Expiring_FiresBelowThreshold_NotAbove()
    {
        var rules = new[] { Rule("Expiring", refreshBelow: 2f) };
        Assert.Null(BuffKeeper.Decide(Snap(rules, [Buff("arcane_surge", left: 3f)])));
        var d = BuffKeeper.Decide(Snap(rules, [Buff("arcane_surge", left: 1.5f)]));
        Assert.NotNull(d);
        Assert.StartsWith("expiring", d.Value.Note);
    }

    [Fact]
    public void Expiring_AlsoFiresWhenMissing()
        => Assert.NotNull(BuffKeeper.Decide(Snap([Rule("Expiring")], NoBuffs)));

    [Fact]
    public void Expiring_UsesLongestMatchingInstance()
        => Assert.Null(BuffKeeper.Decide(Snap([Rule("Expiring")],
            [Buff("arcane_surge", left: 0.5f), Buff("arcane_surge", left: 8f)])));

    [Fact]
    public void Expiring_InfiniteBuffNeverExpires()
    {
        var s = Snap([Rule("Expiring", refreshBelow: 5f)], [Buff("arcane_surge", left: float.PositiveInfinity, total: float.PositiveInfinity)]);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.StartsWith("active ∞", BuffKeeper.RuleNotes(s)[0]);
    }

    [Fact]
    public void Interval_FiresFirstTimeThenOnTimer()
    {
        var rules = new[] { Rule("Interval", buff: "", intervalMs: 10000) };
        Assert.NotNull(BuffKeeper.Decide(Snap(rules, NoBuffs)));
        var early = Snap(rules, NoBuffs, now: T0.AddSeconds(9), lastFired: [T0], lastAny: T0);
        Assert.Null(BuffKeeper.Decide(early));
        Assert.StartsWith("next in 1.0s", BuffKeeper.RuleNotes(early)[0]);
        Assert.NotNull(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddSeconds(10), lastFired: [T0], lastAny: T0)));
    }

    [Fact]
    public void Interval_FloorPreventsSpam()
    {
        var rules = new[] { Rule("Interval", intervalMs: 0, minGapMs: 0) };
        Assert.Null(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(400), lastFired: [T0], lastAny: T0)));
        Assert.NotNull(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(BuffKeeper.MinIntervalMs), lastFired: [T0], lastAny: T0)));
    }

    [Fact]
    public void UnreadableBuffs_NeverFireBuffBasedRules()
    {
        var rules = new[] { Rule("Missing"), Rule("Expiring"), Rule("Missing", minCharges: 3) };
        var s = Snap(rules, buffs: null);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.All(BuffKeeper.RuleNotes(s), n => Assert.Equal("paused (buffs unreadable)", n));
    }

    [Fact]
    public void UnreadableBuffs_IntervalRulesStillFire()
    {
        var d = BuffKeeper.Decide(Snap([Rule("Missing"), Rule("Interval", key: VkR)], buffs: null));
        Assert.NotNull(d);
        Assert.Equal(1, d.Value.RuleIndex);
        Assert.Equal(VkR, d.Value.Key);
    }

    [Fact]
    public void MinCharges_RecastsWhenBelow()
    {
        var rules = new[] { Rule("Missing", minCharges: 3) };
        var d = BuffKeeper.Decide(Snap(rules, [Buff("arcane_surge", charges: 1)]));
        Assert.NotNull(d);
        Assert.StartsWith("charges 1<3", d.Value.Note);
        Assert.Null(BuffKeeper.Decide(Snap(rules, [Buff("arcane_surge", charges: 3)])));
    }

    [Fact]
    public void GlobalGap_BlocksAnyPressAndIsFlooredAt50ms()
    {
        var rules = new[] { Rule(), Rule(key: VkR) };
        var lastFired = new[] { T0, default };
        Assert.Null(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(100), lastFired: lastFired, lastAny: T0)));
        var d = BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(300), lastFired: lastFired, lastAny: T0));
        Assert.Equal(1, d?.RuleIndex);
        Assert.Null(BuffKeeper.Decide(Snap(rules, NoBuffs, now: T0.AddMilliseconds(20), lastFired: lastFired, lastAny: T0, globalGapMs: 0)));
    }

    [Fact]
    public void RuleOrderIsPriority_OneKeyPerCall()
    {
        var d = BuffKeeper.Decide(Snap([Rule(key: VkT), Rule(key: VkR)], NoBuffs));
        Assert.Equal(0, d?.RuleIndex);
        Assert.Equal(VkT, d?.Key);
    }

    [Fact]
    public void RuleOnCooldown_LetsNextRuleFire()
    {
        var d = BuffKeeper.Decide(Snap([Rule(key: VkT), Rule(key: VkR)], NoBuffs,
            now: T0.AddMilliseconds(500), lastFired: [T0, default], lastAny: T0));
        Assert.Equal(1, d?.RuleIndex);
    }

    [Fact]
    public void Town_SkippedUnlessOptedOut()
    {
        var s = Snap([Rule(skipInTown: true)], NoBuffs, inTown: true);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.Equal("town — skipped", BuffKeeper.RuleNotes(s)[0]);
        Assert.NotNull(BuffKeeper.Decide(Snap([Rule(skipInTown: false)], NoBuffs, inTown: true)));
    }

    [Fact]
    public void HostileGate_RequiresHostileWithinRange()
    {
        var rules = new[] { Rule(nearHostiles: true, hostileRange: 30f) };
        var none = Snap(rules, NoBuffs, hostiles: null);
        Assert.Null(BuffKeeper.Decide(none));
        Assert.Equal("waiting for hostiles", BuffKeeper.RuleNotes(none)[0]);
        Assert.Null(BuffKeeper.Decide(Snap(rules, NoBuffs, hostiles: [45f, 31f])));
        Assert.NotNull(BuffKeeper.Decide(Snap(rules, NoBuffs, hostiles: [45f, 29f])));
    }

    [Theory]
    [InlineData(false, true, true, true, "keeper is OFF")]
    [InlineData(true, false, true, true, "game not focused")]
    [InlineData(true, true, false, true, "not in game")]
    [InlineData(true, true, true, false, "dead")]
    public void GlobalGates_BlockEverything(bool armed, bool focused, bool inGame, bool alive, string note)
    {
        var rules = new[] { Rule("Missing"), Rule("Interval") };
        var s = Snap(rules, NoBuffs, armed: armed, focused: focused, inGame: inGame, alive: alive);
        Assert.Null(BuffKeeper.Decide(s));
        Assert.All(BuffKeeper.RuleNotes(s), n => Assert.Equal(note, n));
    }

    [Fact]
    public void Misconfigured_RulesNeverFire()
    {
        var rules = new[]
        {
            Rule(enabled: false), Rule(key: 0), Rule(buff: "  "), Rule(trigger: "Sometimes"), Rule("Expiring", buff: "|"),
        };
        var s = Snap(rules, NoBuffs);
        Assert.Null(BuffKeeper.Decide(s));
        var notes = BuffKeeper.RuleNotes(s);
        Assert.Equal("disabled", notes[0]);
        Assert.Equal("no key set", notes[1]);
        Assert.Equal("no buff name set", notes[2]);
        Assert.StartsWith("unknown trigger", notes[3]);
        Assert.Equal("no buff name set", notes[4]);
    }

    [Fact]
    public void Trigger_IsCaseInsensitive_AndBlankMeansMissing()
    {
        Assert.NotNull(BuffKeeper.Decide(Snap([Rule("interval")], buffs: null)));
        Assert.NotNull(BuffKeeper.Decide(Snap([Rule("")], NoBuffs)));
    }

    [Fact]
    public void LastFiredShorterThanRules_TreatedAsNeverFired()
        => Assert.Equal(1, BuffKeeper.Decide(Snap([Rule(buff: "arcane"), Rule(buff: "herald", key: VkR)], [Buff("arcane_surge")],
            lastFired: Array.Empty<DateTime>()))?.RuleIndex);

    [Fact]
    public void RuleNotes_QueuedWhileGlobalGapRuns()
    {
        var s = Snap([Rule()], NoBuffs, now: T0.AddMilliseconds(100), lastFired: [default], lastAny: T0);
        Assert.Equal("missing → recast (queued)", BuffKeeper.RuleNotes(s)[0]);
    }

    [Fact]
    public void Settings_DefaultsAreSafeAndRoundTrip()
    {
        var s = new BuffKeeperSettings();
        Assert.True(s.Enabled);          // armed by default, but nothing presses: the only rule is off
        Assert.DoesNotContain(s.Rules, r => r.Enabled);
        Assert.Single(s.Rules);
        Assert.False(s.Rules[0].Enabled);
        Assert.Equal(VkT, s.Rules[0].Key);

        s.Rules.Add(Rule("Expiring", buff: "a|b"));
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<BuffKeeperSettings>(json)!;
        Assert.Equal(2, back.Rules.Count); // replaced, not appended to the default example
        Assert.Equal("Expiring", back.Rules[1].Trigger);
        Assert.Equal("a|b", back.Rules[1].BuffName);
        Assert.Equal(s.ToggleHotkey, back.ToggleHotkey);
    }
}
