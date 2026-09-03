using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;

namespace POE2Radar.Overlay;

/// <summary>
/// Render-thread executors for the combo macro, the dodge-roll arbiter and boss-mode dodging. Decisions are
/// pure (<see cref="RollArbiter"/>, <see cref="BossFight"/>, <see cref="CombatAssist"/>); this file only
/// presses keys and keeps the status notes.
/// </summary>
public sealed partial class RadarApp
{
    // ── Combo macro: one skill cast expanded into timed steps (N taps or a hold, then an optional dodge-roll
    //    away from the target, then a rotation lockout). Non-blocking: each tick performs whatever is due. ──
    private enum MacroStep { Aim, KeyDown, KeyUp, Dodge, Done }
    private uint _macroTargetId;
    private POE2Radar.Core.Game.Vector3 _macroTargetWorld;
    private uint _lastTargetId;
    // A combo in flight owns the character: the mover (and its travel rolls) must stay idle until it ends.
    private volatile bool _comboBusy;
    private readonly Queue<(MacroStep Step, ushort Vk, TimeSpan After)> _macro = new();
    private DateTime _macroNextUtc = DateTime.MinValue;
    private DateTime _comboLockUntil = DateTime.MinValue;
    private NumVec2 _macroTargetGrid;
    private readonly List<ushort> _macroHeld = new();
    private int _macroCastsDone, _macroCastsTotal, _macroDodgeRetries, _macroCastMs;

    // ── Roll arbiter: the single owner of the dodge key. ──
    private readonly RollArbiter _roll = new();
    private readonly List<ushort> _rollDirHeld = new();
    private volatile string _rollNote = "";

    // ── Boss mode. ──
    private readonly BossFight _bossFight = new();
    private volatile string _bossNote = "";
    private DateTime _bossReturnUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Expand one cast into timed steps. Timeline for "×3, cast time 330, dodge after":
    /// R↓ … R↑(+60) · 330 · R↓ … R↑ · 330 · R↓ … R↑ · 330 (let the LAST cast finish — a roll cancels it) ·
    /// roll away (arbiter: dir↓ + Space↓ … Space↑(+press) · dir↑(+300) · recovery) · then-wait → rotation free.
    /// Taps are real DOWN/UP pairs with a held duration (a zero-length press is missed by a game that polls
    /// key state per frame). Each cast re-aims at the target's live position and extends the arbiter's cast
    /// window so no roll can land inside the animation.
    /// </summary>
    private void StartComboMacro(CombatAssist.Skill spec, CombatAssist.Decision d, DateTime now)
    {
        AbortComboMacro();
        _macroTargetId = d.TargetId;
        _macroTargetGrid = d.TargetGrid;
        _macroTargetWorld = d.TargetWorld;
        var castMs = Math.Max(60, spec.RepeatGapMs);
        var tapMs = Math.Clamp(_settings.CombatTapHoldMs, 30, 200);
        var hold = spec.HoldMs > 0 ? spec.HoldMs : tapMs;
        _macroCastMs = Math.Max(castMs, hold);
        _macroCastsDone = 0;
        _macroCastsTotal = spec.Repeat;
        _macroDodgeRetries = 0;
        for (var i = 0; i < spec.Repeat; i++)
        {
            var after = i == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(castMs);
            _macro.Enqueue((MacroStep.Aim, 0, after));
            _macro.Enqueue((MacroStep.KeyDown, d.Vk, TimeSpan.Zero));
            _macro.Enqueue((MacroStep.KeyUp, d.Vk, TimeSpan.FromMilliseconds(hold)));
        }
        if (spec.DodgeAfter && _settings.CombatDodgeKey is >= 1 and <= 255)
        {
            // Wait a full cast time after the last tap so the roll does not cancel the final cast.
            _macro.Enqueue((MacroStep.Dodge, 0, TimeSpan.FromMilliseconds(Math.Max(0, castMs - hold))));
            _macro.Enqueue((MacroStep.Done, 0, TimeSpan.FromMilliseconds(_settings.RollPressMs + Math.Max(0, _settings.CombatDodgeRecoverMs) + spec.NextDelayMs)));
        }
        else
        {
            _macro.Enqueue((MacroStep.Done, 0, TimeSpan.FromMilliseconds(spec.NextDelayMs)));
        }
        _macroNextUtc = now;
    }

    /// <summary>Advance the macro; true while steps remain.</summary>
    private bool RunComboMacro(DateTime now, NumVec2 player, POE2Radar.Core.Game.Vector3? playerWorld,
        IReadOnlyList<Poe2Live.EntityDot> entities)
    {
        // One step per tick at most for DOWN/UP pairs: never collapse a press and its release into the same
        // instant even after a frame hitch.
        var steps = 0;
        while (_macro.Count > 0 && now >= _macroNextUtc && steps++ < 2)
        {
            var (step, vk, _) = _macro.Dequeue();
            switch (step)
            {
                case MacroStep.Aim:
                {
                    // Live target position when it is still listed; else the position at macro start.
                    var grid = _macroTargetGrid; var world = _macroTargetWorld;
                    foreach (var e in entities) if (e.Id == _macroTargetId) { grid = e.Grid; world = e.World; break; }
                    _macroTargetGrid = grid;
                    AimAtEntity(grid, world, playerWorld);
                    break;
                }
                case MacroStep.KeyDown:
                    GameHost.KeyDown(vk); _macroHeld.Add(vk);
                    _roll.NoteCast(now, _macroCastMs);
                    _macroCastsDone++;
                    break;
                case MacroStep.KeyUp: GameHost.KeyUp(vk); _macroHeld.Remove(vk); break;
                case MacroStep.Dodge:
                {
                    // Roll AWAY from the target through the arbiter (direction = held WASD, cursor behind us as a
                    // fallback for a build that rolls toward the cursor). Refused (another roll recovering, cast
                    // window) → retry every 50 ms for up to a second, then give the roll up rather than block.
                    var dir = RollArbiter.AwayFrom(player, _macroTargetGrid);
                    var aim = player + new NumVec2(dir.x, dir.y) * 8f;
                    AimClick((int)MathF.Round(aim.X), (int)MathF.Round(aim.Y), playerWorld);
                    if (!TryRoll(RollArbiter.Owner.Combo, dir, now, out var why))
                    {
                        if (_macroDodgeRetries++ < 20)
                        {
                            var rest = _macro.ToArray();
                            _macro.Clear();
                            _macro.Enqueue((MacroStep.Dodge, 0, TimeSpan.FromMilliseconds(50)));
                            foreach (var r in rest) _macro.Enqueue(r);
                            _rollNote = "combo roll waiting: " + why;
                        }
                        else _rollNote = "combo roll skipped: " + why;
                    }
                    break;
                }
                case MacroStep.Done:
                    break;
            }
            if (_macro.Count > 0)
            {
                var next = _macro.Peek();
                _macroNextUtc = now + next.After;
                if (next.Step == MacroStep.Done) { _comboLockUntil = _macroNextUtc; _macro.Dequeue(); }
            }
        }
        return _macro.Count > 0;
    }

    private void AbortComboMacro()
    {
        _macro.Clear();
        foreach (var k in _macroHeld) GameHost.KeyUp(k);
        _macroHeld.Clear();
    }

    /// <summary>Why the rotation is not deciding right now (status note), or "" when it is free.</summary>
    private string ComboBusyReason(DateTime now)
    {
        if (_macro.Count > 0)
            return _macroCastsDone < _macroCastsTotal ? $"casting {_macroCastsDone}/{_macroCastsTotal}" : "casting (roll / then-wait)";
        if (now < _comboLockUntil) return $"combo then-wait {(_comboLockUntil - now).TotalMilliseconds:0} ms";
        return _roll.Note(now);
    }

    // ── Roll executor ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask the arbiter for a roll and press the keys when granted: direction keys first (unless the mover /
    /// flee already holds them), then the dodge key. Rolls the character along held WASD.
    /// </summary>
    private bool TryRoll(RollArbiter.Owner who, (int x, int y) dir, DateTime now, out string reason)
    {
        var vk = _settings.CombatDodgeKey;
        if (vk is < 1 or > 255) { reason = "no dodge key bound"; return false; }
        _roll.PressMs = Math.Clamp(_settings.RollPressMs, 30, 200);
        _roll.RecoverMs = Math.Clamp(_settings.CombatDodgeRecoverMs, 0, 3000);
        if (!_roll.TryRequest(who, dir, now, out reason)) return false;
        if (who is RollArbiter.Owner.Combo or RollArbiter.Owner.Boss)
        {
            foreach (var k in PathMove.KeysFor(dir, _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD))
            {
                if (_heldKeys.Contains(k)) continue;
                GameHost.KeyDown(k); _rollDirHeld.Add(k);
            }
        }
        GameHost.KeyDown((ushort)vk);
        _rollNote = reason;
        return true;
    }

    /// <summary>Per-tick: release the dodge key after the press, the roll's direction keys after DirHoldMs.</summary>
    private void TickRoll(DateTime now)
    {
        var cmd = _roll.Tick(now);
        if (cmd.Release && _settings.CombatDodgeKey is >= 1 and <= 255) GameHost.KeyUp((ushort)_settings.CombatDodgeKey);
        if (cmd.ReleaseDir)
        {
            foreach (var k in _rollDirHeld) if (!_heldKeys.Contains(k)) GameHost.KeyUp(k);
            _rollDirHeld.Clear();
        }
        if (!_roll.Busy && !_roll.InCast(now) && !_rollNote.StartsWith("combo roll", StringComparison.Ordinal)) _rollNote = "";
        else if (_roll.Busy) _rollNote = _roll.Note(now);
    }

    /// <summary>Drop the roll state and every key it holds (focus loss, death, disarm).</summary>
    private void AbortRoll()
    {
        if (_roll.KeyHeld && _settings.CombatDodgeKey is >= 1 and <= 255) GameHost.KeyUp((ushort)_settings.CombatDodgeKey);
        foreach (var k in _rollDirHeld) if (!_heldKeys.Contains(k)) GameHost.KeyUp(k);
        _rollDirHeld.Clear();
        _roll.Reset();
        _rollNote = "";
    }

    // ── Boss mode ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boss dodging: while a unique is engaged, roll on the timer / on a player-life spike. A spike aborts a
    /// combo in flight (incoming damage beats finishing the cast); the timer waits for the combo. Returns the
    /// status fragment for the combat note.
    /// </summary>
    private void TickBossDodge(CombatWatch.Result watch, NumVec2 player, POE2Radar.Core.Game.Vector3? playerWorld, DateTime now)
    {
        _bossFight.DodgeIntervalMs = _settings.BossDodgeIntervalMs;
        _bossFight.SpikePct = _settings.BossDodgeSpikePct;
        if (!watch.Boss)
        {
            _bossFight.WantDodge(false, 0, default, _hpPct, now, out _);
            _bossNote = "";
            return;
        }
        if (!_bossFight.WantDodge(true, watch.BossId, watch.BossGrid, _hpPct, now, out var why))
        {
            _bossNote = "boss: " + why;
            return;
        }
        var spike = why.StartsWith("hp spike", StringComparison.Ordinal);
        if (_macro.Count > 0 && !spike) { _bossNote = "boss: timed roll after the combo"; return; }
        if (spike) AbortComboMacro();
        var dir = RollArbiter.AwayFrom(player, watch.BossGrid);
        var aim = player + new NumVec2(dir.x, dir.y) * 8f;
        AimClick((int)MathF.Round(aim.X), (int)MathF.Round(aim.Y), playerWorld);
        if (TryRoll(RollArbiter.Owner.Boss, dir, now, out var granted))
        {
            _bossFight.Rolled(now);
            _bossNote = $"boss: rolling ({why})";
        }
        else _bossNote = $"boss: roll wanted ({why}) — {granted}";
    }

    /// <summary>After a respawn: walk back to where the boss was last seen (re-engage) for up to two minutes.</summary>
    private void ArmBossReturn(DateTime now)
    {
        if (!_settings.BossReengage || !_bossFight.HasBoss) return;
        _bossReturnUntilUtc = now.AddMinutes(2);
        Console.WriteLine($"\nBoss: walking back to ({_bossFight.BossGrid.X:0},{_bossFight.BossGrid.Y:0}) to re-engage.");
    }

    /// <summary>World-thread: the boss-arena cell id to route to while a return is armed, else null. Clears the
    /// return once a unique is visible again or we are standing in the arena.</summary>
    private string? BossReturnTarget(NumVec2 player, bool uniqueVisible, DateTime now)
    {
        if (now >= _bossReturnUntilUtc || !_bossFight.HasBoss) return null;
        var g = _bossFight.BossGrid;
        if (uniqueVisible || NumVec2.Distance(player, g) < 10f) { _bossReturnUntilUtc = DateTime.MinValue; return null; }
        return MapClear.CellId((int)MathF.Round(g.X), (int)MathF.Round(g.Y));
    }
}
