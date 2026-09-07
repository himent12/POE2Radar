using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Fight watchdog that decides when the bot should STOP walking to fight, and when a fight has gone
/// nowhere for long enough that the bot should give up on those monsters and move on.
///
/// <para>Symptom this fixes: with a hostile inside the combat range the mover was paused forever, even
/// when nothing was actually being hit (mob behind a wall / on another level / unreachable / cursor aimed
/// elsewhere). The bot then stood still with 1–2 live mobs nearby. Now the pause only holds while a
/// hostile is inside the tighter <c>engageRange</c> AND some hostile in range has taken damage (or died)
/// within <see cref="StallAfter"/>. When that stops being true the engaged monsters are ignored for
/// <see cref="IgnoreFor"/> — movement resumes, map-clear routes elsewhere — and re-considered after.</para>
///
/// <para>Thread-safe: <see cref="Update"/> runs on the render/tick thread, <see cref="IgnoredIds"/> /
/// <see cref="IsIgnored"/> are read from the world thread by map-clear.</para>
/// </summary>
public sealed class CombatWatch
{
    /// <summary>
    /// <see cref="Flee"/>: player HP dropped under <see cref="FleeBelowPct"/> with hostiles engaged → stop
    /// attacking and run away (the caller feeds <see cref="TryFleePoint"/> to the mover) until HP is back
    /// above <see cref="FleeRecoverPct"/> or nothing is in range any more.
    /// </summary>
    /// <summary><see cref="Kite"/>: a hostile is closer than <see cref="KeepDistance"/> — back off while still
    /// attacking (ranged builds). Movement is not paused; the caller feeds it the flee point.</summary>
    public readonly record struct Result(bool PauseMove, bool Fighting, bool Stalled, int InRange, string Note, bool Flee = false, bool Kite = false);

    private readonly object _lock = new();
    private readonly Dictionary<uint, int> _hp = new();
    private readonly Dictionary<uint, DateTime> _ignoreUntil = new();
    private readonly HashSet<uint> _seen = new();
    private HashSet<uint> _ignoredSnapshot = new();
    private bool _fighting;
    private bool _fleeing;
    private DateTime _lastProgressUtc;

    /// <summary><see cref="TimeSpan.Zero"/> = never give up on a fight: stand and attack until the mobs are dead.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.Zero;
    public TimeSpan IgnoreFor { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Run away when player HP% is below this (0 = never flee).</summary>
    public float FleeBelowPct { get; set; } = 35f;
    /// <summary>Stop running once HP% is back at/above this.</summary>
    public float FleeRecoverPct { get; set; } = 60f;

    /// <summary>Back off when any hostile is closer than this many cells (0 = melee, never kite).</summary>
    public float KeepDistance { get; set; } = 0f;

    /// <summary>
    /// Retarget grace: after the last engaged hostile dies, keep the mover paused this long while OTHER
    /// hostiles are still inside <c>attackRange</c> (the wider combat range), so the rotation re-aims at the
    /// next mob in the pack instead of the mover kicking in for a moment — a run/roll press between two
    /// targets is what drops a built-up combo.
    /// </summary>
    public TimeSpan RetargetGrace { get; set; } = TimeSpan.FromMilliseconds(900);
    private DateTime _lastEngagedUtc = DateTime.MinValue;

    public bool IsFleeing { get { lock (_lock) return _fleeing; } }

    /// <summary>Hostile ids the watchdog has given up on (snapshot; safe to read from any thread).</summary>
    public IReadOnlyCollection<uint> IgnoredIds
    {
        get { lock (_lock) return _ignoredSnapshot; }
    }

    public bool IsIgnored(uint id)
    {
        lock (_lock) return _ignoredSnapshot.Contains(id);
    }

    /// <summary>Forget everything (zone change).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _hp.Clear();
            _ignoreUntil.Clear();
            _seen.Clear();
            _ignoredSnapshot = new HashSet<uint>();
            _fighting = false;
            _fleeing = false;
        }
    }

    /// <summary>
    /// One tick. <paramref name="engageRange"/> is the grid radius inside which a live hostile pauses movement.
    /// Progress = any tracked hostile lost HP or died/vanished since the previous tick.
    /// </summary>
    public Result Update(IReadOnlyList<Poe2Live.EntityDot>? entities, NumVec2 player, float engageRange, DateTime nowUtc, float playerHpPct = 100f,
        float attackRange = 0f)
    {
        lock (_lock)
        {
            // Expire ignores.
            if (_ignoreUntil.Count > 0)
            {
                List<uint>? expired = null;
                foreach (var kv in _ignoreUntil)
                    if (kv.Value <= nowUtc) (expired ??= new()).Add(kv.Key);
                if (expired is not null)
                {
                    foreach (var id in expired) _ignoreUntil.Remove(id);
                    _ignoredSnapshot = new HashSet<uint>(_ignoreUntil.Keys);
                }
            }

            var r = Math.Max(0f, engageRange);
            var rSq = r * r;
            var keepSq = KeepDistance > 0f ? KeepDistance * KeepDistance : -1f;
            var inRange = 0;
            var tooClose = false;
            var progress = false;
            _seen.Clear();

            if (entities is not null)
            {
                foreach (var e in entities)
                {
                    if (e.Category != Poe2Live.EntityCategory.Monster || !e.HasLife) continue;
                    if ((e.Reaction & 0x7F) == 1) continue;
                    if (_ignoreUntil.ContainsKey(e.Id)) continue;

                    // A tracked monster that died counts as progress (kill), then drops out of tracking.
                    if (!e.IsAlive)
                    {
                        if (_hp.Remove(e.Id)) progress = true;
                        continue;
                    }

                    var dx = e.Grid.X - player.X;
                    var dy = e.Grid.Y - player.Y;
                    var dSq = dx * dx + dy * dy;
                    if (dSq > rSq) continue;
                    if (keepSq > 0f && dSq < keepSq) tooClose = true;

                    inRange++;
                    _seen.Add(e.Id);
                    if (_hp.TryGetValue(e.Id, out var prev))
                    {
                        if (e.HpCur < prev) progress = true;
                        if (e.HpCur != prev) _hp[e.Id] = e.HpCur;
                    }
                    else
                    {
                        _hp[e.Id] = e.HpCur;
                    }
                }
            }

            // Tracked monsters that vanished from the in-range set (killed and despawned, or walked off) →
            // treat as progress only if they are no longer in the list at all (despawn). Simply drop them.
            if (_hp.Count > _seen.Count)
            {
                List<uint>? gone = null;
                foreach (var id in _hp.Keys)
                    if (!_seen.Contains(id)) (gone ??= new()).Add(id);
                if (gone is not null)
                {
                    var listed = entities is null ? null : new HashSet<uint>(IdsOf(entities));
                    foreach (var id in gone)
                    {
                        _hp.Remove(id);
                        if (listed is not null && !listed.Contains(id)) progress = true; // despawned = killed
                    }
                }
            }

            if (inRange == 0)
            {
                // Just killed the engaged mob(s) and the pack is not done: hold the mover for the retarget
                // grace while another hostile is inside attack range — the rotation picks it up next tick.
                if (_fighting && !_fleeing && RetargetGrace > TimeSpan.Zero && nowUtc - _lastEngagedUtc < RetargetGrace
                    && attackRange > r && CombatAssist.HasHostileInRange(entities, player, attackRange, _ignoredSnapshot))
                    return new(true, true, false, 0, "retargeting");
                _fighting = false;
                _fleeing = false;
                return new(false, false, false, 0, "clear");
            }
            _lastEngagedUtc = nowUtc;

            // Low-HP flee with hysteresis: start under FleeBelowPct, stop at FleeRecoverPct.
            if (_fleeing)
            {
                if (playerHpPct >= Math.Max(FleeBelowPct, FleeRecoverPct)) _fleeing = false;
            }
            else if (FleeBelowPct > 0f && playerHpPct < FleeBelowPct)
            {
                _fleeing = true;
            }
            if (_fleeing)
            {
                _fighting = false;
                return new(false, false, false, inRange, $"fleeing (hp {playerHpPct:F0}%)", Flee: true);
            }

            if (!_fighting)
            {
                _fighting = true;
                _lastProgressUtc = nowUtc;
            }
            else if (progress)
            {
                _lastProgressUtc = nowUtc;
            }

            if (tooClose)
                return new(false, true, false, inRange, $"kiting ({inRange} in range)", Kite: true);

            if (StallAfter > TimeSpan.Zero && nowUtc - _lastProgressUtc >= StallAfter)
            {
                // Nothing in range has taken damage for a while: give up on THESE monsters for now.
                var until = nowUtc + IgnoreFor;
                foreach (var id in _seen)
                {
                    _ignoreUntil[id] = until;
                    _hp.Remove(id);
                }
                _ignoredSnapshot = new HashSet<uint>(_ignoreUntil.Keys);
                _fighting = false;
                return new(false, false, true, inRange, $"stalled → skipping {inRange}");
            }

            return new(true, true, false, inRange, $"fighting {inRange}");
        }
    }

    private static IEnumerable<uint> IdsOf(IReadOnlyList<Poe2Live.EntityDot> entities)
    {
        foreach (var e in entities) yield return e.Id;
    }

    /// <summary>
    /// Where to run: away from the centroid of the hostiles inside <paramref name="threatRange"/>, up to
    /// <paramref name="distance"/> cells, along the most open of 16 directions (ray-sampled against
    /// <paramref name="walkable"/> when terrain is known; straight away otherwise). Directions that head
    /// back into the pack (dot &lt; -0.2) are never chosen.
    /// </summary>
    public static bool TryFleePoint(
        IReadOnlyList<Poe2Live.EntityDot>? entities,
        NumVec2 player,
        float threatRange,
        float distance,
        byte[]? walkable,
        int width,
        int height,
        out NumVec2 point)
    {
        point = player;
        if (entities is null || distance <= 0f) return false;
        var rSq = Math.Max(0f, threatRange) * Math.Max(0f, threatRange);
        var sum = NumVec2.Zero;
        var n = 0;
        foreach (var e in entities)
        {
            if (!CombatAssist.IsHostile(e)) continue;
            var d = e.Grid - player;
            if (d.LengthSquared() > rSq) continue;
            sum += e.Grid;
            n++;
        }
        if (n == 0) return false;

        var away = player - sum / n;
        away = away.LengthSquared() < 1e-3f ? new NumVec2(1f, 0f) : NumVec2.Normalize(away);

        var hasTerrain = walkable is not null && width > 0 && height > 0;
        var bestScore = float.MinValue;
        var found = false;
        for (var i = 0; i < 16; i++)
        {
            var ang = i * (MathF.PI * 2f / 16f);
            var dir = new NumVec2(MathF.Cos(ang), MathF.Sin(ang));
            var dot = NumVec2.Dot(dir, away);
            if (dot < -0.2f) continue;

            var reach = distance;
            if (hasTerrain)
            {
                reach = 0f;
                for (var step = 1f; step <= distance; step += 1f)
                {
                    var c = player + dir * step;
                    var cx = (int)MathF.Round(c.X);
                    var cy = (int)MathF.Round(c.Y);
                    if ((uint)cx >= (uint)width || (uint)cy >= (uint)height || walkable![cy * width + cx] == 0) break;
                    reach = step;
                }
                if (reach < 4f) continue;
            }

            var score = reach * (0.5f + dot);
            if (score > bestScore)
            {
                bestScore = score;
                point = player + dir * reach;
                found = true;
            }
        }
        return found;
    }
}
