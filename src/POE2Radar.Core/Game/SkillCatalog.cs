using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>Offline PoE2 gem/effect facts. Base timings are not character-modified timings.</summary>
public static class SkillCatalog
{
    public sealed record Effect(string Id, string Name, string[] Types,
        bool Manual, int CastMs, int CooldownMs, string[] CostKinds, bool Reserves);
    public sealed record Gem(string Name, string GemType, string[] Tags, Effect[] Effects);
    private static readonly Lazy<Dictionary<string, Gem>> Data = new(() =>
    {
        using var stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream(
            "POE2Radar.Core.Game.poe2_skill_catalog.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, Gem>>(stream)!;
    });
    public static Gem? Find(string metadata)
    {
        if (Data.Value.TryGetValue(metadata, out var gem)) return gem;
        // The game's Gem/Gems directory aliases have varied between granted and socketed gems.
        var alias = metadata.Contains("/Items/Gems/", StringComparison.Ordinal)
            ? metadata.Replace("/Items/Gems/", "/Items/Gem/", StringComparison.Ordinal)
            : metadata.Replace("/Items/Gem/", "/Items/Gems/", StringComparison.Ordinal);
        return Data.Value.GetValueOrDefault(alias);
    }

    public static (string Metadata, Gem Gem)? FindByEffect(string effectId)
    {
        var matches = Data.Value.Where(pair => pair.Value.GemType == "active"
            && pair.Value.Effects.Any(e => e.Id == effectId && e.Manual)).Take(2).ToArray();
        return matches.Length == 1 ? (matches[0].Key, matches[0].Value) : null;
    }
}
