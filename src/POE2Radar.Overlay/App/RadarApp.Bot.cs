using System.Linq;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // ── Auto-flask (opt-in input). Foreground + in-game gated; F8 master kill-switch.
    //    Flask keys are configurable in RadarSettings (LifeKey/ManaKey). ──
    private bool _autoFlask = true;

    private DateTime _lifeFiredAt = DateTime.MinValue, _manaFiredAt = DateTime.MinValue;

    private DateTime _nextToggleAt = DateTime.MinValue;

    private float _hpPct = 100f, _manaPct = 100f, _esPct = 100f;

    private string _flaskNote = "";

    // Combat assist (opt-in input). Same gates as auto-flask; F4 kill-switch. Default OFF.
    // Per-skill last-fire clocks + round-robin cursor (one tap per tick).
    private bool _combatAssist;

    private DateTime[] _combatFiredAt = Array.Empty<DateTime>();

    private int _combatNextIndex;

    private string _combatNote = "OFF (F4)";

    private DateTime _questUseFiredAt = DateTime.MinValue;

    // Fight watchdog (render thread): pauses movement only while an engaged hostile is actually being
    // damaged; gives up on monsters nothing lands on so the bot never stands still next to 1-2 mobs.
    private readonly CombatWatch _combatWatch = new();

    private volatile bool _inCombat;

    // Path move (opt-in input). Same gates as combat assist; F5 kill-switch. Default OFF.
    // Also armed while F3 bot master is on. Walks the first SelectedPath.
    private bool _moveEnabled;

    private DateTime _moveFiredAt = DateTime.MinValue;

    private string _moveNote = "OFF (F5)";

    private volatile int _hostilesNear;

    // Death / respawn (render thread). HpCur == 0 from the Life component = dead; the game is showing the
    // death screen. We stop all input, wait RespawnDelayMs, then tap the resurrect key every 2 s until alive.
    private volatile bool _playerDead;

    private DateTime _deadSinceUtc = DateTime.MinValue;

    private DateTime _nextRespawnTapUtc = DateTime.MinValue;

    private int _respawnTaps;

    private volatile string _respawnNote = "";

    private bool _lastRealFocused;

    /// <summary>
    /// Auto-respawn. Returns true while the character is dead (callers suppress all other input). Any bot
    /// arm bit (bot / clear / combat / move) enables it; plain overlay users without automation are left alone.
    /// </summary>
    private bool TickRespawn(bool inGame, bool focused)
    {
        var automation = _botEnabled || _mapClear || CombatArmed || MoveArmed;
        if (!inGame || !_playerDead || !automation)
        {
            if (_deadSinceUtc != DateTime.MinValue && !_playerDead)
            {
                Console.WriteLine("\nRespawn: alive again.");
                _combatWatch.Reset();
                _clearStuckId = null;
            }
            _deadSinceUtc = DateTime.MinValue;
            _respawnTaps = 0;
            _respawnNote = "";
            return inGame && _playerDead;
        }
        var now = DateTime.UtcNow;
        if (_deadSinceUtc == DateTime.MinValue)
        {
            _deadSinceUtc = now;
            _nextRespawnTapUtc = now.AddMilliseconds(Math.Max(500, _settings.RespawnDelayMs));
            ReleaseHeldKeys();
            Console.WriteLine("\nRespawn: character died — resurrecting at checkpoint.");
        }
        _respawnNote = "dead → respawning";
        if (!_settings.AutoRespawn || !focused) { _respawnNote = _settings.AutoRespawn ? "dead (PoE2 not focused)" : "dead (auto-respawn off)"; return true; }
        if (now >= _nextRespawnTapUtc && _settings.RespawnKey is >= 1 and <= 255)
        {
            GameHost.TapKey((ushort)_settings.RespawnKey);
            _respawnTaps++;
            _nextRespawnTapUtc = now.AddSeconds(2);
            _respawnNote = $"dead → respawn tap {_respawnTaps}";
        }
        return true;
    }

    /// <summary>
    /// Auto-flask: press the life/mana flask key when the corresponding pool drops below its
    /// threshold. Hard-gated: enabled + PoE2 is the foreground window + per-flask cooldown.
    /// The life flask's trigger pool is selectable (LifeFlaskMode): Health%, Energy Shield%, or
    /// Either — ES is ignored on builds with no ES pool, so "Either" is safe for a pure-life build.
    /// </summary>
    private void TickAutoFlask(nint localPlayer)
    {
        // No plausible vitals read (Life component missing, or vital offsets drifted past the auto-
        // relocation's reach): DON'T fire — firing on unknown HP would either spam or never trigger.
        // Surface it so a post-patch break is visible instead of silently "armed but never fires".
        if (_live.PlayerVitals(localPlayer) is not { } v)
        {
            _flaskNote = "paused (vitals unreadable — offsets may have drifted)";
            return;
        }
        _hpPct = v.HpPct; _manaPct = v.ManaPct; _esPct = v.EsPct;
        _playerDead = v.HpCur <= 0;

        if (!_autoFlask) { _flaskNote = "OFF (F8)"; return; }
        if (GameHost.GetForegroundWindow() != _gameHwnd && !_settings.PlayInBackground) { _flaskNote = "paused (PoE2 not focused)"; return; }
        _flaskNote = "armed";

        // Which pool(s) the single life-flask key watches. ES only participates when a real ES pool is
        // present (HasEs) — a build with no shield never trips the ES branch even in "Either" mode.
        var hpLow = v.HpPct < _settings.LifeThresholdPct;
        var esLow = v.HasEs && v.EsPct < _settings.EsThresholdPct;
        var (lifeTrigger, lifeReason) = _settings.LifeFlaskMode switch
        {
            "EnergyShield" => (esLow, $"es@{v.EsPct:F0}%"),
            "Either"       => (hpLow || esLow, hpLow ? $"life@{v.HpPct:F0}%" : $"es@{v.EsPct:F0}%"),
            _              => (hpLow, $"life@{v.HpPct:F0}%"), // "Health" (default)
        };

        var now = DateTime.UtcNow;
        if (lifeTrigger && now - _lifeFiredAt >= TimeSpan.FromMilliseconds(_settings.LifeCooldownMs))
        {
            GameHost.TapKey((ushort)_settings.LifeKey); _lifeFiredAt = now; _flaskNote = lifeReason;
        }
        if (v.ManaPct < _settings.ManaThresholdPct &&
            now - _manaFiredAt >= TimeSpan.FromMilliseconds(_settings.ManaCooldownMs))
        {
            GameHost.TapKey((ushort)_settings.ManaKey); _manaFiredAt = now; _flaskNote = $"mana@{v.ManaPct:F0}%";
        }
    }

    /// <summary>
    /// Combat assist: tap the next ready skill in the rotation when a hostile monster is in grid range.
    /// Same gates as auto-flask (armed + focused + in-game + per-skill cooldown). Decision is
    /// <see cref="CombatAssist.Decide"/>; this method only taps and updates the status note.
    /// </summary>
    private void TickCombatAssist(bool inGame, bool focused, NumVec2 player,
        IReadOnlyList<Poe2Live.EntityDot> entities, POE2Radar.Core.Game.Vector3? playerWorld,
        CombatWatch.Result watch)
    {
        var now = DateTime.UtcNow;
        var src = _settings.CombatSkills;
        var n = src?.Count ?? 0;
        EnsureCombatClocks(n);
        var specs = new CombatAssist.Skill[n];
        for (var i = 0; i < n; i++)
        {
            var sk = src![i];
            specs[i] = new CombatAssist.Skill(sk.Key, sk.CooldownMs, sk.Range, Math.Max(1, sk.MinTargets), sk.RareOnly, sk.HpBelowPct, sk.Enabled);
        }
        var decision = CombatAssist.Decide(new CombatAssist.Snapshot(
            Armed: CombatArmed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: player,
            Entities: entities,
            Range: _settings.CombatRange,
            NowUtc: now,
            LastFireUtc: _combatFiredAt,
            Skills: specs,
            NextIndex: _combatNextIndex,
            IgnoreIds: _combatWatch.IgnoredIds,
            Mode: CombatAssist.ParseTargetMode(_settings.CombatTargetMode),
            PriorityOrder: string.Equals(_settings.CombatRotationMode, "Priority", StringComparison.OrdinalIgnoreCase),
            PlayerHpPct: _hpPct,
            KeyboardOnly: GameHost.IsBackgroundActive && !GameHost.BackgroundSupportsMouse));
        _hostilesNear = decision.HostilesInRange;
        if (GameHost.IsBackgroundActive && !GameHost.BackgroundSupportsMouse && !decision.ShouldTap && decision.HasTarget
            && (src?.All(k => k.Key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06) ?? false))
            decision = decision with { Note = "background: bind skills to keys" };
        if (_playerDead) decision = decision with { Note = _respawnNote };
        _combatNote = watch.Flee ? watch.Note
            : watch.Stalled ? decision.Note + " (stalled, moving on)"
            : watch.Fighting ? $"{decision.Note} ({watch.Note})"
            : decision.Note;
        if (watch.Flee) return; // running, not swinging
        if (!decision.ShouldTap) return;
        // Aim: PoE2 fires a skill toward the cursor, so warp it onto the target first — otherwise the
        // rotation swings at wherever the last move-click left the cursor and the mob never dies.
        if (decision.HasTarget) AimAtEntity(decision.TargetGrid, decision.TargetWorld, playerWorld);
        GameHost.TapKey(decision.Vk);
        if ((uint)decision.SkillIndex < (uint)_combatFiredAt.Length)
            _combatFiredAt[decision.SkillIndex] = now;
        _combatNextIndex = decision.NextIndex;
    }

    /// <summary>Grow/shrink the per-skill last-fire clocks to match the current rotation length.</summary>
    private void EnsureCombatClocks(int n)
    {
        if (_combatFiredAt.Length == n) return;
        var next = new DateTime[n];
        var copy = Math.Min(_combatFiredAt.Length, n);
        if (copy > 0) Array.Copy(_combatFiredAt, next, copy);
        _combatFiredAt = next;
        if (n <= 0 || _combatNextIndex >= n) _combatNextIndex = 0;
    }

    /// <summary>
    /// Path move: tap WASD or click-to-move toward the next waypoint of the first selected path.
    /// Armed by F5 or by F3 bot master. Same gates as combat assist
    /// (armed + focused + in-game + cooldown). Decision is <see cref="PathMove.Decide"/>;
    /// this method only taps (and aims the cursor for Click) and updates the status note.
    /// </summary>
    private void TickPathMove(bool inGame, bool focused, NumVec2 player,
        IReadOnlyList<SelectedPath> paths, POE2Radar.Core.Game.Vector3? playerWorld, bool inCombat, bool fleeing = false)
    {
        var now = DateTime.UtcNow;
        IReadOnlyList<(int x, int y)> waypoints = paths.Count > 0
            ? paths[0].Points
            : Array.Empty<(int x, int y)>();
        var terrain = _terrain;
        var decision = PathMove.Decide(new PathMove.Snapshot(
            Armed: MoveArmed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: player,
            Waypoints: waypoints,
            ArriveRadius: _settings.MoveArriveRadius,
            NowUtc: now,
            LastFireUtc: _moveFiredAt,
            CooldownMs: _settings.MoveCooldownMs,
            Method: _settings.MoveMethod ?? "WASD",
            KeyW: _settings.MoveKeyW,
            KeyA: _settings.MoveKeyA,
            KeyS: _settings.MoveKeyS,
            KeyD: _settings.MoveKeyD,
            ClickKey: _settings.MoveClickKey,
            PauseForCombat: inCombat,
            LookAhead: _settings.MoveLookAhead,
            Walkable: terrain?.Walkable,
            Width: terrain?.Width ?? 0,
            Height: terrain?.Height ?? 0,
            Diagonals: _settings.MoveDiagonals,
            AxisRotationDeg: _settings.MoveAxisRotationDeg,
            RunKey: _settings.MoveRunKey,
            RunEnabled: _settings.MoveRunEnabled,
            PrevHoldKeys: _prevDirKeys));
        _moveNote = fleeing ? "kite → " + decision.Note : decision.Note;

        // Held keys: WASD direction set + run key while moving. Diff against what is currently down so a
        // direction change is one KeyUp + one KeyDown, not a tap storm; everything is released the instant
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
        if (wantsDir && !fleeing)
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
        if (wantsDir && now < _sidestepUntil)
        {
            var dir = PathMove.DirectionOf(decision.HoldKeys!, _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD);
            var perp = (x: -dir.y * _sidestepSign, y: dir.x * _sidestepSign);
            _wantKeys.Clear();
            foreach (var k in PathMove.KeysFor(perp, _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD)) _wantKeys.Add(k);
            _moveNote = "stuck → sidestep";
        }

        // Run key: keep it down across the brief "no path" / "arrived" gaps between route replans and
        // re-targets (RunLinger), so it is pressed ONCE and held for the whole trip. Hard stops (combat,
        // unfocused, disarmed, not in game) release it immediately.
        var hardStop = !MoveArmed || !focused || !inGame || inCombat;
        if (decision.Moving) _runHoldUntil = now + RunLinger;
        else if (hardStop) _runHoldUntil = DateTime.MinValue;
        if (now < _runHoldUntil && _settings.MoveRunEnabled && _settings.MoveRunKey is >= 1 and <= 255)
            _wantKeys.Add((ushort)_settings.MoveRunKey);
        ApplyHeldKeys();

        if (!decision.ShouldTap) return;
        if (PathMove.IsClick(_settings.MoveMethod))
            AimClick(decision.TargetX, decision.TargetY, playerWorld);
        GameHost.TapKey(decision.Vk);
        _moveFiredAt = now;
    }

    private readonly HashSet<ushort> _heldKeys = new();

    private readonly HashSet<ushort> _wantKeys = new();

    private static readonly TimeSpan RunLinger = TimeSpan.FromMilliseconds(600);

    private IReadOnlyList<ushort>? _prevDirKeys;

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

    private void ApplyHeldKeys()
    {
        _keyScratch.Clear();
        foreach (var k in _heldKeys) if (!_wantKeys.Contains(k)) _keyScratch.Add(k);
        foreach (var k in _keyScratch) { GameHost.KeyUp(k); _heldKeys.Remove(k); }
        foreach (var k in _wantKeys) if (_heldKeys.Add(k)) GameHost.KeyDown(k);
    }

    /// <summary>Let go of every held movement/run key (shutdown, disarm, focus loss).</summary>
    private void ReleaseHeldKeys()
    {
        foreach (var k in _heldKeys) GameHost.KeyUp(k);
        _heldKeys.Clear();
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

    private void AimWorld(float wx, float wy, float wz)
    {
        if (_cameraMatrix is not { } m) return;
        if (!POE2Radar.Core.Pathfinding.MapProjection.TryWorldToScreen(m, wx, wy, wz, _window.Width, _window.Height, out var sx, out var sy))
            return;
        const float pad = 8f;
        if (sx < pad || sy < pad || sx > _window.Width - pad || sy > _window.Height - pad) return;
        GameHost.SetCursorPos(_window.OriginX + (int)MathF.Round(sx), _window.OriginY + (int)MathF.Round(sy));
    }

    /// <summary>
    /// Quest interact/use: tap the configured use key when the player is inside the use radius of
    /// the selected quest target. Armed by F3. Decision is <see cref="QuestFollow.DecideUse"/>.
    /// </summary>
    private void TickQuestUse(bool inGame, bool focused, NumVec2 player,
        POE2Radar.Core.Game.Vector3? playerWorld, bool inCombat)
    {
        var now = DateTime.UtcNow;
        var decision = QuestFollow.DecideUse(new QuestFollow.UseSnapshot(
            Armed: _questFollow,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: player,
            HasTarget: _questFollowHasGrid,
            TargetGrid: new NumVec2(_questFollowGx, _questFollowGy),
            ArriveRadius: _settings.QuestUseRadius,
            NowUtc: now,
            LastFireUtc: _questUseFiredAt,
            CooldownMs: _settings.QuestUseCooldownMs,
            UseKey: _settings.QuestUseKey,
            PauseForCombat: inCombat));
        if (!decision.ShouldTap) return;
        if (GameHost.WouldDrop(decision.Vk)) { _questFollowNote = "background: interact needs a keyboard key"; return; }
        if (decision.Vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06)
            AimClick((int)MathF.Round(_questFollowGx), (int)MathF.Round(_questFollowGy), playerWorld);
        GameHost.TapKey(decision.Vk);
        _questUseFiredAt = now;
        _questFollowNote = "use";
    }

    private bool CombatArmed => _combatAssist || _botEnabled || _mapClear;

    private bool MoveArmed => _moveEnabled || _botEnabled || _mapClear;
}
