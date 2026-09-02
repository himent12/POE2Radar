using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Opt-in path movement: tap one WASD key toward the next waypoint of the first selected path,
/// or tap a click/move key (click-to-move). The decision is a pure function of the snapshot so
/// tests never need a live client. I/O (hotkey, TapKey, cursor, settings) stays in <c>RadarApp</c>.
/// </summary>
public static class PathMove
{
    public const ushort VkW = 0x57;
    public const ushort VkA = 0x41;
    public const ushort VkS = 0x53;
    public const ushort VkD = 0x44;
    public const ushort VkClick = 0x01; // VK_LBUTTON

    public readonly record struct Snapshot(
        bool Armed,
        bool Focused,
        bool InGame,
        NumVec2 PlayerGrid,
        IReadOnlyList<(int x, int y)> Waypoints,
        float ArriveRadius,
        DateTime NowUtc,
        DateTime LastFireUtc,
        int CooldownMs,
        string Method,
        int KeyW,
        int KeyA,
        int KeyS,
        int KeyD,
        int ClickKey);

    public readonly record struct Decision(bool ShouldTap, ushort Vk, string Note, int TargetX = 0, int TargetY = 0);

    /// <summary>
    /// Grid-cardinal WASD: +Y → W, −Y → S, +X → D, −X → A. Dominant axis wins; Y on a tie.
    /// Click method taps <see cref="Snapshot.ClickKey"/> toward the same next waypoint.
    /// </summary>
    public static Decision Decide(in Snapshot s)
    {
        if (!s.Armed) return new(false, 0, "OFF (F5)");
        if (!s.InGame) return new(false, 0, "paused (not in game)");
        if (!s.Focused) return new(false, 0, "paused (PoE2 not focused)");

        var pts = s.Waypoints;
        if (pts is not { Count: > 0 }) return new(false, 0, "no path");

        var radius = Math.Max(0f, s.ArriveRadius);
        var radiusSq = radius * radius;
        var player = s.PlayerGrid;
        (int x, int y)? next = null;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var dx = p.x - player.X;
            var dy = p.y - player.Y;
            if (dx * dx + dy * dy > radiusSq) { next = p; break; }
        }
        if (next is not { } wp) return new(false, 0, "arrived");

        var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, s.CooldownMs));
        if (s.NowUtc - s.LastFireUtc < cooldown) return new(false, 0, "armed");

        if (IsClick(s.Method))
            return Tap(s.ClickKey, "click", wp.x, wp.y);

        if (!IsWasd(s.Method)) return new(false, 0, "armed");

        var mx = wp.x - player.X;
        var my = wp.y - player.Y;
        if (Math.Abs(mx) > Math.Abs(my))
            return mx > 0 ? Tap(s.KeyD, "D", wp.x, wp.y) : Tap(s.KeyA, "A", wp.x, wp.y);
        return my > 0 ? Tap(s.KeyW, "W", wp.x, wp.y) : Tap(s.KeyS, "S", wp.x, wp.y);
    }

    public static bool IsClick(string? method)
        => method is not null
           && (method.Equals("Click", StringComparison.OrdinalIgnoreCase)
               || method.Equals("ClickToMove", StringComparison.OrdinalIgnoreCase));

    private static Decision Tap(int key, string note, int x, int y)
        => new(true, (ushort)Math.Clamp(key, 1, 255), note, x, y);

    private static bool IsWasd(string method)
        => string.IsNullOrEmpty(method)
           || method.Equals("WASD", StringComparison.OrdinalIgnoreCase);
}
