using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Navigation;

/// <summary>
/// In-map "click it" events the bot can run while clearing: essence crystals, strongboxes, shrines,
/// breach hands, ritual altars, ordinary chests. Pure: metadata pattern → event kind, plus the
/// "imprisoned monster" rule (a monster standing on an unopened essence is untargetable until the
/// crystal is clicked, so it must not count as a fight). Execution (walk + aim + click) lives in
/// <c>RadarApp</c>; enable/disable per kind in <c>RadarSettings</c>.
/// </summary>
public static class MapEvents
{
    public enum Kind { None, Essence, Strongbox, Shrine, Breach, Ritual, Chest, Stalled }

    // A record CLASS so `new Options()` yields the documented defaults (a record struct's parameterless
    // constructor would zero every flag).
    public sealed record Options(
        bool Essence = true,
        bool Strongbox = true,
        bool Shrine = true,
        bool Breach = true,
        bool Ritual = false,
        bool Chests = false,
        bool ClickStalled = true);

    public readonly record struct Event(uint Id, Kind Kind, NumVec2 Grid, Vector3 World, string Label);

    /// <summary>Metadata → event kind. Order matters: strongboxes are chests, essences are objects.</summary>
    public static Kind Classify(in Poe2Live.EntityDot e)
    {
        var m = e.Metadata;
        if (string.IsNullOrEmpty(m)) return Kind.None;
        if (m.Contains("StrongBox", StringComparison.OrdinalIgnoreCase)) return Kind.Strongbox;
        if (m.Contains("/Shrines/", StringComparison.OrdinalIgnoreCase)) return Kind.Shrine;
        if (m.Contains("Essence", StringComparison.OrdinalIgnoreCase) && e.Category != Poe2Live.EntityCategory.Monster) return Kind.Essence;
        if (m.Contains("Breach", StringComparison.OrdinalIgnoreCase) && e.Category != Poe2Live.EntityCategory.Monster) return Kind.Breach;
        if (m.Contains("Ritual", StringComparison.OrdinalIgnoreCase) && e.Category != Poe2Live.EntityCategory.Monster) return Kind.Ritual;
        if (e.Category == Poe2Live.EntityCategory.Chest) return Kind.Chest;
        return Kind.None;
    }

    public static bool Enabled(Kind k, Options o) => k switch
    {
        Kind.Essence => o.Essence,
        Kind.Strongbox => o.Strongbox,
        Kind.Shrine => o.Shrine,
        Kind.Breach => o.Breach,
        Kind.Ritual => o.Ritual,
        Kind.Chest => o.Chests,
        Kind.Stalled => o.ClickStalled,
        _ => false,
    };

    /// <summary>
    /// Pending events: enabled kinds that are not yet completed (opened chest / IconComplete) and not on
    /// the <paramref name="blacklist"/>. <paramref name="stalledMonsters"/> = hostiles the fight watchdog gave
    /// up on (essence-encased rares show up exactly like that) — they become "click me" targets too.
    /// </summary>
    public static List<Event> Pending(
        IReadOnlyList<Poe2Live.EntityDot> entities,
        Options options,
        IReadOnlyCollection<uint>? blacklist = null,
        IReadOnlyCollection<uint>? stalledMonsters = null)
    {
        var list = new List<Event>();
        foreach (var e in entities)
        {
            if (blacklist is { Count: > 0 } && blacklist.Contains(e.Id)) continue;
            if (e.IconComplete || e.Opened) continue;
            var kind = Classify(e);
            if (kind == Kind.None && stalledMonsters is { Count: > 0 } && stalledMonsters.Contains(e.Id) && e.IsAlive)
                kind = Kind.Stalled;
            if (kind == Kind.None || !Enabled(kind, options)) continue;
            list.Add(new Event(e.Id, kind, e.Grid, e.World, kind.ToString()));
        }
        return list;
    }

    /// <summary>
    /// Monsters standing within <paramref name="radius"/> cells of an unopened essence crystal are imprisoned:
    /// immune until the crystal is clicked. Exclude them from combat/targeting so the bot clicks instead of swinging.
    /// </summary>
    public static HashSet<uint> ImprisonedMonsters(IReadOnlyList<Poe2Live.EntityDot> entities, float radius = 5f)
    {
        var crystals = new List<NumVec2>();
        foreach (var e in entities)
            if (!e.IconComplete && !e.Opened && Classify(e) == Kind.Essence) crystals.Add(e.Grid);
        var result = new HashSet<uint>();
        if (crystals.Count == 0) return result;
        var r2 = radius * radius;
        foreach (var e in entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster) continue;
            foreach (var c in crystals)
                if (NumVec2.DistanceSquared(c, e.Grid) <= r2) { result.Add(e.Id); break; }
        }
        return result;
    }

    /// <summary>Nearest pending event within <paramref name="maxDistance"/> (0 = any), or null.</summary>
    public static Event? Nearest(IReadOnlyList<Event> events, NumVec2 player, float maxDistance = 0f)
    {
        Event? best = null;
        var bestD = maxDistance > 0f ? maxDistance * maxDistance : float.MaxValue;
        foreach (var ev in events)
        {
            var d = NumVec2.DistanceSquared(ev.Grid, player);
            if (d <= bestD && (best is null || d < bestD)) { bestD = d; best = ev; }
        }
        return best;
    }
}
