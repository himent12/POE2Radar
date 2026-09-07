using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Opt-in combat assist: fire skills from a configurable rotation at a chosen hostile.
/// The decision is a pure function of the snapshot so tests never need a live client.
/// I/O (hotkey, TapKey, cursor aim, settings) stays in <c>RadarApp</c>.
/// </summary>
public static class CombatAssist
{
    /// <summary>How the primary target is chosen among hostiles inside the global range.</summary>
    public enum TargetMode { Nearest, Rarity, LowestHp, HighestHp }

    /// <summary>
    /// One rotation slot.
    /// <see cref="Range"/> 0 = use the snapshot's global range.
    /// <see cref="MinTargets"/> = only fire when at least this many hostiles are inside the skill's range (AoE gating).
    /// <see cref="RareOnly"/> = only fire when the target is rare or unique (single-target nukes / curses).
    /// <see cref="HpBelowPct"/> &gt; 0 = only fire while the PLAYER's life is under this % (defensive skills, guard skills).
    /// <see cref="Enabled"/> false = slot kept but skipped.
    /// </summary>
    /// <para>Combo controls: <see cref="Repeat"/> taps the key N times (<see cref="RepeatGapMs"/> apart),
    /// <see cref="HoldMs"/> &gt; 0 holds it instead of tapping (channelled skills), <see cref="DodgeAfter"/> rolls
    /// away from the target when the casts are done, and <see cref="NextDelayMs"/> blocks the whole rotation
    /// for that long afterwards (animation / combo timing).</para>
    public readonly record struct Skill(
        int Key,
        int CooldownMs,
        float Range = 0f,
        int MinTargets = 1,
        bool RareOnly = false,
        float HpBelowPct = 0f,
        bool Enabled = true,
        int Repeat = 1,
        int RepeatGapMs = 150,
        int HoldMs = 0,
        bool DodgeAfter = false,
        int NextDelayMs = 0,
        float ManaBelowPct = 0f,
        float MinManaPct = 0f,
        float EsBelowPct = 0f,
        float TargetHpBelowPct = 0f,
        bool RequireTarget = true,
        bool Priority = false,
        string AimMode = "Target",
        bool AnyLowResource = false,
        int Modifiers = 0);

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
        int NextIndex,
        IReadOnlyCollection<uint>? IgnoreIds = null,
        TargetMode Mode = TargetMode.Nearest,
        bool PriorityOrder = false,
        float PlayerHpPct = 100f,
        bool KeyboardOnly = false,    // background mode on Linux: mouse-bound skills cannot be delivered
        bool Busy = false,            // a combo macro is still executing → no new decision
        uint PreferredTargetId = 0,
        float PlayerManaPct = 100f,
        float PlayerEsPct = 100f,
        bool HasEs = false,
        bool VitalsKnown = true,
        bool Fleeing = false);  // stick to this target while it is alive and in range (no hopping between mobs)

    /// <summary>
    /// <see cref="HasTarget"/> + <see cref="TargetGrid"/> / <see cref="TargetWorld"/> name the hostile the
    /// tap is aimed at (the caller warps the cursor onto it so the skill fires toward the monster instead
    /// of wherever the cursor was left). Set whenever a hostile is in the global range, tap or not.
    /// </summary>
    public readonly record struct Decision(
        bool ShouldTap,
        ushort Vk,
        int SkillIndex,
        int NextIndex,
        string Note,
        bool HasTarget = false,
        NumVec2 TargetGrid = default,
        Vector3 TargetWorld = default,
        uint TargetId = 0,
        int HostilesInRange = 0);

    /// <summary>
    /// Hostile = not friendly: <c>(Reaction &amp; 0x7F) != 1</c>. Only alive monsters count.
    /// Picks the target per <see cref="Snapshot.Mode"/>, then walks the rotation — round-robin from
    /// <see cref="Snapshot.NextIndex"/>, or strict list order when <see cref="Snapshot.PriorityOrder"/> —
    /// skipping slots that are disabled, cooling down, out of range of the target, below their
    /// <see cref="Skill.MinTargets"/> count, rare-only against a normal, or HP-gated. One tap per call.
    /// </summary>
public static Decision Decide(in Snapshot s)
    {
        if (!s.Armed) return Idle(0, "OFF (F4)");
        if (!s.InGame) return Idle(0, "paused (not in game)");
        if (!s.Focused) return Idle(0, "paused (PoE2 not focused)");
        if (s.VitalsKnown && s.PlayerHpPct <= 0f) return Idle(s.NextIndex, "paused (dead)");
        if (s.Busy) return Idle(s.NextIndex, "casting");
        var skills = s.Skills;
        var n = skills?.Count ?? 0;
        if (n == 0) return Idle(0, "no skills");
        var cursor = ((s.NextIndex % n) + n) % n;
        var globalRange = Math.Max(0f, s.Range);
        var hasTarget = TryPickTarget(s.Entities, s.PlayerGrid, globalRange, out var target, s.IgnoreIds, s.Mode);
        if (hasTarget && s.PreferredTargetId != 0 && s.PreferredTargetId != target.Id
            && TryFindHostile(s.Entities, s.PreferredTargetId, s.PlayerGrid, globalRange, s.IgnoreIds, out var kept)
            && (s.Mode != TargetMode.Rarity || RarityRank(kept.Rarity) >= RarityRank(target.Rarity)))
            target = kept;
        var inGlobal = CountHostilesInRange(s.Entities, s.PlayerGrid, globalRange, s.IgnoreIds);
        var last = s.LastFireUtc;
        // Emergency slots precede the rotation, but never interrupt a held-key macro.
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < n; i++)
        {
            var idx = pass == 0 || s.PriorityOrder ? i : (cursor + i) % n;
            var sk = skills![idx];
            if (sk.Priority != (pass == 0) || !sk.Enabled) continue;
            if (s.Fleeing && !sk.Priority) continue;
            if (sk.Key is < 1 or > 255) continue;
            if (sk.Modifiers is < 0 or > 7) continue;
            if (s.KeyboardOnly && sk.Key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06) continue;
            var needsTarget = sk.RequireTarget || sk.AimMode != "Cursor" || sk.RareOnly || sk.TargetHpBelowPct > 0f;
            if (needsTarget && !hasTarget) continue;
            var skillRange = sk.Range > 0f ? sk.Range : globalRange;
            if (needsTarget && hasTarget && NumVec2.DistanceSquared(target.Grid, s.PlayerGrid) > skillRange * skillRange) continue;
            if (sk.RareOnly && target.Rarity is not (Poe2Live.Rarity.Rare or Poe2Live.Rarity.Unique)) continue;
            var resourceGate = sk.HpBelowPct > 0f || sk.ManaBelowPct > 0f || sk.MinManaPct > 0f || sk.EsBelowPct > 0f;
            if (resourceGate && !s.VitalsKnown) continue;
            var lowConditions = 0;
            var matchedLowConditions = 0;
            if (sk.HpBelowPct > 0f)
            {
                lowConditions++;
                if (float.IsFinite(s.PlayerHpPct) && s.PlayerHpPct < sk.HpBelowPct) matchedLowConditions++;
            }
            if (sk.ManaBelowPct > 0f)
            {
                lowConditions++;
                if (float.IsFinite(s.PlayerManaPct) && s.PlayerManaPct < sk.ManaBelowPct) matchedLowConditions++;
            }
            if (sk.EsBelowPct > 0f)
            {
                lowConditions++;
                if (s.HasEs && float.IsFinite(s.PlayerEsPct) && s.PlayerEsPct < sk.EsBelowPct) matchedLowConditions++;
            }
            if (lowConditions > 0 && (sk.AnyLowResource ? matchedLowConditions == 0 : matchedLowConditions != lowConditions)) continue;
            if (sk.MinManaPct > 0f && (!float.IsFinite(s.PlayerManaPct) || s.PlayerManaPct < sk.MinManaPct)) continue;
            if (sk.TargetHpBelowPct > 0f && (target.HpMax <= 0 || 100f * target.HpCur / target.HpMax >= sk.TargetHpBelowPct)) continue;
            if (sk.MinTargets > 0)
            {
                var count = sk.Range > 0f ? CountHostilesInRange(s.Entities, s.PlayerGrid, skillRange, s.IgnoreIds) : inGlobal;
                if (count < sk.MinTargets) continue;
            }
            var firedAt = idx < (last?.Count ?? 0) ? last![idx] : DateTime.MinValue;
            if (s.NowUtc - firedAt < TimeSpan.FromMilliseconds(Math.Max(0, sk.CooldownMs))) continue;
            var next = sk.Priority || s.PriorityOrder ? cursor : (idx + 1) % n;
            return new(true, (ushort)sk.Key, idx, next, sk.Priority ? "priority cast" : "fired",
                HasTarget: hasTarget, TargetGrid: target.Grid, TargetWorld: target.World, TargetId: target.Id, HostilesInRange: inGlobal);
        }
        return new(false, 0, 0, cursor, "armed", HasTarget: hasTarget,
            TargetGrid: target.Grid, TargetWorld: target.World, TargetId: target.Id, HostilesInRange: inGlobal);
    }

    private static Decision Idle(int next, string note) => new(false, 0, 0, next, note);

    /// <summary>Alive, non-friendly monster (<c>(Reaction &amp; 0x7F) != 1</c>).</summary>
    public static bool IsHostile(in Poe2Live.EntityDot e)
        => e.Category == Poe2Live.EntityCategory.Monster && e.IsAlive && (e.Reaction & 0x7F) != 1;

    /// <summary>
    /// Primary target inside <paramref name="range"/> per <paramref name="mode"/>:
    /// Nearest — closest; Rarity — unique &gt; rare &gt; magic &gt; normal, then closest;
    /// LowestHp / HighestHp — by HP fraction (finish-off vs. focus-the-tank), then closest.
    /// Ids in <paramref name="ignore"/> (fights the watchdog gave up on) are skipped.
    /// </summary>
    public static bool TryPickTarget(
        IReadOnlyList<Poe2Live.EntityDot>? entities,
        NumVec2 playerGrid,
        float range,
        out Poe2Live.EntityDot target,
        IReadOnlyCollection<uint>? ignore = null,
        TargetMode mode = TargetMode.Nearest)
    {
        target = default;
        if (entities is null) return false;
        var r = Math.Max(0f, range);
        var rangeSq = r * r;
        var found = false;
        var bestPrimary = float.MinValue;
        var bestD = float.MaxValue;
        foreach (var e in entities)
        {
            if (!IsHostile(e)) continue;
            if (ignore is { Count: > 0 } && ignore.Contains(e.Id)) continue;
            var dx = e.Grid.X - playerGrid.X;
            var dy = e.Grid.Y - playerGrid.Y;
            var d = dx * dx + dy * dy;
            if (d > rangeSq) continue;
            var primary = mode switch
            {
                TargetMode.Rarity => RarityRank(e.Rarity),
                TargetMode.LowestHp => -e.HpFraction,
                TargetMode.HighestHp => e.HpFraction,
                _ => 0f,
            };
            if (!found || primary > bestPrimary + 1e-4f || (MathF.Abs(primary - bestPrimary) <= 1e-4f && d < bestD))
            {
                found = true;
                bestPrimary = primary;
                bestD = d;
                target = e;
            }
        }
        return found;
    }

    private static bool TryFindHostile(IReadOnlyList<Poe2Live.EntityDot>? entities, uint id, NumVec2 player, float range,
        IReadOnlyCollection<uint>? ignore, out Poe2Live.EntityDot found)
    {
        found = default;
        if (entities is null) return false;
        var rangeSq = Math.Max(0f, range) * Math.Max(0f, range);
        foreach (var e in entities)
        {
            if (e.Id != id) continue;
            if (!IsHostile(e)) return false;
            if (ignore is { Count: > 0 } && ignore.Contains(e.Id)) return false;
            var dx = e.Grid.X - player.X; var dy = e.Grid.Y - player.Y;
            if (dx * dx + dy * dy > rangeSq) return false;
            found = e; return true;
        }
        return false;
    }

    public static int CountHostilesInRange(
        IReadOnlyList<Poe2Live.EntityDot>? entities,
        NumVec2 playerGrid,
        float range,
        IReadOnlyCollection<uint>? ignore = null)
    {
        if (entities is null) return 0;
        var rangeSq = Math.Max(0f, range) * Math.Max(0f, range);
        var count = 0;
        foreach (var e in entities)
        {
            if (!IsHostile(e)) continue;
            if (ignore is { Count: > 0 } && ignore.Contains(e.Id)) continue;
            var dx = e.Grid.X - playerGrid.X;
            var dy = e.Grid.Y - playerGrid.Y;
            if (dx * dx + dy * dy <= rangeSq) count++;
        }
        return count;
    }

    private static int RarityRank(Poe2Live.Rarity r) => r switch
    {
        Poe2Live.Rarity.Unique => 3,
        Poe2Live.Rarity.Rare => 2,
        Poe2Live.Rarity.Magic => 1,
        _ => 0,
    };

    public static TargetMode ParseTargetMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rarity" => TargetMode.Rarity,
        "lowesthp" or "lowest" or "weakest" => TargetMode.LowestHp,
        "highesthp" or "highest" or "tank" => TargetMode.HighestHp,
        _ => TargetMode.Nearest,
    };

    /// <summary>
    /// Hostile = not friendly: <c>(Reaction &amp; 0x7F) != 1</c>. Only alive monsters count.
    /// Bot master uses this to halt pathing while a fight is on.
    /// </summary>
    public static bool HasHostileInRange(
        IReadOnlyList<Poe2Live.EntityDot>? entities,
        NumVec2 playerGrid,
        float range,
        IReadOnlyCollection<uint>? ignore = null)
        => CountHostilesInRange(entities, playerGrid, range, ignore) > 0;
}
