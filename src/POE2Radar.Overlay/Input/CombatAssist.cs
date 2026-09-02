using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Opt-in combat assist: tap the next ready skill in a rotation while a hostile monster is in grid range.
/// The decision is a pure function of the snapshot so tests never need a live client.
/// I/O (hotkey, TapKey, settings) stays in <c>RadarApp</c>.
/// </summary>
public static class CombatAssist
{
    /// <summary>One rotation slot. <see cref="Range"/> of 0 uses the snapshot's global range.</summary>
    public readonly record struct Skill(int Key, int CooldownMs, float Range = 0f);

    public readonly record struct Snapshot(
        bool Armed,
        bool Focused,
        bool InGame,
        NumVec2 PlayerGrid,
        IReadOnlyList<Poe2Live.EntityDot> Entities,
        float Range,
        DateTime NowUtc,
        IReadOnlyList<DateTime> LastFireUtc,
        IReadOnlyList<Skill> Skills,
        int NextIndex);

    public readonly record struct Decision(bool ShouldTap, ushort Vk, int SkillIndex, int NextIndex, string Note);

    /// <summary>
    /// Hostile = not friendly: <c>(Reaction &amp; 0x7F) != 1</c>. Only alive monsters count.
    /// Round-robin from <see cref="Snapshot.NextIndex"/>; skip a skill that is on cooldown or out of
    /// its own range; wrap the list. One tap per call.
    /// </summary>
    public static Decision Decide(in Snapshot s)
    {
        if (!s.Armed) return Idle(0, "OFF (F4)");
        if (!s.InGame) return Idle(0, "paused (not in game)");
        if (!s.Focused) return Idle(0, "paused (PoE2 not focused)");

        var skills = s.Skills;
        var n = skills?.Count ?? 0;
        if (n == 0) return Idle(0, "no skills");

        var cursor = s.NextIndex % n;
        if (cursor < 0) cursor += n;

        var globalRange = Math.Max(0f, s.Range);
        if (!HasHostileInRange(s.Entities, s.PlayerGrid, globalRange)) return Idle(cursor, "armed");

        var last = s.LastFireUtc;
        var lastN = last?.Count ?? 0;
        for (var i = 0; i < n; i++)
        {
            var idx = (cursor + i) % n;
            var sk = skills![idx];
            if (sk.Key is < 1 or > 255) continue;
            var skillRange = sk.Range > 0f ? sk.Range : globalRange;
            if (!HasHostileInRange(s.Entities, s.PlayerGrid, skillRange)) continue;
            var firedAt = idx < lastN ? last![idx] : DateTime.MinValue;
            var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, sk.CooldownMs));
            if (s.NowUtc - firedAt < cooldown) continue;
            return new(true, (ushort)sk.Key, idx, (idx + 1) % n, "fired");
        }

        return Idle(cursor, "armed");
    }

    private static Decision Idle(int next, string note) => new(false, 0, 0, next, note);

    /// <summary>
    /// Hostile = not friendly: <c>(Reaction &amp; 0x7F) != 1</c>. Only alive monsters count.
    /// Bot master uses this to halt quest pathing while a fight is on.
    /// </summary>
    public static bool HasHostileInRange(
        IReadOnlyList<Poe2Live.EntityDot>? entities,
        NumVec2 playerGrid,
        float range)
    {
        if (entities is null) return false;
        var rangeSq = Math.Max(0f, range) * Math.Max(0f, range);
        foreach (var e in entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster) continue;
            if (!e.IsAlive) continue;
            if ((e.Reaction & 0x7F) == 1) continue;
            var dx = e.Grid.X - playerGrid.X;
            var dy = e.Grid.Y - playerGrid.Y;
            if (dx * dx + dy * dy <= rangeSq) return true;
        }
        return false;
    }
}
