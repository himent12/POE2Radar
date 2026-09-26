using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>
/// Rollable affix tiers and equipment base stats, from the RePoE PoE2 export (embedded <c>poe2_item_data.json</c>,
/// regenerated per patch by <c>resources/poe2-data/generate_item_data.py</c>). Lets a mod read from memory be placed
/// in its family — "T2 of 9 on these boots, 92% of the best roll" — and gives each base's damage, attack time,
/// crit and defences so weapon DPS and total armour/evasion/ES can be computed from the base plus its local mods.
/// A tier is the rank among the family's mods this base can roll, best (highest required level) first.
/// </summary>
public sealed class ItemAffixData
{
    public sealed record StatRange(string Id, int Min, int Max);

    /// <summary>A rollable prefix/suffix. <see cref="SpawnTags"/> are ordered: the first tag the base carries decides,
    /// "!tag" meaning weight 0 (cannot roll).</summary>
    public sealed record Affix(string Id, string Family, bool Prefix, bool Desecrated, int Level,
        IReadOnlyList<StatRange> Stats, IReadOnlyList<string> SpawnTags);

    /// <summary>An equipment base. Damage is the base physical range, <see cref="AttackTimeMs"/> the base attack time;
    /// defences are the base values (0 = none).</summary>
    public sealed record ItemBase(string Metadata, string Name, string Class, IReadOnlyList<string> Tags,
        int Armour, int Evasion, int EnergyShield, int DamageMin, int DamageMax, int AttackTimeMs);

    private readonly Dictionary<string, Affix> _affixes;
    private readonly Dictionary<string, ItemBase> _bases;
    private readonly ConcurrentDictionary<string, BaseIndex> _byBase = new(StringComparer.OrdinalIgnoreCase);

    public ItemAffixData(IEnumerable<Affix> affixes, IEnumerable<ItemBase> bases)
    {
        _affixes = affixes.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _bases = new Dictionary<string, ItemBase>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bases) _bases[b.Metadata] = b;
    }

    /// <summary>The shared table, loaded once from the embedded resource.</summary>
    public static ItemAffixData Shared { get; } = LoadEmbedded();

    public bool IsLoaded => _affixes.Count > 0 && _bases.Count > 0;

    public Affix? AffixFor(string modId) => _affixes.GetValueOrDefault(modId);

    /// <summary>The base for an item's metadata path (case-insensitive: the export mixes "Metadata/Items" and
    /// "Metadata/items").</summary>
    public ItemBase? BaseFor(string? metadata) => string.IsNullOrEmpty(metadata) ? null : _bases.GetValueOrDefault(metadata);

    /// <summary>Whether <paramref name="affix"/> can roll on <paramref name="itemBase"/> (first carried spawn tag decides).</summary>
    public static bool CanRoll(Affix affix, ItemBase itemBase)
    {
        foreach (var tag in affix.SpawnTags)
        {
            var allowed = !tag.StartsWith('!');
            var name = allowed ? tag : tag[1..];
            if (name == "default" || itemBase.Tags.Contains(name)) return allowed;
        }
        return false;
    }

    /// <summary>Tier of <paramref name="affix"/> on this base (1 = best) and how many tiers the family has here;
    /// null when the base can't roll it (essence-only, unique or foreign mods).</summary>
    public (int Tier, int Count)? TierOf(Affix affix, ItemBase itemBase)
    {
        var index = Index(itemBase);
        if (!index.Families.TryGetValue(FamilyKey(affix), out var family)) return null;
        var rank = family.FindIndex(a => a.Id == affix.Id);
        return rank < 0 ? null : (rank + 1, family.Count);
    }

    /// <summary>The highest value <paramref name="statId"/> reaches on any single affix this base can roll (0 = none).</summary>
    public int BestRoll(string statId, ItemBase itemBase) => Index(itemBase).Best.GetValueOrDefault(statId);

    /// <summary>Every affix this base can roll, best tier first within each family.</summary>
    public IEnumerable<Affix> Rollable(ItemBase itemBase) => Index(itemBase).Families.Values.SelectMany(f => f);

    private static string FamilyKey(Affix a) => $"{a.Family}|{(a.Prefix ? 'p' : 's')}|{(a.Desecrated ? 'd' : 'i')}";

    private sealed record BaseIndex(Dictionary<string, List<Affix>> Families, Dictionary<string, int> Best);

    private BaseIndex Index(ItemBase itemBase) => _byBase.GetOrAdd(itemBase.Metadata, _ =>
    {
        var families = new Dictionary<string, List<Affix>>(StringComparer.Ordinal);
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in _affixes.Values)
        {
            if (!CanRoll(a, itemBase)) continue;
            var key = FamilyKey(a);
            (families.TryGetValue(key, out var list) ? list : families[key] = new List<Affix>()).Add(a);
            foreach (var s in a.Stats)
            {
                var top = Math.Max(Math.Abs(s.Min), Math.Abs(s.Max));
                if (top > best.GetValueOrDefault(s.Id)) best[s.Id] = top;
            }
        }
        foreach (var list in families.Values)
            list.Sort((x, y) => y.Level != x.Level ? y.Level.CompareTo(x.Level)
                : (y.Stats.Count > 0 ? y.Stats[0].Max : 0).CompareTo(x.Stats.Count > 0 ? x.Stats[0].Max : 0));
        return new BaseIndex(families, best);
    });

    private static ItemAffixData LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("poe2_item_data", StringComparison.Ordinal));
            if (name == null) return new ItemAffixData([], []);
            using var stream = asm.GetManifestResourceStream(name)!;
            return Parse(stream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ItemAffixData load failed: {ex.Message}");
            return new ItemAffixData([], []);
        }
    }

    /// <summary>Parse the generated table (see the generator for the compact field names).</summary>
    public static ItemAffixData Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var affixes = new List<Affix>();
        foreach (var p in root.GetProperty("affixes").EnumerateObject())
        {
            var a = p.Value;
            var stats = a.GetProperty("s").EnumerateArray()
                .Select(s => new StatRange(s[0].GetString()!, s[1].GetInt32(), s[2].GetInt32())).ToArray();
            var tags = a.GetProperty("w").EnumerateArray().Select(t => t.GetString()!).ToArray();
            affixes.Add(new Affix(p.Name, a.GetProperty("f").GetString()!, a.GetProperty("g").GetString() == "p",
                a.GetProperty("d").GetString() == "d", a.GetProperty("l").GetInt32(), stats, tags));
        }
        var bases = new List<ItemBase>();
        foreach (var p in root.GetProperty("bases").EnumerateObject())
        {
            var b = p.Value;
            int Int(string key) => b.TryGetProperty(key, out var v) ? v.GetInt32() : 0;
            bases.Add(new ItemBase(p.Name, b.GetProperty("n").GetString()!, b.GetProperty("c").GetString()!,
                b.GetProperty("t").EnumerateArray().Select(t => t.GetString()!).ToArray(),
                Int("ar"), Int("ev"), Int("es"), Int("dmin"), Int("dmax"), Int("at")));
        }
        return new ItemAffixData(affixes, bases);
    }
}
