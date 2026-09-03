using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Input;

/// <summary>
/// Character speed from the grid-position history, and a self-calibrating walk / run classifier so nobody has
/// to tune "is the run key working" by hand. Feed <see cref="Push"/> every tick with whether the run key is
/// currently held: the walking speed is learned while it is not, the running speed while it has been held for
/// a moment. <see cref="RunKeyIgnored"/> then says "you have been holding run long enough, yet you are only
/// walking" — the executor re-presses the key. Pure; no clock of its own.
/// </summary>
public sealed class SpeedMeter
{
    /// <summary>Speed is measured over this window of position samples.</summary>
    public int WindowMs { get; set; } = 400;

    /// <summary>The run key must have been held this long before its speed counts as "running" (roll wind-up).</summary>
    public int RunSettleMs { get; set; } = 600;

    /// <summary>Speeds under this (cells/s) are "standing" and never train the classifier.</summary>
    public float MinMoveSpeed { get; set; } = 1.5f;

    /// <summary>Held run counts as ignored when speed is under walk × this (once a walk speed is known).</summary>
    public float RunFactor { get; set; } = 1.2f;

    /// <summary>Fixed walking speed (cells/s) — when &gt; 0 it replaces the learned value and learning stops.</summary>
    public float FixedWalkSpeed { get; set; }

    /// <summary>Fixed running speed (cells/s) — when &gt; 0 it replaces the learned value and learning stops.</summary>
    public float FixedRunSpeed { get; set; }

    private readonly List<(NumVec2 pos, DateTime at)> _hist = new();
    private DateTime _heldSince = DateTime.MinValue;
    private bool _wasHeld;

    /// <summary>Current speed in grid cells per second (0 with fewer than two samples).</summary>
    public float Speed { get; private set; }

    private float _walk, _run;

    /// <summary>Walking speed in use (fixed when set, else learned; 0 until seen).</summary>
    public float WalkSpeed => FixedWalkSpeed > 0f ? FixedWalkSpeed : _walk;

    /// <summary>Running speed in use (fixed when set, else learned; 0 until seen).</summary>
    public float RunSpeed => FixedRunSpeed > 0f ? FixedRunSpeed : _run;

    public bool WalkIsFixed => FixedWalkSpeed > 0f;

    public bool RunIsFixed => FixedRunSpeed > 0f;

    public bool HasWalk => WalkSpeed > 0f;

    public bool HasRun => RunSpeed > 0f;

    public bool Moving => Speed >= MinMoveSpeed;

    /// <summary>Faster than walking: above the walk/run midpoint when both are known, else above walk × RunFactor.</summary>
    public bool Running => Moving && HasWalk && Speed > (HasRun && RunSpeed > WalkSpeed ? (WalkSpeed + RunSpeed) * 0.5f : WalkSpeed * RunFactor);

    /// <summary>How long the run key has been held (zero when it is not).</summary>
    public TimeSpan HeldFor(DateTime nowUtc) => _wasHeld ? nowUtc - _heldSince : TimeSpan.Zero;

    /// <summary>
    /// The run key has been held past <see cref="RunSettleMs"/>, we are moving, a walk speed is known, and we are
    /// still only walking → the game did not take the press. Never true before a walk speed has been learned.
    /// </summary>
    public bool RunKeyIgnored(DateTime nowUtc)
        => _wasHeld && HeldFor(nowUtc) >= TimeSpan.FromMilliseconds(RunSettleMs) && Moving && HasWalk && Speed < WalkSpeed * RunFactor;

    public void Push(NumVec2 grid, DateTime nowUtc, bool runHeld)
    {
        if (runHeld && !_wasHeld) _heldSince = nowUtc;
        _wasHeld = runHeld;

        _hist.Add((grid, nowUtc));
        var cutoff = nowUtc.AddMilliseconds(-Math.Max(50, WindowMs));
        while (_hist.Count > 2 && _hist[0].at < cutoff) _hist.RemoveAt(0);
        if (_hist.Count < 2) { Speed = 0f; return; }
        var (p0, t0) = _hist[0];
        var dt = (float)(nowUtc - t0).TotalSeconds;
        if (dt < 0.05f) return;
        // Teleport / zone load: a jump no character makes in one window → drop the history, keep the last speed.
        var dist = NumVec2.Distance(grid, p0);
        if (dist / dt > 80f) { _hist.Clear(); _hist.Add((grid, nowUtc)); return; }
        Speed = dist / dt;

        if (!Moving) return;
        const float k = 0.1f;
        if (!runHeld)
        {
            if (!WalkIsFixed) _walk = _walk > 0f ? _walk + (Speed - _walk) * k : Speed;
        }
        else if (!RunIsFixed && HeldFor(nowUtc) >= TimeSpan.FromMilliseconds(RunSettleMs) && (!HasWalk || Speed > WalkSpeed * RunFactor))
            _run = _run > 0f ? _run + (Speed - _run) * k : Speed;
    }

    /// <summary>Forget the learned speeds (new character / zone with different movement speed is fine to keep; call on request).</summary>
    public void Reset()
    {
        _hist.Clear();
        Speed = 0f;
        _walk = 0f;
        _run = 0f;
        _wasHeld = false;
    }

    public string Note => !Moving ? "standing"
        : Running ? $"running {Speed:0.0} c/s"
        : HasWalk ? $"walking {Speed:0.0} c/s" : $"moving {Speed:0.0} c/s";
}
