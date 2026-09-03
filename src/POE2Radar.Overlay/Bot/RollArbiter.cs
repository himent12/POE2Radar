namespace POE2Radar.Overlay.Input;

/// <summary>
/// The ONE owner of the dodge-roll key. PoE2 binds Space to both "dodge roll" and (for the bot) "travel fast" —
/// the mover, the combo macro ("dodge after"), the flee/kite logic and boss mode all want to press it, and when
/// two of them did so at once the roll fired early, twice, or in the wrong direction. Every roll now goes
/// through <see cref="TryRequest"/>: exactly one owner at a time, a roll is a fixed press
/// (<see cref="PressMs"/>) followed by a recovery lockout (<see cref="RecoverMs"/>) during which nobody may
/// roll, and no roll may start inside a cast's animation window (<see cref="NoteCast"/>) because a roll cancels
/// the cast. Pure state machine: the caller does the KeyDown/KeyUp from the <see cref="Command"/> it returns.
/// Direction is a WASD sign pair — the executor holds those keys for <see cref="DirHoldMs"/> so the character
/// rolls along them instead of toward the cursor.
/// </summary>
public sealed class RollArbiter
{
    public enum Owner { None, Mover, Combo, Flee, Boss }

    public enum Phase { Idle, Pressed, Recovering }

    /// <summary>What the executor must do this tick. <see cref="Release"/> = let go of the dodge key now;
    /// <see cref="ReleaseDir"/> = let go of the direction keys held for this roll (non-mover owners).</summary>
    public readonly record struct Command(bool Release, bool ReleaseDir, Owner Owner, (int x, int y) Dir);

    /// <summary>How long the dodge key is physically held. The game polls key state per frame; a zero-length
    /// press is missed, a long one is not "more roll".</summary>
    public int PressMs { get; set; } = 60;

    /// <summary>Roll animation + recovery: no roll and no cast until it has elapsed.</summary>
    public int RecoverMs { get; set; } = 650;

    /// <summary>How long the roll's direction keys stay held after the press (the roll follows held WASD).</summary>
    public int DirHoldMs { get; set; } = 300;

    private Phase _phase;
    private Owner _owner;
    private (int x, int y) _dir;
    private DateTime _pressedAt;
    private DateTime _lockUntil;
    private DateTime _castUntil;
    private bool _dirHeld;

    public Phase State => _phase;

    public Owner Current => _owner;

    /// <summary>Pressed or recovering — nobody else may roll and no cast should start.</summary>
    public bool Busy => _phase != Phase.Idle;

    public bool KeyHeld => _phase == Phase.Pressed;

    /// <summary>Direction sign pair of the roll in flight (or the last one).</summary>
    public (int x, int y) Direction => _dir;

    /// <summary>Extend the "a cast is animating" window: no roll starts before <paramref name="nowUtc"/> +
    /// <paramref name="animMs"/>. Overlapping calls keep the later end.</summary>
    public void NoteCast(DateTime nowUtc, int animMs)
    {
        var until = nowUtc.AddMilliseconds(Math.Max(0, animMs));
        if (until > _castUntil) _castUntil = until;
    }

    public bool InCast(DateTime nowUtc) => nowUtc < _castUntil;

    /// <summary>
    /// Ask to roll now. Granted (true) only when idle, outside every cast window and for a real owner; the
    /// caller then presses the dodge key (and holds <paramref name="dir"/> when it is not the mover's own
    /// held WASD). <paramref name="reason"/> explains a refusal for the status note.
    /// </summary>
    public bool TryRequest(Owner who, (int x, int y) dir, DateTime nowUtc, out string reason)
    {
        if (who == Owner.None) { reason = "no owner"; return false; }
        switch (_phase)
        {
            case Phase.Pressed:
                reason = $"roll in progress ({_owner})";
                return false;
            case Phase.Recovering:
                reason = $"roll recovery {Math.Max(0, (_lockUntil - nowUtc).TotalMilliseconds):0} ms ({_owner})";
                return false;
        }
        if (nowUtc < _castUntil)
        {
            reason = $"cast animation {(_castUntil - nowUtc).TotalMilliseconds:0} ms";
            return false;
        }
        _phase = Phase.Pressed;
        _owner = who;
        _dir = dir;
        _pressedAt = nowUtc;
        _lockUntil = nowUtc.AddMilliseconds(Math.Max(0, PressMs) + Math.Max(0, RecoverMs));
        _dirHeld = who is Owner.Combo or Owner.Boss && dir != (0, 0);
        reason = $"roll ({who})";
        return true;
    }

    /// <summary>Advance the timers. Returns the release commands exactly once each.</summary>
    public Command Tick(DateTime nowUtc)
    {
        var release = false;
        var releaseDir = false;
        var owner = _owner;
        switch (_phase)
        {
            case Phase.Pressed:
                if (nowUtc - _pressedAt >= TimeSpan.FromMilliseconds(Math.Max(0, PressMs)))
                {
                    _phase = Phase.Recovering;
                    release = true;
                }
                break;
            case Phase.Recovering:
                if (nowUtc >= _lockUntil)
                {
                    _phase = Phase.Idle;
                    _owner = Owner.None;
                }
                break;
        }
        if (_dirHeld && nowUtc - _pressedAt >= TimeSpan.FromMilliseconds(Math.Max(0, DirHoldMs)))
        {
            _dirHeld = false;
            releaseDir = true;
        }
        return new(release, releaseDir, owner, _dir);
    }

    /// <summary>Drop everything (focus loss, death, disarm). The caller releases the physical keys.</summary>
    public void Reset()
    {
        _phase = Phase.Idle;
        _owner = Owner.None;
        _dirHeld = false;
        _castUntil = DateTime.MinValue;
    }

    /// <summary>One-line status for the overlay ("roll recovery 400 ms (combo)", "cast animation 120 ms", "").</summary>
    public string Note(DateTime nowUtc) => _phase switch
    {
        Phase.Pressed => $"rolling ({_owner})",
        Phase.Recovering => $"roll recovery {Math.Max(0, (_lockUntil - nowUtc).TotalMilliseconds):0} ms ({_owner})",
        _ => nowUtc < _castUntil ? $"cast animation {(_castUntil - nowUtc).TotalMilliseconds:0} ms" : "",
    };

    /// <summary>Sign pair pointing from <paramref name="from"/> away from <paramref name="threat"/> (8-way).
    /// (1,0) when the two coincide.</summary>
    public static (int x, int y) AwayFrom(System.Numerics.Vector2 from, System.Numerics.Vector2 threat)
    {
        var away = from - threat;
        if (away.LengthSquared() < 1e-3f) return (1, 0);
        away = System.Numerics.Vector2.Normalize(away);
        var dir = ((int)MathF.Round(away.X), (int)MathF.Round(away.Y));
        if (dir == (0, 0))
            dir = MathF.Abs(away.X) > MathF.Abs(away.Y) ? (MathF.Sign(away.X), 0) : (0, MathF.Sign(away.Y));
        return dir;
    }
}
