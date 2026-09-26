using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Pricing;
using Xunit;

namespace POE2Radar.Tests;

public sealed class MapCheckTests
{
    private static ItemTradeProfile Waystone(params string[] mods) =>
        new("Dread Keep", Poe2Live.Rarity.Rare, true, mods.Select(m => new ItemTradeMod("explicit", m)).ToArray(), true);

    [Fact]
    public void Flags_default_dangerous_mods_case_insensitively()
    {
        var r = MapCheck.Check(Waystone("Players cannot Regenerate Life, Mana or Energy Shield",
            "Monsters have 40% increased Critical Hit Chance", "Area contains 2 additional packs of Beasts"),
            new MapCheckSettings().Dangerous);
        Assert.Equal(2, r.Flagged.Count);
        var (text, detail, danger) = MapCheck.Describe("Dread Keep", r);
        Assert.True(danger);
        Assert.Contains("2 dangerous mods", text);
        Assert.Contains("! Players cannot Regenerate", detail);
    }

    [Fact]
    public void Clean_waystone_reports_all_clear()
    {
        var r = MapCheck.Check(Waystone("Area contains 2 additional packs of Beasts"), new MapCheckSettings().Dangerous);
        var (text, _, danger) = MapCheck.Describe("x", r);
        Assert.False(danger);
        Assert.Contains("none flagged", text);
    }

    [Theory]
    [InlineData("Monsters deal 30% of Damage as Extra Fire", "re:Damage as Extra (Fire|Cold)", true)]
    [InlineData("Monsters deal 30% of Damage as Extra Chaos", "re:Damage as Extra (Fire|Cold)", false)]
    [InlineData("anything", "re:(unclosed", false)]
    [InlineData("anything", "   ", false)]
    public void Regex_patterns_are_supported_and_invalid_ones_never_match(string text, string pattern, bool expected)
        => Assert.Equal(expected, MapCheck.Matches(text, pattern));

    [Theory]
    [InlineData("Metadata/Items/Maps/MapKeyTier15", "Dread Keep", true)]
    [InlineData("Metadata/Items/Rings/Ring1", "Waystone (Tier 3)", true)]
    [InlineData("Metadata/Items/Rings/Ring1", "Ruby Ring", false)]
    [InlineData("", null, false)]
    public void Detects_waystones_by_metadata_or_name(string metadata, string? name, bool expected)
        => Assert.Equal(expected, MapCheck.IsWaystone(metadata, name));
}
