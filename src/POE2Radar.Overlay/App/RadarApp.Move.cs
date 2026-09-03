using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;

namespace POE2Radar.Overlay;

/// <summary>Render-thread executor for path movement: held WASD, travel rolls via the arbiter, stuck watchdog,
/// cursor aiming. Decision is <see cref="PathMove.Decide"/>.</summary>
public sealed partial class RadarApp
{
    private readonly HashSet<ushort> _heldKeys = new();

    private readonly HashSet<ushort> _wantKeys = new();

    private static readonly TimeSpan RunLinger = TimeSpan.FromMilliseconds(600);

    private IReadOnlyList<ushort>? _prevDirKeys;

    private (int x, int y)? _prevMoveTarget;

    private DateTime _lastMovingUtc;

    // Stuck watchdog state (render thread).
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan SidestepFor = TimeSpan.FromMilliseconds(350);

    private const float StuckMoveCellsSq = 0.75f * 0.75f;

    private NumVec2 _stuckRef;

    private DateTime _stuckSince = DateTime.MinValue;

    private int _stuckKicks;

    private DateTime _sidestepUntil = DateTime.MinValue;

    private int _sidestepSign = 1;

    private bool _moveWasLive;

    private DateTime _runHoldUntil = DateTime.MinValue;

    private readonly List<ushort> _keyScratch = new();

    /// <summary>
    /// Path move: hold WASD (or click) toward the steer point of the first selected path. Armed by F5 or by
    /// F3 bot master. Same gates as combat assist. Decision is <see cref="PathMove.Decide"/>; this method only
    /// presses keys, asks the roll arbiter for travel rolls, and updates the status note.
    /// </summary>
    private void TickPathMove(bool inGame, bool focused, NumVec2 player,
        IReadOnlyList<SelectedPath> paths, POE2Radar.Core.Game.Vector3? playerWorld, bool inCombat, bool fleeing = false)
    {
        var now = DateTime.UtcNow;
        IReadOnlyList<(int x, int y)> waypoints = paths.Count > 0
            ? paths[0].Points
            : Array.Empty<(int x, int y)>();
        var kind = paths.Count > 0 ? paths[0].Kind : "";
        var terrain = _terrain;
        var comboPause = _comboBusy && !inCombat;
        var decision = PathMove.Decide(new PathMove.Snapshot(
            Armed: MoveArmed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: player,
            Waypoints: waypoints,
            ArriveRadius: PathMove.ArriveRadiusFor(kind, _settings.MoveArriveRadius, _settings.MoveArriveRadiusMob, _settings.MoveArriveRadiusEvent),
            NowUtc: now,
            LastFireUtc: _moveFiredAt,
            CooldownMs: _settings.MoveCooldownMs,
            Method: _settings.MoveMethod ?? "WASD",
            KeyW: _settings.MoveKeyW,
            KeyA: _settings.MoveKeyA,
            KeyS: _settings.MoveKeyS,
            KeyD: _settings.MoveKeyD,
            ClickKey: _settings.MoveClickKey,
            PauseForCombat: inCombat || _comboBusy,
            LookAhead: _settings.MoveLookAhead,
            Walkable: terrain?.Walkable,
            Width: terrain?.Width ?? 0,
            Height: terrain?.Height ?? 0,
            Diagonals: _settings.MoveDiagonals,
            AxisRotationDeg: _settings.MoveAxisRotationDeg,
            RunKey: _settings.MoveRunKey,
            RunEnabled: _settings.MoveRunEnabled,
            PrevHoldKeys: _prevDirKeys,
            PrevTarget: _prevMoveTarget,
            LastMovingUtc: _lastMovingUtc,
            CoastMs: _settings.MoveCoastMs,
            RollMinCells: _settings.MoveRollMinCells));
        var note = comboPause ? "paused: combo in flight" : decision.Note;
        if (!string.IsNullOrEmpty(kind) && decision.Moving && !decision.Coasting) note += $" → {kind}";
        _moveNote = fleeing ? "kite → " + note : note;
        if (decision.Moving && !decision.Coasting) { _lastMovingUtc = now; _prevMoveTarget = (decision.TargetX, decision.TargetY); }
        else if (!decision.Coasting) _prevMoveTarget = null;

        // Held keys: WASD direction set (+ a plain run key while moving). Diff against what is currently down so
        // a direction change is one KeyUp + one KeyDown, not a tap storm; everything is released the instant
        // the bot stops wanting to move (arrived / combat / unfocused / disarmed).
        // Focus / in-game regained: the game drops key state on focus loss while our bookkeeping still says
        // "held" → nothing would ever be re-pressed. Release everything so the next tick presses afresh.
        var live = focused && inGame;
        if (live && !_moveWasLive) ReleaseHeldKeys();
        _moveWasLive = live;

        _wantKeys.Clear();
        _prevDirKeys = decision.HoldKeys is { Count: > 0 } ? decision.HoldKeys : null;
        var wantsDir = decision.HoldKeys is { Count: > 0 };
        if (wantsDir) foreach (var k in decision.HoldKeys!) _wantKeys.Add(k);

        // Stuck watchdog: direction keys held but the character has not moved. Two "kicks" (release all,
        // re-press next tick — fixes a lost key-down / dropped run state), then a perpendicular sidestep to
        // slide off whatever wall corner we're pressed into; alternate sides. Reset the moment we move.
        if (wantsDir && !fleeing && !decision.Coasting)
        {
            if (NumVec2.DistanceSquared(player, _stuckRef) > StuckMoveCellsSq)
            {
                _stuckRef = player; _stuckSince = now; _stuckKicks = 0;
            }
            else if (_stuckSince == DateTime.MinValue)
            {
                _stuckRef = player; _stuckSince = now;
            }
            else if (now < _sidestepUntil)
            {
                // sidestep in progress — handled below
            }
            else if (now - _stuckSince > StuckAfter)
            {
                _stuckKicks++;
                _stuckSince = now;
                if (_stuckKicks <= 2)
                {
                    ReleaseHeldKeys();
                    _runHoldUntil = DateTime.MinValue;
                    _moveNote = $"stuck → re-press ({_stuckKicks})";
                    return; // keys re-pressed next tick
                }
                _sidestepSign = -_sidestepSign;
                _sidestepUntil = now + SidestepFor;
                _stuckKicks = 0;
                Console.WriteLine("\nPath move: stuck — sidestepping.");
            }
        }
        else
        {
            _stuckSince = DateTime.MinValue;
            _stuckKicks = 0;
        }
        var sidestepping = wantsDir && now < _sidestepUntil;
        if (sidestepping)
        {
            var dir = PathMove.DirectionOf(decision.HoldKeys!, _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD);
            var perp = (x: -dir.y * _sidestepSign, y: dir.x * _sidestepSign);
            _wantKeys.Clear();
            foreach (var k in PathMove.KeysFor(perp, _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD)) _wantKeys.Add(k);
            _moveNote = "stuck → sidestep";
        }

        // Run key. When it IS the dodge key (PoE2: Space) "running" means periodic dodge rolls, and those go
        // through the arbiter below — never held. A different run key is held across the brief "no path" /
        // "arrived" gaps between replans (RunLinger) so it is pressed ONCE per trip.
        var hardStop = !MoveArmed || !focused || !inGame || inCombat || _comboBusy;
        var runKey = _settings.MoveRunKey;
        var runIsRoll = _settings.MoveRunEnabled && runKey is >= 1 and <= 255 && runKey == _settings.CombatDodgeKey;
        if (!runIsRoll)
        {
            if (decision.Moving) _runHoldUntil = now + RunLinger;
            else if (hardStop) _runHoldUntil = DateTime.MinValue;
            if (now < _runHoldUntil && _settings.MoveRunEnabled && runKey is >= 1 and <= 255)
                _wantKeys.Add((ushort)runKey);
        }
        ApplyHeldKeys();

        // Run = the dodge key HELD through the arbiter (PoE2 chains rolls while it stays down). WASD: the
        // roll follows the held direction keys; Click: it follows the cursor, which sits on the steer point.
        // Released the instant we stop wanting to move, near corners / the goal (RollOk), while stuck, and for
        // any combat pause — a tap owner (combo / boss) asks for the release itself.
        if (runIsRoll)
        {
            var clickMove = PathMove.IsClick(_settings.MoveMethod);
            // Held for the WHOLE trip — no per-corner release (every release/re-press is a visible stutter and the
            // game needs a fresh press each time). Only a real stop lets go.
            var wantRun = (wantsDir || clickMove) && !sidestepping && !hardStop && decision.Moving;
            var nearHostiles = _settings.MoveRunStopNearHostiles && _hostilesNear > 0 && !fleeing;
            if (!wantRun) ReleaseRunHold(now);
            else if (nearHostiles) { ReleaseRunHold(now); _moveNote += $" · walking ({_hostilesNear} hostile in range)"; }
            else
            {
                if (clickMove && _roll.Holding) AimClick(decision.TargetX, decision.TargetY, playerWorld);
                TryRunHold(fleeing ? RollArbiter.Owner.Flee : RollArbiter.Owner.Mover, now, ref _moveNote);
            }
        }

        if (!decision.ShouldTap) return;
        if (PathMove.IsClick(_settings.MoveMethod))
            AimClick(decision.TargetX, decision.TargetY, playerWorld);
        GameHost.TapKey(decision.Vk);
        _moveFiredAt = now;
    }

    private void ApplyHeldKeys()
    {
        _keyScratch.Clear();
        foreach (var k in _heldKeys) if (!_wantKeys.Contains(k)) _keyScratch.Add(k);
        foreach (var k in _keyScratch) { GameHost.KeyUp(k); _heldKeys.Remove(k); }
        foreach (var k in _wantKeys) if (_heldKeys.Add(k)) GameHost.KeyDown(k);
    }

    /// <summary>Let go of every held movement/run key and any roll in flight (shutdown, disarm, focus loss).</summary>
    private void ReleaseHeldKeys()
    {
        foreach (var k in _heldKeys) GameHost.KeyUp(k);
        _heldKeys.Clear();
        AbortRoll();
    }

    /// <summary>Warp the cursor onto the projected waypoint so a click-to-move tap walks there.</summary>
    private void AimClick(int gridX, int gridY, POE2Radar.Core.Game.Vector3? playerWorld)
        => AimWorld(
            gridX * POE2Radar.Core.Pathfinding.GridConstants.GridToWorld,
            gridY * POE2Radar.Core.Pathfinding.GridConstants.GridToWorld,
            playerWorld?.Z ?? 0f);

    /// <summary>Warp the cursor onto a monster: its own world position when read, else its grid at player height.</summary>
    private void AimAtEntity(NumVec2 grid, POE2Radar.Core.Game.Vector3 world, POE2Radar.Core.Game.Vector3? playerWorld)
    {
        if (world.X != 0f || world.Y != 0f)
            AimWorld(world.X, world.Y, world.Z != 0f ? world.Z : playerWorld?.Z ?? 0f);
        else
            AimClick((int)MathF.Round(grid.X), (int)MathF.Round(grid.Y), playerWorld);
    }

    private int _lastAimX = int.MinValue, _lastAimY = int.MinValue;

    /// <summary>Warp the cursor to a world point. A move under 4 px from where we last put it is skipped — the
    /// per-cast re-aim on a target that barely moved would otherwise jitter the cursor every frame.</summary>
    private void AimWorld(float wx, float wy, float wz)
    {
        if (_cameraMatrix is not { } m) return;
        if (!POE2Radar.Core.Pathfinding.MapProjection.TryWorldToScreen(m, wx, wy, wz, _window.Width, _window.Height, out var sx, out var sy))
            return;
        const float pad = 8f;
        if (sx < pad || sy < pad || sx > _window.Width - pad || sy > _window.Height - pad) return;
        var px = _window.OriginX + (int)MathF.Round(sx);
        var py = _window.OriginY + (int)MathF.Round(sy);
        if (Math.Abs(px - _lastAimX) < 4 && Math.Abs(py - _lastAimY) < 4) return;
        _lastAimX = px; _lastAimY = py;
        GameHost.SetCursorPos(px, py);
    }
}
