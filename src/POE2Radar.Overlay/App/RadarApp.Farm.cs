using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core.Game;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;

namespace POE2Radar.Overlay;

/// <summary>
/// Farm loop (F11): clear the farm area with map-clear, cast a town portal, take it, walk to the town
/// waypoint, ctrl-click the farm area in the waypoint menu (fresh instance), clear again — until F11.
/// Phase logic is <see cref="FarmLoop.Next"/>; this partial owns the state and the I/O split:
/// the WORLD thread advances phases + sets the nav target (<see cref="ApplyFarmLoop"/>), the RENDER thread
/// performs inputs (portal key, waypoint-menu click) and probes the UI (<see cref="TickFarmInput"/>).
/// </summary>
public sealed partial class RadarApp
{
    private volatile bool _farmLoop;
    private volatile string _farmNote = "OFF (F11)";
    private volatile string _farmAreaCode = "";
    private int _farmPhaseRaw;                     // FarmLoop.Phase (volatile via Interlocked/Volatile)
    private DateTime _farmPhaseSince = DateTime.MinValue;
    private long _farmLastInputTicks;              // written by the render thread
    private volatile bool _farmMenuAreaVisible;    // render probe: waypoint menu shows the farm area name
    private DateTime _farmClearDoneSince = DateTime.MinValue;
    private DateTime _farmMenuProbeAt = DateTime.MinValue;
    private int _farmRuns;
    private volatile bool _mapClearIdle;           // set by ApplyMapClear: nothing left to clear/fight/click

    private FarmLoop.Phase FarmPhase => (FarmLoop.Phase)Volatile.Read(ref _farmPhaseRaw);
    private DateTime FarmLastInput => new(Volatile.Read(ref _farmLastInputTicks), DateTimeKind.Utc);

    /// <summary>Nav-use is armed while the loop is walking INTO something (portal / waypoint).</summary>
    private bool FarmUseArmed => _farmLoop && FarmPhase is FarmLoop.Phase.EnterPortal or FarmLoop.Phase.TownWaypoint;

    private string FarmAreaName => string.IsNullOrEmpty(_farmAreaCode) ? "" : ZoneGuide.Shared.FriendlyName(_farmAreaCode);

    private void ToggleFarmLoop()
    {
        if (!_farmLoop)
        {
            var here = _state.AreaCode;
            if (!string.IsNullOrEmpty(here) && !QuestFollow.IsTownOrHideout(here)) _farmAreaCode = here;
            else if (!string.IsNullOrEmpty(_settings.FarmAreaCode)) _farmAreaCode = _settings.FarmAreaCode;
            if (string.IsNullOrEmpty(_farmAreaCode))
            {
                _farmNote = "stand in the area to farm, then F11";
                Console.WriteLine("\nFarm loop: stand inside the area you want to farm (not a town) and press F11 again.");
                return;
            }
            _settings.FarmAreaCode = _farmAreaCode;
        }
        _farmLoop = !_farmLoop;
        _settings.FarmLoopEnabled = _farmLoop;
        _settings.Save();
        Volatile.Write(ref _farmPhaseRaw, (int)FarmLoop.Phase.Off);
        _farmPhaseSince = DateTime.UtcNow;
        _farmClearDoneSince = DateTime.MinValue;
        _farmMenuAreaVisible = false;
        if (!_farmLoop)
        {
            _questFollowId = null; _questFollowHasGrid = false;
            ReleaseHeldKeys();
        }
        _farmNote = _farmLoop ? $"armed · {FarmAreaName}" : "OFF (F11)";
        _mapClearNote = _farmLoop ? "farm loop" : (_mapClear ? "armed" : "OFF (F2)");
        _moveNote = MoveArmed ? "armed" : "OFF (F5)";
        Console.WriteLine($"\nFarm loop: {(_farmLoop ? "ON — " + FarmAreaName + " (" + _farmAreaCode + ")" : "OFF")}");
    }

    /// <summary>World thread: advance the loop one tick and point the nav/use machinery at its target.</summary>
    private void ApplyFarmLoop(string areaCode, NumVec2 player)
    {
        var now = DateTime.UtcNow;
        var phase = FarmPhase;
        var inTown = QuestFollow.IsTownOrHideout(areaCode);

        // Clearing: map-clear drives the nav target; "cleared" must hold for the settle time with nothing near.
        if (phase == FarmLoop.Phase.Clearing)
        {
            ApplyMapClear(areaCode, player);
            var idle = _mapClearIdle && _hostilesNear == 0 && !_inCombat;
            if (!idle) _farmClearDoneSince = DateTime.MinValue;
            else if (_farmClearDoneSince == DateTime.MinValue) _farmClearDoneSince = now;
        }
        var clearDone = _farmClearDoneSince != DateTime.MinValue
                        && (now - _farmClearDoneSince).TotalMilliseconds >= Math.Max(500, _settings.FarmClearSettleMs);

        var step = FarmLoop.Next(new FarmLoop.Snapshot(
            phase, _farmPhaseSince, now, areaCode, _farmAreaCode, inTown, clearDone,
            _entities, player, _farmMenuAreaVisible, FarmLastInput,
            PortalKeyBound: _settings.FarmPortalKey is >= 1 and <= 255));

        if (step.Phase != phase || step.Reset)
        {
            if (step.Phase != phase)
            {
                Console.WriteLine($"\nFarm loop: {phase} → {step.Phase} ({step.Note})");
                if (step.Phase == FarmLoop.Phase.Clearing && phase == FarmLoop.Phase.Returning) _farmRuns++;
            }
            Volatile.Write(ref _farmPhaseRaw, (int)step.Phase);
            _farmPhaseSince = now;
            _farmClearDoneSince = DateTime.MinValue;
        }
        Volatile.Write(ref _farmWantInput, (int)step.Input);

        if (step.Phase != FarmLoop.Phase.Clearing)
            SetAutoNavTarget(step.TargetId, step.Note, "Farm loop");

        var runs = _farmRuns > 0 ? $" · run {_farmRuns + 1}" : "";
        _farmNote = step.Phase == FarmLoop.Phase.Clearing ? $"{_mapClearNote}{runs}" : $"{step.Note}{runs}";
    }

    private int _farmWantInput; // FarmLoop.Input requested by the world thread for the render thread

    /// <summary>Render thread: perform the requested one-shot input and probe the waypoint menu.</summary>
    private void TickFarmInput(nint inGameState, bool inGame, bool focused)
    {
        if (!_farmLoop || !inGame || !focused || _playerDead || _comboBusy || _bossDecision.Active || _bossDodge.Busy) return;
        var now = DateTime.UtcNow;
        var phase = FarmPhase;

        // Probe the UI for the farm area's name (the waypoint menu label) — throttled, only when relevant.
        if (phase is FarmLoop.Phase.TownWaypoint or FarmLoop.Phase.WaypointMenu && now >= _farmMenuProbeAt && inGameState != 0)
        {
            _farmMenuProbeAt = now.AddMilliseconds(250);
            var name = FarmAreaName;
            _farmMenuAreaVisible = name.Length >= 3
                && _liveRender.TryFindVisibleTextRect(inGameState, name, _window.Width, _window.Height, out _, out _, out _, out _, out _);
        }
        else if (phase is not (FarmLoop.Phase.TownWaypoint or FarmLoop.Phase.WaypointMenu))
        {
            _farmMenuAreaVisible = false;
        }

        var want = (FarmLoop.Input)Volatile.Read(ref _farmWantInput);
        if (want == FarmLoop.Input.None) return;
        if ((now - FarmLastInput).TotalMilliseconds < 1000) return; // one input per second at most
        switch (want)
        {
            case FarmLoop.Input.CastPortal when _settings.FarmPortalKey is >= 1 and <= 255:
                if (_comboBusy) return;
                ReleaseHeldKeys();
                GameHost.TapKey((ushort)_settings.FarmPortalKey);
                Volatile.Write(ref _farmLastInputTicks, now.Ticks);
                Volatile.Write(ref _farmWantInput, (int)FarmLoop.Input.None);
                break;

            case FarmLoop.Input.ClickWaypointArea:
            {
                var name = FarmAreaName;
                if (inGameState == 0 || name.Length < 3
                    || !_liveRender.TryFindVisibleTextRect(inGameState, name, _window.Width, _window.Height, out var bx, out var by, out var bw, out var bh, out _))
                    return;
                var cx = _window.OriginX + (int)MathF.Round(bx + bw * 0.5f);
                var cy = _window.OriginY + (int)MathF.Round(by + bh * 0.5f);
                // Ctrl+click = open a NEW instance of the area (a plain click would rejoin the cleared one).
                ReleaseHeldKeys();
                GameHost.SetCursorPos(cx, cy);
                GameHost.KeyDown(0x11);
                Thread.Sleep(40);
                GameHost.TapKey(0x01);
                Thread.Sleep(40);
                GameHost.KeyUp(0x11);
                Volatile.Write(ref _farmLastInputTicks, now.Ticks);
                Volatile.Write(ref _farmWantInput, (int)FarmLoop.Input.None);
                break;
            }
        }
    }
}
