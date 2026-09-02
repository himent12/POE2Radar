using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Opt-in combat assist: tap one attack key while a hostile monster is in grid range.
/// The decision is a pure function of the snapshot so tests never need a live client.
/// I/O (hotkey, TapKey, settings) stays in <c>RadarApp</c>.
/// </summary>
public static class CombatAssist
{
    public readonly record struct Snapshot(
        bool Armed,
        bool Focused,
        bool InGame,
        System.Numerics.Vector2 PlayerGrid,
        IReadOnlyList<Poe2Live.EntityDot> Entities,
        float Range,
        DateTime NowUtc,
        DateTime LastFireUtc,
        int CooldownMs);

    public readonly record struct Decision(bool ShouldTap, string Note);

    /// <summary>
    /// Hostile = not friendly: <c>(Reaction &amp; 0x7F) != 1</c>. Only alive monsters count.
    /// </summary>
    public static Decision Decide(in Snapshot s)
    {
        if (!s.Armed) return new(false, "OFF (F4)");
        if (!s.InGame) return new(false, "paused (not in game)");
        if (!s.Focused) return new(false, "paused (PoE2 not focused)");

        var range = Math.Max(0f, s.Range);
        var rangeSq = range * range;
        var hostileInRange = false;
        foreach (var e in s.Entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster) continue;
            if (!e.IsAlive) continue;
            if ((e.Reaction & 0x7F) == 1) continue;
            var dx = e.Grid.X - s.PlayerGrid.X;
            var dy = e.Grid.Y - s.PlayerGrid.Y;
            if (dx * dx + dy * dy <= rangeSq) { hostileInRange = true; break; }
        }

        if (!hostileInRange) return new(false, "armed");

        var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, s.CooldownMs));
        if (s.NowUtc - s.LastFireUtc < cooldown) return new(false, "armed");

        return new(true, "fired");
    }
}
