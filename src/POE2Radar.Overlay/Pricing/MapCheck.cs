using System.Text.RegularExpressions;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>
/// Waystone mod checker (PoE Overlay's "map checker"): flags a hovered waystone's rendered mod lines against
/// the user's dangerous-mod list before they commit to running it. Patterns are case-insensitive substrings,
/// or a .NET regex when prefixed with <c>re:</c> (bounded by a timeout so a bad pattern can't stall the scan).
/// </summary>
public static class MapCheck
{
    public sealed record Result(IReadOnlyList<string> Flagged, int ModCount, bool Complete);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>A waystone by metadata path (Maps / MapKey items) or, as a fallback, by its base name.</summary>
    public static bool IsWaystone(string? metadata, string? name)
        => metadata is { Length: > 0 } m && (m.Contains("/Maps/", StringComparison.Ordinal) || m.Contains("MapKey", StringComparison.Ordinal))
           || name?.Contains("Waystone", StringComparison.OrdinalIgnoreCase) == true;

    public static Result Check(ItemTradeProfile profile, IReadOnlyList<string> patterns)
    {
        var flagged = new List<string>();
        foreach (var mod in profile.Mods)
            if (patterns.Any(p => Matches(mod.Text, p)))
                flagged.Add(mod.Text);
        return new Result(flagged, profile.Mods.Count, profile.Complete);
    }

    public static bool Matches(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (pattern.StartsWith("re:", StringComparison.Ordinal))
        {
            try { return Regex.IsMatch(text, pattern[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return false; }
        }
        return text.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hover-panel text for a checked waystone: headline + the flagged lines (or an all-clear).</summary>
    public static (string Text, string Detail, bool Danger) Describe(string name, Result r)
    {
        if (!r.Complete && r.ModCount == 0)
            return ("Waystone: mods unreadable", name + "\nUnidentified or mods could not be read.", false);
        if (r.Flagged.Count == 0)
            return ($"Waystone OK · {r.ModCount} mods, none flagged", name + "\nEdit the dangerous-mod list on the dashboard (Item Value).", false);
        var detail = name + "\n" + string.Join("\n", r.Flagged.Select(f => "! " + f));
        return ($"DANGER: {r.Flagged.Count} dangerous mod{(r.Flagged.Count == 1 ? "" : "s")}", detail, true);
    }
}
