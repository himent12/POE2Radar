namespace POE2Radar.Overlay.Input;

/// <summary>
/// Boss-mode dodge trigger. There is no telegraph data in memory, so the two signals we have are used:
/// a periodic roll every <see cref="DodgeIntervalMs"/> while a unique is engaged, and an "incoming damage"
/// roll when the PLAYER's life drops by <see cref="SpikePct"/> points within <see cref="SpikeWindowMs"/>
/// (a wind-up that already connected once tends to be followed by more). Pure: the executor asks
/// <see cref="WantDodge"/> each tick and reports a granted roll back via <see cref="Rolled"/>.
/// </summary>
public sealed class BossFight
{
    /// <summary>Roll at least this often while a boss is engaged (0 = timer off).</summary>
    public int DodgeIntervalMs { get; set; } = 4000;

    /// <summary>Life lost (percentage points) inside the window that triggers a roll (0 = spike trigger off).</summary>
    public float SpikePct { get; set; } = 12f;

    public int SpikeWindowMs { get; set; } = 600;

    /// <summary>Minimum gap between two spike-triggered rolls (a single big hit must not chain rolls).</summary>
    public int MinGapMs { get; set; } = 900;

    private readonly List<(DateTime at, float hp)> _hp = new();
    private DateTime _lastRollUtc = DateTime.MinValue;
    private bool _engaged;

    public bool Engaged => _engaged;

    /// <summary>Boss id / last known grid position — kept until <see cref="Reset"/> so a fight can be resumed after a respawn.</summary>
    public uint BossId { get; private set; }

    public System.Numerics.Vector2 BossGrid { get; private set; }

    public bool HasBoss => BossId != 0;

    public void Reset()
    {
        _hp.Clear();
        _engaged = false;
        _lastRollUtc = DateTime.MinValue;
        BossId = 0;
        BossGrid = default;
    }

    /// <summary>Forget the remembered boss (killed / zone left) but keep engagement state.</summary>
    public void ForgetBoss()
    {
        BossId = 0;
        BossGrid = default;
    }

    public void Rolled(DateTime nowUtc)
    {
        _lastRollUtc = nowUtc;
        _hp.Clear();
    }

    /// <summary>
    /// One tick. <paramref name="bossInRange"/> = a unique is inside the engage range; <paramref name="playerHpPct"/>
    /// is the live life %. Returns true when a roll should be requested now; <paramref name="reason"/> says why
    /// ("boss timer", "hp spike -18%") or, when false, what we are waiting for.
    /// </summary>
    public bool WantDodge(bool bossInRange, uint bossId, System.Numerics.Vector2 bossGrid, float playerHpPct, DateTime nowUtc, out string reason)
    {
        if (!bossInRange)
        {
            _hp.Clear();
            _engaged = false;
            reason = "";
            return false;
        }
        if (bossId != 0) { BossId = bossId; BossGrid = bossGrid; }
        if (!_engaged)
        {
            _engaged = true;
            _lastRollUtc = nowUtc;   // the interval counts from engagement — no roll on the first frame
        }

        _hp.Add((nowUtc, playerHpPct));
        var cutoff = nowUtc.AddMilliseconds(-Math.Max(50, SpikeWindowMs));
        while (_hp.Count > 1 && _hp[0].at < cutoff) _hp.RemoveAt(0);

        var sinceRoll = nowUtc - _lastRollUtc;
        if (SpikePct > 0f && sinceRoll >= TimeSpan.FromMilliseconds(Math.Max(0, MinGapMs)))
        {
            var peak = float.MinValue;
            foreach (var (_, hp) in _hp) if (hp > peak) peak = hp;
            var drop = peak - playerHpPct;
            if (drop >= SpikePct)
            {
                reason = $"hp spike -{drop:0}%";
                return true;
            }
        }
        if (DodgeIntervalMs > 0 && sinceRoll >= TimeSpan.FromMilliseconds(DodgeIntervalMs))
        {
            reason = "boss timer";
            return true;
        }
        reason = DodgeIntervalMs > 0
            ? $"next timed roll in {Math.Max(0, DodgeIntervalMs - sinceRoll.TotalMilliseconds) / 1000.0:0.0}s"
            : "watching life for a burst";
        return false;
    }
}
