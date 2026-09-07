using Vector2 = System.Numerics.Vector2;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;

namespace POE2Radar.Overlay.Input;

/// <summary>Ranged unique-enemy policy. Uses observed damage and geometry, never a periodic attack predictor.
/// No native input or memory offsets here; unknown targetability is not inferred from a missing entity.</summary>
public sealed class BossCombat
{
    public enum Intent { None, Attack, Reposition, Dodge, Wait }
    public readonly record struct Decision(Intent Intent, uint TargetId = 0, Vector2 Destination = default,
        string Note = "", bool Interrupt = false, bool LongWindow = false)
    {
        public bool Active => Intent != Intent.None;
    }
    public readonly record struct Options(float Range = 35, float KeepDistance = 12,
        float DamagePct = 6, int DodgeGapMs = 1800, int SafeOpeningMs = 1200, int LostGraceMs = 8000, float HazardRadius = 6, float FleeBelow = 35, float FleeRecover = 50, float DodgeDistance = 40);
    public readonly record struct Snapshot(uint Area, DateTime Now, bool Enabled, bool Fresh,
        Vector2 Player, IReadOnlyList<Poe2Live.EntityDot> Entities, Poe2Live.Vitals? Vitals,
        NavGrid? Nav, Options Options, IReadOnlyCollection<uint>? Excluded = null);

    private uint _area, _id;
    private DateTime _lastSeen, _lastProgress, _safeSince, _dangerUntil, _lastDodge, _lastProbe;
    private Vector2 _lastBoss, _lastPlayer;
    private int _lastHp;
    private bool _spacing, _attacked, _inHazard, _lowLife, _available;
    private int _lifeCapacity, _esCapacity;
    private Vector2 _steer;
    private DateTime _steerUntil;
    private readonly Queue<(DateTime At, float Life, float Es)> _resources = new();
    public uint TargetId => _id;

    public void Reset()
    {
        _id = 0; _resources.Clear(); _spacing = _attacked = _inHazard = _lowLife = _available = false;
        _steerUntil = DateTime.MinValue;
        _lastSeen = _lastProgress = _safeSince = _dangerUntil = _lastDodge = _lastProbe = DateTime.MinValue;
    }
    public void DidDodge(DateTime now) { _lastDodge = now; _resources.Clear(); }
    public void DidAttack(DateTime now) { _attacked = true; _lastProbe = now; }

    public Decision Update(in Snapshot s)
    {
        if (_area != s.Area) { Reset(); _area = s.Area; }
        if (!s.Enabled || s.Options.KeepDistance <= 0 || s.Vitals is { HpCur: <= 0 }) { Reset(); return default; }
        if (!s.Fresh) { _resources.Clear(); _safeSince = s.Now; return new(Intent.Wait, _id, Note: "waiting: fresh world observations", Interrupt: true); }
        var boss = default(Poe2Live.EntityDot);
        foreach (var e in s.Entities)
            if (e.Id == _id && _id != 0) { boss = e; break; }
        // Only an observed zero-life sample is a death. An absent Life component is unknown.
        if (_id != 0 && boss.Id == _id && boss.HasLife && boss.HpCur <= 0) { Reset(); return default; }
        if (_id == 0)
        {
            var best = float.MaxValue;
            foreach (var e in s.Entities)
            {
                if (e.Rarity != Poe2Live.Rarity.Unique || !CombatAssist.IsHostile(e) || !e.HasLife
                    || s.Excluded?.Contains(e.Id) == true) continue;
                var d = Vector2.Distance(s.Player, e.Grid);
                if (d > s.Options.Range || d >= best) continue;
                best = d; boss = e;
            }
            if (boss.Id == 0) return default;
            _id = boss.Id; _lastHp = boss.HpCur; _lastBoss = boss.Grid; _lastPlayer = s.Player;
            _lastProgress = _safeSince = _lastSeen = s.Now;
            _available = true;
        }
        if (boss.Id != _id || !boss.HasLife || boss.IsFriendly
            || Vector2.Distance(s.Player, boss.Grid) > s.Options.Range * 2)
        {
            _resources.Clear(); _safeSince = s.Now;
            var firstLoss = _available; _available = false;
            if (s.Now - _lastSeen > TimeSpan.FromMilliseconds(s.Options.LostGraceMs)) { Reset(); return default; }
            return new(Intent.Wait, _id, Note: "waiting: boss unavailable (phase or target loss)", Interrupt: firstLoss);
        }
        _lastSeen = s.Now; _available = true;
        if (boss.HpCur < _lastHp) { _lastProgress = s.Now; _attacked = false; }
        _lastHp = boss.HpCur;
        var bossMoved = Vector2.DistanceSquared(boss.Grid, _lastBoss) > .25f;
        var playerMoved = Vector2.DistanceSquared(s.Player, _lastPlayer) > .25f;
        if (playerMoved) _lastPlayer = s.Player;
        if (bossMoved) _lastBoss = boss.Grid; // accumulate small movements across world samples
        if (s.Vitals is not { } v || !float.IsFinite(v.HpPct))
        { _resources.Clear(); _safeSince = s.Now; return new(Intent.Wait, _id, Note: "waiting: player resources unreadable", Interrupt: true); }

        if (_lifeCapacity != v.HpUnreserved || _esCapacity != v.EsUnreserved)
        { _resources.Clear(); _lifeCapacity = v.HpUnreserved; _esCapacity = v.EsUnreserved; }
        // A 13-point ES pool on a 487-life Ranger must not look like a 100% life hit.
        var es = v.HasEs && v.EsUnreserved >= v.HpUnreserved * .25f ? 100f * v.EsCur / Math.Max(1, v.HpUnreserved) : 0;
        while (_resources.TryPeek(out var old) && s.Now - old.At > TimeSpan.FromMilliseconds(500)) _resources.Dequeue();
        var nowTime = s.Now;
        var drop = _resources.Count == 0 ? 0 : _resources.Max(p => Math.Max(0, p.Life - v.HpPct) + Math.Max(0, p.Es - es));
        _resources.Enqueue((nowTime, v.HpPct, es));
        var hit = s.Options.DamagePct > 0 && drop >= s.Options.DamagePct;
        if (hit) { _dangerUntil = s.Now.AddMilliseconds(900); _resources.Clear(); }
        var distance = Vector2.Distance(s.Player, boss.Grid);
        var min = Math.Min(s.Options.KeepDistance, s.Options.Range * .5f);
        var max = s.Options.Range * .78f;
        var releaseDistance = Math.Min(min + 4, max - 1);
        var tooClose = distance < min || (_spacing && distance < releaseDistance);
        _spacing = tooClose;
        var line = s.Nav is not null && ClearLine(s.Nav, s.Player, boss.Grid, 3);
        var inHazard = HazardRisk(s.Player, s.Entities, s.Options.HazardRadius) > 0;
        var enteredHazard = inHazard && !_inHazard;
        _inHazard = inHazard;
        var wasLowLife = _lowLife;
        _lowLife = s.Options.FleeBelow > 0 && (v.HpPct < s.Options.FleeBelow
            || (_lowLife && v.HpPct < Math.Max(s.Options.FleeBelow, s.Options.FleeRecover)));
        var lowLifeEntered = _lowLife && !wasLowLife;
        var threatened = _lowLife || inHazard || hit || s.Now < _dangerUntil || tooClose || distance > max || bossMoved || playerMoved;
        if (threatened || !line) _safeSince = s.Now;
        var reposition = _lowLife || inHazard || tooClose || !line || distance > max || s.Now < _dangerUntil;
        if (reposition)
        {
            var reason = _lowLife ? "low life" : inHazard ? "observed Rudja oil ground" : tooClose ? "too close" : !line ? "terrain line blocked or unknown" : distance > max ? "outside bow range" : "recent damage";
            if (TryPosition(s.Nav, s.Player, boss.Grid, releaseDistance, max, s.Entities, out var point, s.Options.HazardRadius))
            {
                if (!hit && s.Now < _steerUntil && Vector2.Distance(s.Player, _steer) > 2
                    && s.Nav is not null && ClearLine(s.Nav, s.Player, _steer)
                    && SafeHazardPath(s.Player, _steer, s.Entities, s.Options.HazardRadius)) point = _steer;
                else { _steer = point; _steerUntil = s.Now.AddMilliseconds(350); }
                var dodgePoint = default(Vector2);
                var dodge = hit && s.Now - _lastDodge >= TimeSpan.FromMilliseconds(s.Options.DodgeGapMs)
                    && TryPosition(s.Nav, s.Player, boss.Grid, releaseDistance, max, s.Entities,
                        out dodgePoint, s.Options.HazardRadius, s.Options.DodgeDistance);
                if (dodge) point = dodgePoint;
                return new(dodge ? Intent.Dodge : Intent.Reposition, _id, point,
                    dodge ? $"dodging: observed resource loss {drop:0}%" : "repositioning: " + reason, Interrupt: hit || enteredHazard || lowLifeEntered);
            }
            // Don't send blind retreat commands through walls when all candidates are blocked.
            return new(Intent.Wait, _id, Note: "waiting: no clear escape path", Interrupt: hit || enteredHazard || lowLifeEntered);
        }
        var uncertain = _attacked && s.Now - _lastProgress > TimeSpan.FromSeconds(4);
        if (uncertain && s.Now - _lastProbe < TimeSpan.FromMilliseconds(1500))
            return new(Intent.Wait, _id, Note: "waiting: boss taking no damage (targetability unknown)");
        return new(Intent.Attack, _id, Note: uncertain ? "attacking: short probe (targetability unknown)" : "attacking: bow range clear",
            LongWindow: !uncertain && !threatened && s.Now - _safeSince >= TimeSpan.FromMilliseconds(s.Options.SafeOpeningMs));
    }

    // Validated presence in a live Rudja encounter on 2026-09-07. Radius is a user-tuned exclusion
    // margin, not a decoded hitbox. Generic ServerEffect/GroundEffect entities are NOT classified.
    public static bool IsKnownHazard(in Poe2Live.EntityDot e)
        => e.Metadata == "Metadata/Effects/Spells/crossbow_oilgrenade/RudjaOilGround";

    public static float HazardRisk(Vector2 point, IReadOnlyList<Poe2Live.EntityDot> entities, float radius)
    {
        var risk = 0f;
        foreach (var e in entities)
            if (IsKnownHazard(e)) risk += Math.Max(0, radius - Vector2.Distance(point, e.Grid));
        return risk;
    }
    public static bool SafeHazardPath(Vector2 from, Vector2 to, IReadOnlyList<Poe2Live.EntityDot> entities, float radius)
    {
        if (HazardRisk(to, entities, radius) > 0) return false;
        var risk = HazardRisk(from, entities, radius);
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to)));
        for (var i = 1; i <= steps; i++)
        {
            var next = HazardRisk(Vector2.Lerp(from, to, i / (float)steps), entities, radius);
            if (next > risk + .01f) return false; // allow exiting a patch, never walking deeper into it
            risk = next;
        }
        return true;
    }

    internal static bool SafeEnemyPath(Vector2 from, Vector2 to, IReadOnlyList<Poe2Live.EntityDot> entities)
    {
        var delta = to - from;
        if (delta.LengthSquared() < .01f) return false;
        foreach (var e in entities)
        {
            if (!CombatAssist.IsHostile(e)) continue;
            var t = Math.Clamp(Vector2.Dot(e.Grid - from, delta) / delta.LengthSquared(), 0, 1);
            var clearance = Math.Min(8, Vector2.Distance(from, e.Grid));
            if (Vector2.Distance(from + delta * t, e.Grid) < clearance - .1f) return false;
        }
        return true;
    }

    // Terrain line is a conservative walkable proxy, NOT a validated projectile collision/height test.
    public static bool ClearLine(NavGrid nav, Vector2 a, Vector2 b, int clearance = 6)
        => nav.HasClearLine((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X), (int)MathF.Round(b.Y), clearance, 3);

    public static bool TryPosition(NavGrid? nav, Vector2 player, Vector2 boss, float min, float max,
        IReadOnlyList<Poe2Live.EntityDot> entities, out Vector2 best, float hazardRadius = 6, float? travelDistance = null)
    {
        best = default;
        if (nav is null) return false;
        var score = float.NegativeInfinity;
        var currentDistance = Vector2.Distance(player, boss);
        // Search lateral routes too: blindly walking opposite the boss pins the Ranger against walls.
        foreach (var length in travelDistance is { } travel ? new[] { travel } : new[] { 6f, 10f })
        for (var i = 0; i < 16; i++)
        {
            var angle = i * MathF.Tau / 16;
            var point = player + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * length;
            if (!ClearLine(nav, player, point) || !SafeHazardPath(player, point, entities, hazardRadius)) continue;
            // A long roll must not choose the opposite side of the boss through its collision body.
            if (travelDistance.HasValue && !SafeEnemyPath(player, point, entities)) continue;
            var distance = Vector2.Distance(point, boss);
            if (distance < Math.Min(min, currentDistance + 2)) continue;
            var clearance = nav.ClearanceCells((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
            if (clearance < 2) continue;
            var desired = (min + max) / 2;
            var value = -MathF.Abs(distance - desired) + Math.Min(5, clearance) * 2;
            if (!ClearLine(nav, point, boss, 3)) value -= 15;
            foreach (var e in entities)
                if (CombatAssist.IsHostile(e) && e.Id != 0 && Vector2.Distance(point, e.Grid) < 8) value -= 20;
            if (value <= score) continue;
            best = point; score = value;
        }
        return float.IsFinite(score);
    }
}
