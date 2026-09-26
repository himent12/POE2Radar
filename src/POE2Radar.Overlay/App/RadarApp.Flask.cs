using POE2Radar.Core.Native;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // ── Auto-flask (opt-in input). Foreground + in-game gated; F8 master kill-switch.
    //    Flask keys are configurable in RadarSettings (LifeKey/ManaKey). ──
    private bool _autoFlask = true;

    private DateTime _lifeFiredAt = DateTime.MinValue, _manaFiredAt = DateTime.MinValue;

    private DateTime _nextToggleAt = DateTime.MinValue;

    private float _hpPct = 100f, _manaPct = 100f, _esPct = 100f;

    private volatile bool _playerAlive = true;

    private string _flaskNote = "";

    /// <summary>
    /// Auto-flask: press the life/mana flask key when the corresponding pool drops below its
    /// threshold. Hard-gated: enabled + PoE2 is the foreground window + per-flask cooldown.
    /// The life flask's trigger pool is selectable (LifeFlaskMode): Health%, Energy Shield%, or
    /// Either — ES is ignored on builds with no ES pool, so "Either" is safe for a pure-life build.
    /// Render thread; reads vitals on the render reader stack.
    /// </summary>
    private void TickAutoFlask(nint localPlayer)
    {
        // No plausible vitals read (Life component missing, or vital offsets drifted past the auto-
        // relocation's reach): DON'T fire — firing on unknown HP would either spam or never trigger.
        // Surface it so a post-patch break is visible instead of silently "armed but never fires".
        if (_liveRender.PlayerVitals(localPlayer) is not { } v)
        {
            _flaskNote = "paused (vitals unreadable — offsets may have drifted)";
            return;
        }
        _hpPct = v.HpPct; _manaPct = v.ManaPct; _esPct = v.EsPct;
        _playerAlive = v.HpCur > 0;

        if (!_autoFlask) { _flaskNote = "OFF (F8)"; return; }
        if (!GameFocused()) { _flaskNote = "paused (PoE2 not focused)"; return; }
        if (!_playerAlive) { _flaskNote = "paused (dead)"; return; }
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

    private void ToggleAutoFlask()
    {
        _autoFlask = !_autoFlask;
        _settings.AutoFlaskEnabled = _autoFlask;   // persist so the choice survives a restart
        _settings.Save();
        ShowToast(_autoFlask ? "Auto-flask ON" : "Auto-flask OFF — press F8 to turn it back on");
    }

    // Reused per frame: the status strip + INSERT rail read it during this frame's draw only.
    private readonly List<StatusChip> _statusChips = new(4);

    /// <summary>The live module list for the on-overlay status strip and the INSERT menu rail.</summary>
    private IReadOnlyList<StatusChip> BuildStatusChips()
    {
        _statusChips.Clear();
        _statusChips.Add(new StatusChip("Flask", _autoFlask, _flaskNote, "F8"));
        _statusChips.Add(new StatusChip("Buffs", _buffKeeperArmed, _buffNote, _settings.BuffKeeper.ToggleHotkey));
        if (_settings.Trade.Enabled)
        {
            var open = OpenTradeCount();
            _statusChips.Add(new StatusChip("Trade", _tradeLog.CurrentPath is not null,
                _tradeLog.CurrentPath is null ? "Client.txt not found" : open > 0 ? $"{open} open" : "watching", ""));
        }
        return _statusChips;
    }
}
