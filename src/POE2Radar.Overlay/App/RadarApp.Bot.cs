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
    private bool TickRespawn(bool inGame, bool focused, nint inGameState)
    {
        var automation = _botEnabled || _mapClear || CombatArmed || MoveArmed;
        if (!inGame || !_playerDead || !automation)
        {
            if (_deadSinceUtc != DateTime.MinValue && !_playerDead)
            {
                Console.WriteLine("\nRespawn: alive again.");
                _combatWatch.Reset();
                _clearStuckId = null;
                ArmBossReturn(DateTime.UtcNow);
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
            AbortComboMacro();
            AbortRoll();
            Console.WriteLine("\nRespawn: character died — resurrecting at checkpoint.");
        }
        _respawnNote = "dead → respawning";
        if (!_settings.AutoRespawn || !focused) { _respawnNote = _settings.AutoRespawn ? "dead (PoE2 not focused)" : "dead (auto-respawn off)"; return true; }
        if (now < _nextRespawnTapUtc) return true;
        _nextRespawnTapUtc = now.AddSeconds(2);
        _respawnTaps++;
        // The death screen is a real button — find "Resurrect at Checkpoint" in the visible UI tree and click
        // its centre. Keyboard fallback (Space/Enter) only if the element cannot be found.
        if (inGameState != 0 && _liveRender.TryFindVisibleTextRect(inGameState, "Resurrect at Checkpoint",
                _window.Width, _window.Height, out var bx, out var by, out var bw, out var bh, out _))
        {
            var cx = _window.OriginX + (int)MathF.Round(bx + bw * 0.5f);
            var cy = _window.OriginY + (int)MathF.Round(by + bh * 0.5f);
            GameHost.SetCursorPos(cx, cy);
            GameHost.TapKey(0x01);
            _respawnNote = $"dead → clicking Resurrect ({_respawnTaps})";
            return true;
        }
        if (_settings.RespawnKey is >= 1 and <= 255)
        {
            GameHost.TapKey((ushort)_settings.RespawnKey);
            _respawnNote = $"dead → button not found, key tap {_respawnTaps}";
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
            specs[i] = new CombatAssist.Skill(sk.Key, sk.CooldownMs, sk.Range, Math.Max(1, sk.MinTargets), sk.RareOnly, sk.HpBelowPct, sk.Enabled,
                Math.Clamp(sk.Repeat, 1, 10), Math.Clamp(sk.RepeatGapMs, 30, 2000), Math.Clamp(sk.HoldMs, 0, 10000), sk.DodgeAfter, Math.Clamp(sk.NextDelayMs, 0, 10000));
        }
        // Boss mode first: a life spike may abort the combo in flight and roll instead.
        TickBossDodge(watch, player, playerWorld, now);
        // A combo macro in flight owns the keyboard, and so does a roll (press + recovery): advance the macro
        // and skip deciding — a cast inside the roll recovery is eaten by the game.
        var macroBusy = RunComboMacro(now, player, playerWorld, entities);
        var busy = macroBusy || now < _comboLockUntil || _roll.Busy;
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
            IgnoreIds: CombatIgnoreIds(),
            Mode: CombatAssist.ParseTargetMode(_settings.CombatTargetMode),
            PriorityOrder: string.Equals(_settings.CombatRotationMode, "Priority", StringComparison.OrdinalIgnoreCase),
            PlayerHpPct: _hpPct,
            KeyboardOnly: GameHost.IsBackgroundActive && !GameHost.BackgroundSupportsMouse,
            Busy: busy,
            PreferredTargetId: macroBusy ? _macroTargetId : _lastTargetId,
            BusyReason: busy ? ComboBusyReason(now) : null,
            ComboSkipHpPct: _settings.CombatComboSkipHpPct));
        _comboBusy = macroBusy || now < _comboLockUntil;
        // Sticky target survives the combo: while busy the decision carries no target, so keep the macro's.
        _lastTargetId = decision.HasTarget ? decision.TargetId : (busy ? (macroBusy ? _macroTargetId : _lastTargetId) : 0);
        _hostilesNear = decision.HostilesInRange;
        if (GameHost.IsBackgroundActive && !GameHost.BackgroundSupportsMouse && !decision.ShouldTap && decision.HasTarget
            && (src?.All(k => k.Key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06) ?? false))
            decision = decision with { Note = "background: bind skills to keys" };
        if (_playerDead) decision = decision with { Note = _respawnNote };
        _combatNote = watch.Flee ? watch.Note
            : watch.Stalled ? decision.Note + " (stalled, moving on)"
            : watch.Fighting ? $"{decision.Note} ({watch.Note})"
            : decision.Note;
        if (!string.IsNullOrEmpty(_bossNote)) _combatNote += " · " + _bossNote;
        if (watch.Flee) { AbortComboMacro(); return; } // running, not swinging
        if (!decision.ShouldTap) return;
        // Aim: PoE2 fires a skill toward the cursor, so warp it onto the target first — otherwise the
        // rotation swings at wherever the last move-click left the cursor and the mob never dies.
        if (decision.HasTarget) AimAtEntity(decision.TargetGrid, decision.TargetWorld, playerWorld);
        var spec = specs[decision.SkillIndex];
        var isCombo = spec.Repeat > 1 || spec.HoldMs > 0 || spec.DodgeAfter || spec.NextDelayMs > 0;
        if (isCombo)
        {
            // A combo commits us for a second or more: only start it with the target comfortably inside range
            // (85 %) so it cannot walk out halfway and waste the remaining casts.
            var reach = spec.Range > 0f ? spec.Range : _settings.CombatRange;
            if (NumVec2.Distance(player, decision.TargetGrid) > reach * 0.85f)
            {
                _combatNote = $"closing in ({NumVec2.Distance(player, decision.TargetGrid):0} / {reach * 0.85f:0})";
                return;
            }
            StartComboMacro(spec, decision, now);
        }
        else
        {
            GameHost.TapKey(decision.Vk);
            _roll.NoteCast(now, Math.Max(0, spec.RepeatGapMs)); // cast animation: no roll until it lands
        }
        if ((uint)decision.SkillIndex < (uint)_combatFiredAt.Length)
            _combatFiredAt[decision.SkillIndex] = now;
        _combatNextIndex = decision.NextIndex;
    }

    /// <summary>Watchdog ignores ∪ essence-imprisoned monsters (immune until their crystal is clicked).</summary>
    private IReadOnlyCollection<uint> CombatIgnoreIds()
    {
        var ign = _combatWatch.IgnoredIds;
        var imp = _imprisonedIds;
        if (imp.Count == 0) return ign;
        if (ign.Count == 0) return imp;
        var u = new HashSet<uint>(ign);
        u.UnionWith(imp);
        return u;
    }

    /// <summary>
    /// Map-event use: when the clear routine's current target is a clickable event and we are inside
    /// EventUseRadius, aim the cursor at it and tap interact (LMB). Each click is counted; after
    /// EventMaxClicks with the event still pending it is blacklisted for 90 s so a broken/unreachable one
    /// cannot hold the bot. A successful click on an essence frees the monster → the fight watchdog's
    /// ignore list is cleared so the freed rare is fought immediately.
    /// </summary>
    private void TickEventUse(bool inGame, bool focused, NumVec2 player, POE2Radar.Core.Game.Vector3? playerWorld, bool inCombat)
    {
        if (_eventTarget is not { } ev) { _eventNote = ""; return; }
        if (!_mapClear || !inGame || !focused || inCombat || _playerDead) { _eventNote = inCombat ? "event: waiting for fight" : ""; return; }
        var d = NumVec2.Distance(player, ev.Grid);
        var radius = Math.Max(1f, _settings.EventUseRadius);
        if (d > radius) { _eventNote = $"event: {ev.Label} {d:0} away"; return; }
        var now = DateTime.UtcNow;
        if (now - _eventFiredAt < TimeSpan.FromMilliseconds(Math.Max(200, _settings.EventUseCooldownMs))) return;
        if (GameHost.WouldDrop(_settings.QuestUseKey)) { _eventNote = "event: interact needs a keyboard key (background)"; return; }

        var clicks = _eventClicks.GetValueOrDefault(ev.Id) + 1;
        _eventClicks[ev.Id] = clicks;
        // One click is the whole interaction for chests / boxes / shrines / breaches / essences — the game's
        // Opened/complete flags can lag or never flip, so consider it DONE right away and move on rather than
        // standing there re-clicking until the blacklist kicks in. Stalled monsters get a few tries.
        var maxClicks = ev.Kind == MapEvents.Kind.Stalled ? Math.Max(1, _settings.EventMaxClicks) : 1;
        AimAtEntity(ev.Grid, ev.World, playerWorld);
        GameHost.TapKey((ushort)Math.Clamp(_settings.QuestUseKey, 1, 255));
        _eventFiredAt = now;
        _eventNote = $"event: clicked {ev.Label} ({clicks})";
        if (ev.Kind is MapEvents.Kind.Essence or MapEvents.Kind.Stalled) _combatWatch.Reset(); // freed rare → fight it now
        if (clicks >= maxClicks)
        {
            lock (_eventBlacklist) _eventBlacklist[ev.Id] = now.AddMinutes(30); // done for this zone
            _eventClicks.Remove(ev.Id);
            _eventTarget = null;   // next world tick re-picks immediately (no wait for the flag to flip)
            _clearStuckId = null;
        }
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
