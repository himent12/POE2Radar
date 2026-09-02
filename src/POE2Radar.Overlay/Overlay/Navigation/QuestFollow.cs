using System.Text;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Navigation;

/// <summary>
/// Quest-follow nav picker: given the current area's zone notes plus live landmarks/entities,
/// choose ONE existing nav-target id (<c>t:path@x,y</c> / <c>e:entityId</c>) so
/// <c>MaintainRoutes</c> can A* it. On arrival, <see cref="DecideUse"/> taps interact/use.
/// Pure functions — tests never need a live client.
/// </summary>
public static class QuestFollow
{
    /// <summary>A tile landmark the picker can select. <see cref="Id"/> is the nav id
    /// (<c>t:</c> + <see cref="Poe2Live.Landmark.Key"/>).</summary>
    public readonly record struct LandmarkHint(string Id, string Name, string? CuratedName, string Path);

    /// <summary>A live entity the picker can select. <see cref="Id"/> is the nav id
    /// (<c>e:</c> + entity id). <see cref="Name"/> is the display label (curated or prettified).
    /// <see cref="Grid"/> is the live position used to pick the nearest unique boss.</summary>
    public readonly record struct EntityHint(
        string Id,
        string Name,
        string Metadata,
        bool Poi,
        bool UniqueMonster,
        Poe2Live.EntityCategory Category,
        NumVec2 Grid = default);

    public readonly record struct UseSnapshot(
        bool Armed,
        bool Focused,
        bool InGame,
        NumVec2 PlayerGrid,
        bool HasTarget,
        NumVec2 TargetGrid,
        float ArriveRadius,
        DateTime NowUtc,
        DateTime LastFireUtc,
        int CooldownMs,
        int UseKey,
        bool PauseForCombat = false);

    public readonly record struct UseDecision(bool ShouldTap, ushort Vk, string Note);

    private static readonly Regex DirectedRx = new(
        @"\b(exit(?:\s*/\s*tp)?|tp(?:\s*/\s*exit)?|checkpoint|waypoint)\s*>\s*([^>\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "back", "be", "boss", "by", "checkpoint", "come",
        "enter", "exit", "for", "from", "get", "got", "grab", "has", "have", "if", "in", "into",
        "is", "kill", "layout", "luck", "of", "on", "optional", "or", "over", "skippable", "the",
        "then", "this", "that", "to", "town", "tp", "usually", "waypoint", "with", "you", "your",
        "zone", "area",
    };

    private static readonly string[] DestCuts =
        [" usually", " then ", " for ", " or ", " if ", " to ", ",", ";", " — ", " – "];

    /// <summary>
    /// Nearest live unique monster (boss), or null. Quest follow and map-clear both retarget
    /// onto a unique the moment it appears in the entity list.
    /// </summary>
    public static string? PickBoss(IReadOnlyList<EntityHint> entities, NumVec2 player)
    {
        if (entities is null) return null;
        string? best = null;
        var bestD = float.MaxValue;
        foreach (var e in entities)
        {
            if (!e.UniqueMonster) continue;
            var dx = e.Grid.X - player.X;
            var dy = e.Grid.Y - player.Y;
            var d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = e.Id; }
        }
        return best;
    }

    /// <summary>
    /// Pick a nav-target id for this zone, or null when nothing matches.
    /// Towns/hideouts return null. Matching order: live unique boss; note
    /// <c>Exit/Checkpoint/Waypoint &gt; name</c> tokens against landmarks (transitions first)
    /// then entity POIs; then any landmark/POI whose name appears in the notes; then a
    /// Transition / waypoint / boss fallback.
    /// </summary>
    public static string? PickTarget(
        string areaCode,
        string notes,
        IReadOnlyList<LandmarkHint> landmarks,
        IReadOnlyList<EntityHint> entities,
        NumVec2 player = default)
    {
        if (IsTownOrHideout(areaCode)) return null;
        var boss = PickBoss(entities, player);
        if (boss is not null) return boss;
        notes ??= "";
        var directed = ExtractDirected(notes);

        foreach (var dest in directed)
        {
            var hit = BestLandmark(dest, landmarks, preferTransition: true);
            if (hit is not null) return hit.Value.Id;
        }

        foreach (var dest in directed)
        {
            var hit = BestEntity(dest, entities);
            if (hit is not null) return hit.Value.Id;
        }

        LandmarkHint? noteLm = null;
        var noteLmTransition = false;
        foreach (var lm in landmarks)
        {
            if (!NameAppearsInNotes(notes, DisplayName(lm))) continue;
            var trans = IsTransitionLandmark(lm);
            if (noteLm is null || (trans && !noteLmTransition))
            {
                noteLm = lm;
                noteLmTransition = trans;
                if (trans) break;
            }
        }
        if (noteLm is { } nlm) return nlm.Id;

        foreach (var e in entities)
        {
            if (!(e.Poi || e.UniqueMonster || e.Category == Poe2Live.EntityCategory.Transition)) continue;
            if (NameAppearsInNotes(notes, e.Name)) return e.Id;
        }

        foreach (var lm in landmarks)
            if (IsTransitionLandmark(lm)) return lm.Id;
        foreach (var e in entities)
            if (e.Category == Poe2Live.EntityCategory.Transition) return e.Id;
        foreach (var lm in landmarks)
            if (IsWaypointLandmark(lm)) return lm.Id;
        foreach (var e in entities)
            if (IsWaypointEntity(e)) return e.Id;
        foreach (var lm in landmarks)
            if (IsBossLandmark(lm)) return lm.Id;
        foreach (var e in entities)
            if (e.UniqueMonster) return e.Id;

        return null;
    }

    /// <summary>
    /// Tap interact/use when the player is inside <see cref="UseSnapshot.ArriveRadius"/> of the
    /// selected quest target. Same gates as path move (armed + focused + in-game + cooldown).
    /// </summary>
    public static UseDecision DecideUse(in UseSnapshot s)
    {
        if (!s.Armed) return new(false, 0, "OFF (F3)");
        if (!s.InGame) return new(false, 0, "paused (not in game)");
        if (!s.Focused) return new(false, 0, "paused (PoE2 not focused)");
        if (s.PauseForCombat) return new(false, 0, "combat");
        if (!s.HasTarget) return new(false, 0, "armed (no target)");
        if (s.UseKey is < 1 or > 255) return new(false, 0, "armed");

        var radius = Math.Max(0f, s.ArriveRadius);
        var dx = s.TargetGrid.X - s.PlayerGrid.X;
        var dy = s.TargetGrid.Y - s.PlayerGrid.Y;
        if (dx * dx + dy * dy > radius * radius) return new(false, 0, "armed");

        var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, s.CooldownMs));
        if (s.NowUtc - s.LastFireUtc < cooldown) return new(false, 0, "armed");

        return new(true, (ushort)s.UseKey, "use");
    }

    internal static bool IsTownOrHideout(string areaCode)
        => !string.IsNullOrEmpty(areaCode)
           && (areaCode.Contains("town", StringComparison.OrdinalIgnoreCase)
               || areaCode.Contains("hideout", StringComparison.OrdinalIgnoreCase));

    internal static List<string> ExtractDirected(string notes)
    {
        var ranked = new List<(int rank, int index, string dest)>();
        foreach (Match m in DirectedRx.Matches(notes))
        {
            var kind = m.Groups[1].Value;
            var dest = CleanDest(m.Groups[2].Value);
            if (dest.Length == 0 || IsStopToken(dest)) continue;
            ranked.Add((KindRank(kind), m.Index, dest));
        }
        ranked.Sort((a, b) =>
        {
            var c = a.rank.CompareTo(b.rank);
            return c != 0 ? c : a.index.CompareTo(b.index);
        });
        var list = new List<string>(ranked.Count);
        foreach (var r in ranked)
            if (!list.Contains(r.dest, StringComparer.OrdinalIgnoreCase))
                list.Add(r.dest);
        return list;
    }

    private static int KindRank(string kind)
    {
        var k = kind.ToLowerInvariant();
        if (k.StartsWith("exit", StringComparison.Ordinal)) return 0;
        if (k.StartsWith("checkpoint", StringComparison.Ordinal)) return 1;
        if (k.StartsWith("waypoint", StringComparison.Ordinal)) return 2;
        return 3;
    }

    private static string CleanDest(string raw)
    {
        var s = raw.Trim();
        foreach (var cut in DestCuts)
        {
            var i = s.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i];
        }
        return s.Trim();
    }

    private static LandmarkHint? BestLandmark(string dest, IReadOnlyList<LandmarkHint> landmarks, bool preferTransition)
    {
        LandmarkHint? best = null;
        var bestScore = -1;
        foreach (var lm in landmarks)
        {
            if (!NamesMatch(dest, DisplayName(lm)) && !NamesMatch(dest, lm.Name)) continue;
            var score = 1;
            if (preferTransition && IsTransitionLandmark(lm)) score += 2;
            if (IsWaypointLandmark(lm)) score += 1;
            if (score > bestScore) { bestScore = score; best = lm; }
        }
        return best;
    }

    private static EntityHint? BestEntity(string dest, IReadOnlyList<EntityHint> entities)
    {
        foreach (var e in entities)
        {
            if (NamesMatch(dest, e.Name)) return e;
        }
        return null;
    }

    private static string DisplayName(in LandmarkHint lm)
        => !string.IsNullOrEmpty(lm.CuratedName) ? lm.CuratedName : lm.Name;

    internal static bool IsTransitionLandmark(in LandmarkHint lm)
        => ContainsToken(lm.Path, "transition")
           || ContainsToken(lm.Name, "transition")
           || ContainsToken(lm.CuratedName, "transition");

    internal static bool IsWaypointLandmark(in LandmarkHint lm)
        => ContainsToken(lm.Path, "waypoint")
           || ContainsToken(lm.Name, "waypoint")
           || ContainsToken(lm.CuratedName, "waypoint");

    internal static bool IsBossLandmark(in LandmarkHint lm)
        => ContainsToken(lm.Path, "boss") || ContainsToken(lm.Path, "arena")
           || ContainsToken(lm.Name, "boss") || ContainsToken(lm.Name, "arena")
           || ContainsToken(lm.CuratedName, "boss") || ContainsToken(lm.CuratedName, "arena");

    private static bool IsWaypointEntity(in EntityHint e)
        => ContainsToken(e.Metadata, "waypoint") || ContainsToken(e.Name, "waypoint");

    private static bool ContainsToken(string? hay, string needle)
        => !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    internal static bool NamesMatch(string a, string b)
    {
        var na = StripThe(Normalize(a));
        var nb = StripThe(Normalize(b));
        if (na.Length < 4 || nb.Length < 4) return false;
        if (na == nb) return true;
        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal)) return true;

        var ta = SignificantTokens(na);
        var tb = SignificantTokens(nb);
        if (ta.Count == 0 || tb.Count == 0) return false;
        var shorter = ta.Count <= tb.Count ? ta : tb;
        var longer = ta.Count <= tb.Count ? tb : ta;
        foreach (var t in shorter)
            if (!FuzzyIn(longer, t)) return false;
        return true;
    }

    internal static bool NameAppearsInNotes(string notes, string name)
    {
        var n = StripThe(Normalize(name));
        if (n.Length < 5 || IsStopToken(n)) return false;
        var hay = Normalize(notes);
        if (hay.Contains(n, StringComparison.Ordinal)) return true;
        var tokens = SignificantTokens(n);
        if (tokens.Count == 0) return false;
        foreach (var t in tokens)
            if (!FuzzyIn(hay.Split(' ', StringSplitOptions.RemoveEmptyEntries), t)
                && !hay.Contains(t, StringComparison.Ordinal))
                return false;
        return tokens.Count > 0;
    }

    private static bool IsStopToken(string s)
    {
        var n = StripThe(Normalize(s));
        return n.Length == 0 || Stop.Contains(n);
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var paren = 0;
        var prevSpace = true;
        foreach (var ch in s)
        {
            if (ch == '(') { paren++; continue; }
            if (ch == ')') { if (paren > 0) paren--; continue; }
            if (paren > 0) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static string StripThe(string s)
        => s.StartsWith("the ", StringComparison.Ordinal) ? s[4..] : s;

    private static List<string> SignificantTokens(string normalized)
    {
        var list = new List<string>();
        foreach (var part in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 4 || Stop.Contains(part)) continue;
            list.Add(part);
        }
        return list;
    }

    private static bool FuzzyIn(IReadOnlyList<string> words, string needle)
    {
        foreach (var w in words)
            if (FuzzyEqual(w, needle)) return true;
        return false;
    }

    private static bool FuzzyEqual(string a, string b)
    {
        if (a == b) return true;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return true;
        if (Math.Abs(a.Length - b.Length) > 2) return false;
        var max = Math.Max(a.Length, b.Length) >= 6 ? 1 : 0;
        return max > 0 && EditDistance(a, b) <= max;
    }

    private static int EditDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;
        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }
}
