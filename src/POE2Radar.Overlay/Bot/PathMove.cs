using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Opt-in path movement. WASD method: HOLD up to two direction keys (8-way) toward a look-ahead point
/// on the route (string-pulled over walkable terrain so corners are cut instead of stair-stepped),
/// plus an optional run key held while moving. Click method: tap a click/move key at the same
/// look-ahead point. The decision is a pure function of the snapshot; I/O stays in <c>RadarApp</c>.
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
        int ClickKey,
        bool PauseForCombat = false,
        // ── Movement quality ──
        float LookAhead = 12f,           // cells: steer toward the farthest visible waypoint within this
        byte[]? Walkable = null,         // terrain for line-of-sight string pulling (null = no LOS check)
        int Width = 0,
        int Height = 0,
        bool Diagonals = true,           // allow two keys held at once (8-way)
        float AxisRotationDeg = 0f,      // rotate grid→key mapping (calibration for the isometric camera)
        int RunKey = 0,                  // held while moving (0 = none)
        bool RunEnabled = false,
        IReadOnlyList<ushort>? PrevHoldKeys = null); // last tick's direction keys → sector hysteresis

    /// <summary>
    /// WASD: <see cref="HoldKeys"/> is the exact set to keep pressed this tick (empty = release
    /// everything); <see cref="ShouldTap"/> stays false. Click: <see cref="ShouldTap"/>/<see cref="Vk"/> as
    /// before. <see cref="Moving"/> = the bot wants to be travelling (run key held).
    /// </summary>
    public readonly record struct Decision(
        bool ShouldTap,
        ushort Vk,
        string Note,
        int TargetX = 0,
        int TargetY = 0,
        IReadOnlyList<ushort>? HoldKeys = null,
        bool Moving = false);

    private static readonly ushort[] NoKeys = Array.Empty<ushort>();

    /// <summary>
    /// Grid-cardinal mapping (before rotation): +Y → W, −Y → S, +X → D, −X → A. With diagonals, both
    /// keys are held when the direction is within ±67.5° of both axes (8 sectors of 45°).
    /// </summary>
    public static Decision Decide(in Snapshot s)
    {
        if (!s.Armed) return new(false, 0, "OFF (F5)", HoldKeys: NoKeys);
        if (!s.InGame) return new(false, 0, "paused (not in game)", HoldKeys: NoKeys);
        if (!s.Focused) return new(false, 0, "paused (PoE2 not focused)", HoldKeys: NoKeys);
        if (s.PauseForCombat) return new(false, 0, "combat", HoldKeys: NoKeys);

        var pts = s.Waypoints;
        if (pts is not { Count: > 0 }) return new(false, 0, "no path", HoldKeys: NoKeys);

        var radius = Math.Max(0f, s.ArriveRadius);
        var radiusSq = radius * radius;
        var player = s.PlayerGrid;

        // Goal reached?
        {
            var g = pts[pts.Count - 1];
            var gx = g.x - player.X;
            var gy = g.y - player.Y;
            if (gx * gx + gy * gy <= radiusSq) return new(false, 0, "arrived", HoldKeys: NoKeys);
        }

        // "Next" = the waypoint AHEAD of the player's projection onto the route, never one already passed
        // (the route snapshot lags the live position by a world tick, and with run on the character overshoots
        // nodes — aiming at a node behind us is what caused the forward/back/forward stutter).
        var nextIdx = NextWaypointIndex(pts, player, radiusSq);
        if (nextIdx < 0) return new(false, 0, "arrived", HoldKeys: NoKeys);

        var wp = LookAheadPoint(s, pts, nextIdx);

        if (IsClick(s.Method))
        {
            var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, s.CooldownMs));
            if (s.NowUtc - s.LastFireUtc < cooldown) return new(false, 0, "armed", wp.x, wp.y, NoKeys, Moving: true);
            return new(true, (ushort)Math.Clamp(s.ClickKey, 1, 255), "click", wp.x, wp.y, NoKeys, Moving: true);
        }

        if (!IsWasd(s.Method)) return new(false, 0, "armed", HoldKeys: NoKeys);

        var mx = wp.x - player.X;
        var my = wp.y - player.Y;
        if (s.AxisRotationDeg != 0f)
        {
            var a = s.AxisRotationDeg * MathF.PI / 180f;
            var c = MathF.Cos(a);
            var sn = MathF.Sin(a);
            (mx, my) = (mx * c - my * sn, mx * sn + my * c);
        }
        var len = MathF.Sqrt(mx * mx + my * my);
        if (len < 1e-3f) return new(false, 0, "arrived", HoldKeys: NoKeys);

        // 8 sectors of 45° (4 of 90° without diagonals), centred on the key directions. Hysteresis: keep
        // last tick's sector while the heading is within HysteresisDeg past its edge, so a heading that sits
        // on a boundary does not flap W ↔ WD every tick (each flap is a KeyUp/KeyDown the game sees as a jerk).
        var angle = MathF.Atan2(my, mx);
        var step = s.Diagonals ? MathF.PI / 4f : MathF.PI / 2f;
        var sectorIdx = (int)MathF.Round(angle / step);
        var sectors = s.Diagonals ? 8 : 4;
        sectorIdx = ((sectorIdx % sectors) + sectors) % sectors;
        var err = WrapAngle(angle - sectorIdx * step);
        if (MathF.Abs(err) > DitherRad)
        {
            // The wanted heading sits between two key directions (e.g. 30° with only 0°/45° available). In a
            // corridor that error walks you into the wall before pure pursuit can correct it, so time-slice the
            // two neighbouring sectors: duty = how far toward the neighbour the heading is. 120 ms slots.
            var neighbour = ((sectorIdx + (err > 0 ? 1 : -1)) % sectors + sectors) % sectors;
            var duty = MathF.Abs(err) / step;                       // 0..0.5
            var slot = (s.NowUtc.Ticks / (TimeSpan.TicksPerMillisecond * 120)) % 4;
            if (slot < (int)MathF.Round(duty * 4f)) sectorIdx = neighbour;
        }
        else if (s.PrevHoldKeys is { Count: > 0 } prev && TrySectorOf(s, prev, out var prevSector, out var prevDiag)
            && prevDiag == s.Diagonals && prevSector != sectorIdx)
        {
            var center = prevSector * step;
            var diff = MathF.Abs(WrapAngle(angle - center));
            if (diff < step * 0.5f + HysteresisRad) sectorIdx = prevSector;
        }

        var keys = new List<ushort>(2);
        var note = "";
        var (dirX, dirY) = SectorDirection(sectorIdx, s.Diagonals);
        if (dirY != 0) { keys.Add(Vk(dirY > 0 ? s.KeyW : s.KeyS)); note += dirY > 0 ? "W" : "S"; }
        if (dirX != 0) { keys.Add(Vk(dirX > 0 ? s.KeyD : s.KeyA)); note += dirX > 0 ? "D" : "A"; }
        return new(false, keys[0], note, wp.x, wp.y, keys, Moving: true);
    }

    private const float HysteresisRad = 8f * MathF.PI / 180f;
    private const float DitherRad = 12f * MathF.PI / 180f;

    /// <summary>Direction (x,y) sign pair for a set of held keys (settings-mapped), (0,0) when none.</summary>
    public static (int x, int y) DirectionOf(IReadOnlyList<ushort> keys, int keyW, int keyA, int keyS, int keyD)
    {
        int x = 0, y = 0;
        foreach (var k in keys)
        {
            if (k == Vk(keyW)) y = 1; else if (k == Vk(keyS)) y = -1;
            else if (k == Vk(keyD)) x = 1; else if (k == Vk(keyA)) x = -1;
        }
        return (x, y);
    }

    /// <summary>Keys for a direction sign pair.</summary>
    public static List<ushort> KeysFor((int x, int y) dir, int keyW, int keyA, int keyS, int keyD)
    {
        var keys = new List<ushort>(2);
        if (dir.y > 0) keys.Add(Vk(keyW)); else if (dir.y < 0) keys.Add(Vk(keyS));
        if (dir.x > 0) keys.Add(Vk(keyD)); else if (dir.x < 0) keys.Add(Vk(keyA));
        return keys;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    /// <summary>Sector index → (x, y) sign pair. 8-way: 0=D 1=WD 2=W 3=WA 4=A 5=SA 6=S 7=SD. 4-way: 0=D 1=W 2=A 3=S.</summary>
    private static (int x, int y) SectorDirection(int sector, bool diagonals)
    {
        if (!diagonals) sector *= 2;
        return sector switch
        {
            0 => (1, 0), 1 => (1, 1), 2 => (0, 1), 3 => (-1, 1),
            4 => (-1, 0), 5 => (-1, -1), 6 => (0, -1), _ => (1, -1),
        };
    }

    private static bool TrySectorOf(in Snapshot s, IReadOnlyList<ushort> keys, out int sector, out bool diagonal)
    {
        int x = 0, y = 0;
        foreach (var k in keys)
        {
            if (k == Vk(s.KeyW)) y = 1;
            else if (k == Vk(s.KeyS)) y = -1;
            else if (k == Vk(s.KeyD)) x = 1;
            else if (k == Vk(s.KeyA)) x = -1;
        }
        diagonal = s.Diagonals;
        sector = -1;
        if (x == 0 && y == 0) return false;
        var sectors = diagonal ? 8 : 4;
        for (var i = 0; i < sectors; i++)
            if (SectorDirection(i, diagonal) == (x, y)) { sector = i; return true; }
        return false;
    }

    /// <summary>
    /// Project the player onto the route polyline (first <c>ProjectWindow</c> segments) and return the index of
    /// the endpoint AHEAD of that projection — then skip forward over any node already inside the arrive radius.
    /// -1 when every remaining node is reached.
    /// </summary>
    private const int ProjectWindow = 24;

    public static int NextWaypointIndex(IReadOnlyList<(int x, int y)> pts, NumVec2 player, float radiusSq)
    {
        var n = pts.Count;
        if (n == 0) return -1;
        var next = 0;
        if (n > 1)
        {
            var bestD = float.MaxValue;
            var bestSeg = 0;
            var bestT = 0f;
            var last = Math.Min(n - 1, ProjectWindow);
            for (var i = 0; i < last; i++)
            {
                var a = new NumVec2(pts[i].x, pts[i].y);
                var b = new NumVec2(pts[i + 1].x, pts[i + 1].y);
                var ab = b - a;
                var lenSq = ab.LengthSquared();
                var t = lenSq < 1e-6f ? 0f : Math.Clamp(NumVec2.Dot(player - a, ab) / lenSq, 0f, 1f);
                var d = NumVec2.DistanceSquared(player, a + ab * t);
                // Strictly-better only: on ties (route doubling back near us) keep the EARLIER segment so the
                // cursor cannot leap across a switchback.
                if (d < bestD - 1e-3f) { bestD = d; bestSeg = i; bestT = t; }
            }
            // Before the route's first node (projection clamps to t=0 on segment 0) → head to node 0 itself.
            next = bestSeg == 0 && bestT <= 0.001f ? 0
                 : bestT >= 0.999f ? Math.Min(bestSeg + 2, n - 1)
                 : bestSeg + 1;
        }
        while (next < n)
        {
            var dx = pts[next].x - player.X;
            var dy = pts[next].y - player.Y;
            if (dx * dx + dy * dy > radiusSq) return next;
            next++;
        }
        return -1;
    }

    /// <summary>
    /// String pulling: from the next waypoint, advance along the route while the segment player→waypoint
    /// stays inside <see cref="Snapshot.LookAhead"/> and (when terrain is known) has walkable line of sight.
    /// Returns the farthest such waypoint — the bot cuts corners instead of stair-stepping every node.
    /// </summary>
    private static (int x, int y) LookAheadPoint(in Snapshot s, IReadOnlyList<(int x, int y)> pts, int nextIdx)
    {
        var hasTerrain = s.Walkable is not null && s.Width > 0 && s.Height > 0;
        var look = Math.Max(1f, s.LookAhead);
        // Pass 1 — full look-ahead with clearance 1: the character is wider than a cell, so a line that grazes a
        // corner is a wall hit. Pass 2 (only if pass 1 could not advance past the next node, i.e. we are hugging
        // a wall / in a corridor) — thin line at half the look-ahead: still smooths along the wall, never cuts it.
        var best = Advance(pts, nextIdx, s.PlayerGrid, look, hasTerrain ? 1 : 0, hasTerrain ? s.Walkable : null, s.Width, s.Height);
        if (best == pts[nextIdx] && hasTerrain)
            best = Advance(pts, nextIdx, s.PlayerGrid, look * 0.5f, 0, s.Walkable, s.Width, s.Height);
        // Smoothed routes can have their FIRST next waypoint hundreds of cells away. Advance only
        // bounded later nodes, so that first node bypassed LookAhead and could project off-screen.
        var delta = new NumVec2(best.x, best.y) - s.PlayerGrid;
        if (IsClick(s.Method) && delta.LengthSquared() > look * look)
        {
            var near = s.PlayerGrid + NumVec2.Normalize(delta) * look;
            var bounded = ((int)MathF.Round(near.X), (int)MathF.Round(near.Y));
            if (!hasTerrain || HasLineOfSight(s.Walkable!, s.Width, s.Height, s.PlayerGrid, bounded)) return bounded;
        }
        return best;
    }

    private static (int x, int y) Advance(IReadOnlyList<(int x, int y)> route, int from, NumVec2 player, float range,
        int clearance, byte[]? walkable, int width, int height)
    {
        var b = route[from];
        var rangeSq = range * range;
        for (var i = from + 1; i < route.Count; i++)
        {
            var p = route[i];
            var dx = p.x - player.X;
            var dy = p.y - player.Y;
            if (dx * dx + dy * dy > rangeSq) break;
            if (walkable is not null && !HasLineOfSight(walkable, width, height, player, p, clearance)) break;
            b = p;
        }
        return b;
    }

    /// <summary>Bresenham walk over the walkable bitmap; false at the first blocked cell. With
    /// <paramref name="clearance"/> &gt; 0 the 4-neighbours within that radius must be walkable too (thick line).</summary>
    public static bool HasLineOfSight(byte[] walkable, int width, int height, NumVec2 from, (int x, int y) to, int clearance = 0)
    {
        bool Clear(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
            if (walkable[y * width + x] == 0) return false;
            for (var c = 1; c <= clearance; c++)
            {
                if (!Ok(x + c, y) || !Ok(x - c, y) || !Ok(x, y + c) || !Ok(x, y - c)) return false;
            }
            return true;
        }
        // Off-map neighbours don't count as walls (map edges are already bounded by unwalkable cells).
        bool Ok(int x, int y) => (uint)x >= (uint)width || (uint)y >= (uint)height || walkable[y * width + x] != 0;

        var x0 = (int)MathF.Round(from.X);
        var y0 = (int)MathF.Round(from.Y);
        var x1 = to.x;
        var y1 = to.y;
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        // The origin is where the character STANDS. When the grid calls that cell blocked (door threshold,
        // ledge lip — collision is looser than the nav grid) he can still walk off it, so the origin is not a
        // wall hit; every cell after it is checked as usual.
        var first = true;
        while (true)
        {
            var originOffGrid = first && (uint)x0 < (uint)width && (uint)y0 < (uint)height && walkable[y0 * width + x0] == 0;
            first = false;
            if (!originOffGrid && !Clear(x0, y0)) return false;
            if (x0 == x1 && y0 == y1) return true;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    public static bool IsClick(string? method)
        => method is not null
           && (method.Equals("Click", StringComparison.OrdinalIgnoreCase)
               || method.Equals("ClickToMove", StringComparison.OrdinalIgnoreCase));

    private static ushort Vk(int key) => (ushort)Math.Clamp(key, 1, 255);

    private static bool IsWasd(string method)
        => string.IsNullOrEmpty(method)
           || method.Equals("WASD", StringComparison.OrdinalIgnoreCase);
}
