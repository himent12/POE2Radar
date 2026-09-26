using System.Globalization;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// One buff-keeper rule: press <see cref="Key"/> once to (re)cast a self-buff.
/// <list type="bullet">
/// <item><c>Missing</c>: recast when no active buff matches <see cref="BuffName"/>.</item>
/// <item><c>Expiring</c>: recast when the match is missing OR its (longest) TimeLeft &lt; <see cref="RefreshBelowSec"/>.
/// Infinite-duration buffs never expire.</item>
/// <item><c>Interval</c>: recast every <see cref="IntervalMs"/> on a timer; needs no buff reads, so it keeps
/// working when buffs are unreadable.</item>
/// </list>
/// <see cref="BuffName"/> is a case-insensitive substring of the internal buff id (e.g. "arcane_surge");
/// separate alternatives with '|'. <see cref="MinCharges"/> &gt; 0 also recasts (Missing/Expiring only) while the
/// matched buff has fewer charges. <see cref="MinGapMs"/> is the per-rule cooldown after a press: it covers the
/// cast animation / buff application so a Missing rule doesn't double-press while the buff appears.
/// </summary>
public sealed class BuffRule
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Win32 virtual-key code; 0 = unset (rule never fires).</summary>
    public int Key { get; set; }
    /// <summary>"Missing" | "Expiring" | "Interval" (case-insensitive).</summary>
    public string Trigger { get; set; } = BuffKeeper.TriggerMissing;
    public string BuffName { get; set; } = "";
    public float RefreshBelowSec { get; set; } = 2f;
    public int IntervalMs { get; set; } = 10000;
    public int MinGapMs { get; set; } = 1500;
    public bool OnlyNearHostiles { get; set; }
    /// <summary>Grid units.</summary>
    public float HostileRange { get; set; } = 60f;
    public bool SkipInTown { get; set; } = true;
    public int MinCharges { get; set; }
}

/// <summary>
/// Pure decision engine for the buff keeper — no I/O, no GameHost calls. The caller builds a
/// <see cref="Snapshot"/> each tick, taps <see cref="Decision.Key"/> when one is returned, then records the
/// press in <c>LastFiredUtc[RuleIndex]</c> and <c>LastAnyFiredUtc</c>.
/// <para>Safety: nothing fires unless armed, focused, in game and alive; buff-based triggers
/// (Missing/Expiring/MinCharges) never fire while buffs are unreadable (null = unknown); at most one key per
/// call; a global gap separates any two presses; rule order is priority.</para>
/// </summary>
public static class BuffKeeper
{
    public const string TriggerMissing = "Missing";
    public const string TriggerExpiring = "Expiring";
    public const string TriggerInterval = "Interval";

    /// <summary>Floors applied to user-entered timings so a misconfigured rule can't spam the key.</summary>
    public const int MinRuleGapMs = 250;
    public const int MinIntervalMs = 500;
    public const int MinGlobalGapMs = 50;

    /// <param name="HostileDistances">Grid distances from the player to live hostiles (null = none known).</param>
    /// <param name="Buffs">The player's buffs; null = unreadable → buff-based rules pause.</param>
    /// <param name="LastFiredUtc">Per-rule last press (index-aligned with <paramref name="Rules"/>; missing = never).</param>
    public readonly record struct Snapshot(
        DateTime NowUtc,
        bool Armed,
        bool Focused,
        bool InGame,
        bool Alive,
        bool InTown,
        IReadOnlyList<float>? HostileDistances,
        IReadOnlyList<Poe2Live.BuffInfo>? Buffs,
        IReadOnlyList<BuffRule> Rules,
        IReadOnlyList<DateTime> LastFiredUtc,
        DateTime LastAnyFiredUtc = default,
        int GlobalGapMs = 250);

    public readonly record struct Decision(int RuleIndex, int Key, string Note);

    /// <summary>The single key to press now (highest-priority rule that wants to fire), or null.</summary>
    public static Decision? Decide(Snapshot s)
    {
        if (GlobalBlock(s) is not null) return null;
        if (InGlobalGap(s)) return null;
        for (var i = 0; i < s.Rules.Count; i++)
        {
            var (fire, note) = Evaluate(s, i);
            if (fire) return new Decision(i, s.Rules[i].Key, note);
        }
        return null;
    }

    /// <summary>One human-readable status per rule (index-aligned with <see cref="Snapshot.Rules"/>) for the UI.</summary>
    public static IReadOnlyList<string> RuleNotes(Snapshot s)
    {
        var notes = new string[s.Rules.Count];
        var global = GlobalBlock(s);
        var gap = InGlobalGap(s);
        for (var i = 0; i < notes.Length; i++)
        {
            if (!s.Rules[i].Enabled) { notes[i] = "disabled"; continue; }
            if (global is not null) { notes[i] = global; continue; }
            var (fire, note) = Evaluate(s, i);
            notes[i] = fire && gap ? note + " (queued)" : note;
        }
        return notes;
    }

    private static string? GlobalBlock(Snapshot s)
        => !s.Armed ? "keeper is OFF"
            : !s.InGame ? "not in game"
            : !s.Focused ? "game not focused"
            : !s.Alive ? "dead"
            : null;

    private static bool InGlobalGap(Snapshot s)
        => s.LastAnyFiredUtc != default
            && (s.NowUtc - s.LastAnyFiredUtc).TotalMilliseconds < Math.Max(MinGlobalGapMs, s.GlobalGapMs);

    // Per-rule evaluation, ignoring the global gates. Returns whether this rule wants to press now + its note.
    private static (bool Fire, string Note) Evaluate(Snapshot s, int i)
    {
        var r = s.Rules[i];
        if (!r.Enabled) return (false, "disabled");
        if (r.Key <= 0 || r.Key > 0xFE) return (false, "no key set");
        var trigger = ParseTrigger(r.Trigger);
        if (trigger is null) return (false, $"unknown trigger '{r.Trigger}'");
        if (r.SkipInTown && s.InTown) return (false, "town — skipped");
        if (r.OnlyNearHostiles && !HostileNear(s.HostileDistances, r.HostileRange)) return (false, "waiting for hostiles");

        var last = i < s.LastFiredUtc.Count ? s.LastFiredUtc[i] : default;
        var sinceMs = last == default ? double.PositiveInfinity : (s.NowUtc - last).TotalMilliseconds;
        var gapMs = Math.Max(MinRuleGapMs, r.MinGapMs);

        if (trigger == TriggerInterval)
        {
            var interval = Math.Max(Math.Max(MinIntervalMs, r.IntervalMs), gapMs);
            if (sinceMs >= interval) return (true, "interval → recast");
            return (false, $"next in {Secs((interval - sinceMs) / 1000.0)}");
        }

        if (s.Buffs is null) return (false, "paused (buffs unreadable)");
        var alternatives = SplitAlternatives(r.BuffName);
        if (alternatives.Length == 0) return (false, "no buff name set");

        var match = FindBest(s.Buffs, alternatives);
        string want;
        if (match is not { } b) want = "missing → recast";
        else if (trigger == TriggerExpiring && !b.IsInfinite && b.TimeLeft < r.RefreshBelowSec)
            want = $"expiring {Secs(b.TimeLeft)} → recast";
        else if (r.MinCharges > 0 && b.Charges < r.MinCharges)
            want = $"charges {b.Charges}<{r.MinCharges} → recast";
        else
            return (false, ActiveNote(b));

        // The per-rule gap doubles as the post-press grace while the cast lands and the buff appears.
        if (sinceMs < gapMs) return (false, $"cooldown {Secs((gapMs - sinceMs) / 1000.0)} ({want.Split(' ')[0]})");
        return (true, want);
    }

    private static string? ParseTrigger(string? t)
    {
        var v = t?.Trim() ?? "";
        if (v.Length == 0 || v.Equals(TriggerMissing, StringComparison.OrdinalIgnoreCase)) return TriggerMissing;
        if (v.Equals(TriggerExpiring, StringComparison.OrdinalIgnoreCase)) return TriggerExpiring;
        if (v.Equals(TriggerInterval, StringComparison.OrdinalIgnoreCase)) return TriggerInterval;
        return null;
    }

    private static bool HostileNear(IReadOnlyList<float>? distances, float range)
    {
        if (distances is null) return false;
        foreach (var d in distances)
            if (d >= 0 && d <= range) return true;
        return false;
    }

    internal static string[] SplitAlternatives(string? buffName)
        => (buffName ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The matching buff with the most time left (several instances can share a name), or null.</summary>
    internal static Poe2Live.BuffInfo? FindBest(IReadOnlyList<Poe2Live.BuffInfo> buffs, string[] alternatives)
    {
        Poe2Live.BuffInfo? best = null;
        foreach (var b in buffs)
        {
            if (!Matches(b.Name, alternatives)) continue;
            if (best is not { } cur || b.TimeLeft > cur.TimeLeft || (b.TimeLeft == cur.TimeLeft && b.Charges > cur.Charges))
                best = b;
        }
        return best;
    }

    private static bool Matches(string name, string[] alternatives)
    {
        foreach (var a in alternatives)
            if (name.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ActiveNote(Poe2Live.BuffInfo b)
    {
        var t = b.IsInfinite ? "active ∞" : $"active {Secs(b.TimeLeft)}";
        return b.Charges > 0 ? $"{t} ×{b.Charges}" : t;
    }

    private static string Secs(double seconds)
        => Math.Max(0, seconds).ToString("0.0", CultureInfo.InvariantCulture) + "s";
}
