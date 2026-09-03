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
    private DateTime _nextPathKeyAt = DateTime.MinValue;

    private DateTime _nextBrowserAt = DateTime.MinValue;

    private DateTime _nextCombatToggleAt = DateTime.MinValue;

    private DateTime _nextQuestToggleAt = DateTime.MinValue;

    private DateTime _nextMapClearToggleAt = DateTime.MinValue;

    private DateTime _nextMoveToggleAt = DateTime.MinValue;

    // ── Phase 1: exploration fog + draw-only path guidance (all gated by RadarSettings flags). ──
    // Unified navigation targets: a single list built each world tick from BOTH terrain-tile
    // landmarks AND entity POIs (bosses, expedition, waypoints…), each addressed by a STABLE STRING
    // id ("t:<path>" / "e:<entityId>"). Multi-select: each selected target draws its OWN full A*
    // route in its OWN color (by selection-order slot). F6 adds the nearest not-yet-selected target;
    // F7 clears the whole selection; clicking a legend row toggles that target. Selection is capped
    // at the palette size so colors stay distinct (and per-tick planning stays bounded). On a zone
    // change the selection is cleared, then the persistent auto-nav patterns re-select matching
    // targets in the new zone.
    private const int MapClearVk = 0x71;

    private const int QuestFollowVk = 0x72;

    private const int PathMoveVk = 0x74;

    private const int AddNearestVk = 0x75;

    private const int ClearPathsVk = 0x76;

    // INSERT in-game menu (render thread owns it; clicks arrive on the window thread → volatile).
    private volatile bool _insMenuOpen;

    private volatile int _insMenuTab;

    private DateTime _nextInsToggleAt = DateTime.MinValue;

    /// <summary>
    /// Per-frame click-through toggle. The overlay captures clicks (click-through OFF) only while the
    /// overlay is active (PoE2 foreground) AND the cursor is currently over a legend row. In every
    /// other case — overlay hidden, PoE2 not foreground, or the map closed (legend empty) — it stays
    /// click-through so we never eat the user's game clicks. Reads only the cursor; sends nothing.
    /// </summary>
    private void UpdateClickThrough(bool active)
    {
        // INSERT menu open: the overlay owns the pointer outright (whole window, exclusive grab where the
        // platform allows) — the game must not get clicks meant for the menu. Otherwise the old per-widget rule.
        var menu = active && _insMenuOpen;
        var overWidget = menu
                         || (active
                             && _renderer.LegendRowRects.Count > 0
                             && GameHost.GetCursorPos(out var pt)
                             && HitTestWidget(ScreenToClientPoint(pt)) is not null);
        _window.SetClickThrough(!overWidget);
        _window.CapturePointer(menu);
        PollMenuClick(menu);
    }

    // Fallback click detection for the INSERT menu: poll the physical button instead of relying on the window
    // receiving ButtonPress (a fullscreen game holding a pointer grab swallows those). Rising edge of LMB while
    // the cursor is over a menu control → dispatch. Deduped against the event path by a short window.
    private bool _menuLmbWasDown;

    private DateTime _lastMenuClickUtc = DateTime.MinValue;

    private void PollMenuClick(bool menu)
    {
        if (!menu) { _menuLmbWasDown = false; return; }
        var down = GameHost.IsMouseButtonDown(0x01);
        var rising = down && !_menuLmbWasDown;
        _menuLmbWasDown = down;
        if (!rising) return;
        if ((DateTime.UtcNow - _lastMenuClickUtc).TotalMilliseconds < 150) return;
        if (!GameHost.GetCursorPos(out var pt)) return;
        var c = ScreenToClientPoint(pt);
        var hit = HitTestWidgetRect(c);
        if (hit is null || !hit.Value.Action.StartsWith("ins:", StringComparison.Ordinal)) return;
        _lastMenuClickUtc = DateTime.UtcNow;
        OnInsMenuClick(hit.Value.Action, hit.Value.Rect, c.X);
    }

    /// <summary>Convert a screen-space cursor point to the overlay window's client coords.</summary>
    private (int X, int Y) ScreenToClientPoint(GameHost.Point screen)
    {
        var p = screen;
        GameHost.ScreenToClient(_window.Handle, ref p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Hit-test a client-space point against the renderer's navigation-menu rects. Returns the
    /// matched Action string (e.g. "menu-toggle", "corner:TopRight", "target:e:123") or null if the
    /// point is over no widget rect. LegendRowRects are in overlay client pixels (D2D renders at
    /// 96 DPI into a DIB sized to the game window's physical client rect, so 1 DIP == 1 device
    /// pixel == 1 client pixel), the same space ScreenToClient yields.
    /// </summary>
    private string? HitTestWidget((int X, int Y) p) => HitTestWidgetRect(p)?.Action;

    private (RawRectF Rect, string Action)? HitTestWidgetRect((int X, int Y) p)
    {
        // Later entries are drawn on top (the INSERT menu registers its controls after its panel rect),
        // so scan from the end to give the topmost control the click.
        var rects = _renderer.LegendRowRects;
        for (var i = rects.Count - 1; i >= 0; i--)
        {
            var (rect, action) = rects[i];
            if (p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom)
                return (rect, action);
        }
        return null;
    }

    /// <summary>
    /// WM_LBUTTONDOWN handler (wired to <see cref="OverlayWindow.OnClientClick"/>): dispatch the
    /// click on the navigation-menu widget. "menu-toggle" flips the dropdown; "corner:X" pins the
    /// widget to that screen corner (persisted); "target:&lt;id&gt;" toggles that nav target's selection;
    /// "mono-collapse" collapses/expands the nearby-monolith reward panel (persisted).
    /// Client coords arrive directly from the window, in the same space as LegendRowRects. Purely
    /// local UI — nothing is ever sent to the game.
    /// </summary>
    private void OnOverlayClick(int clientX, int clientY)
    {
        var hit = HitTestWidgetRect((clientX, clientY));
        if (hit is null) return;
        var (hitRect, action) = hit.Value;

        if (action.StartsWith("ins:", StringComparison.Ordinal))
        {
            if ((DateTime.UtcNow - _lastMenuClickUtc).TotalMilliseconds < 150) return; // poll path already took it
            _lastMenuClickUtc = DateTime.UtcNow;
            OnInsMenuClick(action, hitRect, clientX);
            return;
        }

        if (action == "menu-toggle")
        {
            _navMenuExpanded = !_navMenuExpanded;
        }
        else if (action.StartsWith("corner:", StringComparison.Ordinal))
        {
            _settings.NavMenuCorner = action.Substring("corner:".Length);
            _settings.Save();
        }
        else if (action.StartsWith("target:", StringComparison.Ordinal))
        {
            TogglePathTarget(action.Substring("target:".Length));
        }
        else if (action == "mono-collapse")
        {
            _settings.Monoliths.PanelCollapsed = !_settings.Monoliths.PanelCollapsed;
            _settings.Save();   // persist so the panel stays as the user left it across restarts
        }
        else if (action == "exchange-collapse")
        {
            // X on the card collapses it to a small "expand" tab pinned to the exchange window; clicking the
            // tab expands it again. Persisted so it stays as the user left it.
            _settings.CurrencyExchange.Collapsed = !_settings.CurrencyExchange.Collapsed;
            _settings.Save();
        }
    }

    /// <summary>INSERT-menu click dispatch (see OverlayRenderer.InsMenu for the action grammar).</summary>
    private void OnInsMenuClick(string action, RawRectF rect, int clientX)
    {
        switch (action)
        {
            case "ins:panel": return;
            case "ins:close": _insMenuOpen = false; return;
            case "ins:toggle:bot": ToggleBot(); return;
            case "ins:toggle:clear": ToggleMapClear(); return;
            case "ins:toggle:combat": ToggleCombatAssist(); return;
            case "ins:toggle:move": TogglePathMove(); return;
            case "ins:toggle:flask": ToggleAutoFlask(); return;
            case "ins:skill:add": AddCombatSkill(); return;
        }
        var parts = action.Split(':');
        if (parts.Length < 3) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        switch (parts[1])
        {
            case "tab" when int.TryParse(parts[2], out var tab):
                _insMenuTab = Math.Clamp(tab, 0, InsMenuData.TabCount - 1);
                return;
            case "adj" when parts.Length == 4 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out var delta):
                if (InsSliderSpec.All.TryGetValue(parts[2], out var spec)) SetSetting(spec, spec.Clamp(GetSetting(parts[2]) + delta));
                return;
            case "slider" when InsSliderSpec.All.TryGetValue(parts[2], out var sspec):
            {
                var t = rect.Width > 0f ? Math.Clamp((clientX - rect.Left) / rect.Width, 0f, 1f) : 0f;
                SetSetting(sspec, sspec.Clamp(sspec.Min + t * (sspec.Max - sspec.Min)));
                return;
            }
            case "set" when parts.Length == 4:
                SetChoice(parts[2], parts[3]);
                return;
            case "flag":
                if (parts[2] == "moveRunEnabled") _settings.MoveRunEnabled = !_settings.MoveRunEnabled;
                else if (parts[2] == "moveDiagonals") _settings.MoveDiagonals = !_settings.MoveDiagonals;
                else if (parts[2] == "playInBackground") _settings.PlayInBackground = !_settings.PlayInBackground;
                else if (parts[2] == "autoRespawn") _settings.AutoRespawn = !_settings.AutoRespawn;
                else if (parts[2] == "eventEssence") _settings.EventEssence = !_settings.EventEssence;
                else if (parts[2] == "eventStrongbox") _settings.EventStrongbox = !_settings.EventStrongbox;
                else if (parts[2] == "eventShrine") _settings.EventShrine = !_settings.EventShrine;
                else if (parts[2] == "eventBreach") _settings.EventBreach = !_settings.EventBreach;
                else if (parts[2] == "eventRitual") _settings.EventRitual = !_settings.EventRitual;
                else if (parts[2] == "eventChests") _settings.EventChests = !_settings.EventChests;
                else if (parts[2] == "eventClickStalled") _settings.EventClickStalled = !_settings.EventClickStalled;
                else if (parts[2] == "bossReengage") _settings.BossReengage = !_settings.BossReengage;
                else if (parts[2] == "moveRunStopNearHostiles") _settings.MoveRunStopNearHostiles = !_settings.MoveRunStopNearHostiles;
                else return;
                _settings.Save();
                return;
            case "skill":
                OnSkillAction(parts);
                return;
        }
    }

    private float GetSetting(string key) => key switch
    {
        "combatRange" => _settings.CombatRange,
        "combatEngageRange" => _settings.CombatEngageRange,
        "combatKeepDistance" => _settings.CombatKeepDistance,
        "combatFleeHpPct" => _settings.CombatFleeHpPct,
        "combatFleeRecoverPct" => _settings.CombatFleeRecoverPct,
        "combatFleeDistance" => _settings.CombatFleeDistance,
        "combatStallMs" => _settings.CombatStallMs,
        "mapClearStampRadius" => _settings.MapClearStampRadius,
        "mapClearAggroRange" => _settings.MapClearAggroRange,
        "mapClearStuckMs" => _settings.MapClearStuckMs,
        "eventRange" => _settings.EventRange,
        "eventUseRadius" => _settings.EventUseRadius,
        "moveArriveRadius" => _settings.MoveArriveRadius,
        "moveCooldownMs" => _settings.MoveCooldownMs,
        "moveLookAhead" => _settings.MoveLookAhead,
        "moveAxisRotationDeg" => _settings.MoveAxisRotationDeg,
        "lifeThresholdPct" => _settings.LifeThresholdPct,
        "manaThresholdPct" => _settings.ManaThresholdPct,
        "rollPressMs" => _settings.RollPressMs,
        "combatDodgeRecoverMs" => _settings.CombatDodgeRecoverMs,
        "combatDodgeDelayMs" => _settings.CombatDodgeDelayMs,
        "moveRollIntervalMs" => _settings.MoveRollIntervalMs,
        "moveRollMinCells" => _settings.MoveRollMinCells,
        "moveWalkSpeed" => _settings.MoveWalkSpeed,
        "moveRunSpeed" => _settings.MoveRunSpeed,
        "moveCoastMs" => _settings.MoveCoastMs,
        "moveArriveRadiusMob" => _settings.MoveArriveRadiusMob,
        "moveArriveRadiusEvent" => _settings.MoveArriveRadiusEvent,
        "combatComboSkipHpPct" => _settings.CombatComboSkipHpPct,
        "bossFleeHpPct" => _settings.BossFleeHpPct,
        "bossKeepDistance" => _settings.BossKeepDistance,
        "bossDodgeIntervalMs" => _settings.BossDodgeIntervalMs,
        "bossDodgeSpikePct" => _settings.BossDodgeSpikePct,
        _ => 0f,
    };

    /// <summary>Write a slider tunable (already clamped to its spec) and persist.</summary>
    private void SetSetting(InsSliderSpec spec, float v)
    {
        var s = _settings;
        switch (spec.Key)
        {
            case "combatRange": s.CombatRange = v; break;
            case "combatEngageRange": s.CombatEngageRange = v; break;
            case "combatKeepDistance": s.CombatKeepDistance = v; break;
            case "combatFleeHpPct": s.CombatFleeHpPct = v; break;
            case "combatFleeRecoverPct": s.CombatFleeRecoverPct = v; break;
            case "combatFleeDistance": s.CombatFleeDistance = v; break;
            case "combatStallMs": s.CombatStallMs = (int)v; break;
            case "mapClearStampRadius": s.MapClearStampRadius = (int)v; break;
            case "mapClearAggroRange": s.MapClearAggroRange = v; break;
            case "mapClearStuckMs": s.MapClearStuckMs = (int)v; break;
            case "eventRange": s.EventRange = v; break;
            case "eventUseRadius": s.EventUseRadius = v; break;
            case "moveArriveRadius": s.MoveArriveRadius = v; break;
            case "moveCooldownMs": s.MoveCooldownMs = (int)v; break;
            case "moveLookAhead": s.MoveLookAhead = v; break;
            case "moveAxisRotationDeg": s.MoveAxisRotationDeg = v; break;
            case "lifeThresholdPct": s.LifeThresholdPct = v; break;
            case "manaThresholdPct": s.ManaThresholdPct = v; break;
            case "rollPressMs": s.RollPressMs = (int)v; break;
            case "combatDodgeRecoverMs": s.CombatDodgeRecoverMs = (int)v; break;
            case "combatDodgeDelayMs": s.CombatDodgeDelayMs = (int)v; break;
            case "moveRollIntervalMs": s.MoveRollIntervalMs = (int)v; break;
            case "moveRollMinCells": s.MoveRollMinCells = v; break;
            case "moveWalkSpeed": s.MoveWalkSpeed = v; break;
            case "moveRunSpeed": s.MoveRunSpeed = v; break;
            case "moveCoastMs": s.MoveCoastMs = (int)v; break;
            case "moveArriveRadiusMob": s.MoveArriveRadiusMob = v; break;
            case "moveArriveRadiusEvent": s.MoveArriveRadiusEvent = v; break;
            case "combatComboSkipHpPct": s.CombatComboSkipHpPct = v; break;
            case "bossFleeHpPct": s.BossFleeHpPct = v; break;
            case "bossKeepDistance": s.BossKeepDistance = v; break;
            case "bossDodgeIntervalMs": s.BossDodgeIntervalMs = (int)v; break;
            case "bossDodgeSpikePct": s.BossDodgeSpikePct = v; break;
            default: return;
        }
        s.Save();
    }

    private void SetChoice(string key, string value)
    {
        switch (key)
        {
            case "combatTargetMode" when value is "Nearest" or "Rarity" or "LowestHp" or "HighestHp": _settings.CombatTargetMode = value; break;
            case "combatRotationMode" when value is "RoundRobin" or "Priority": _settings.CombatRotationMode = value; break;
            case "moveMethod" when value is "WASD" or "Click": _settings.MoveMethod = value; break;
            default: return;
        }
        _settings.Save();
    }

    // Keys the in-game skill editor cycles through: Q W E R T, 1-5, then mouse buttons.
    private static readonly int[] SkillKeyCycle = { 0x51, 0x57, 0x45, 0x52, 0x54, 0x31, 0x32, 0x33, 0x34, 0x35, 0x01, 0x02, 0x04, 0x05, 0x06 };

    private void AddCombatSkill()
    {
        _settings.CombatSkills ??= new List<CombatSkill>();
        if (_settings.CombatSkills.Count >= 8) return;
        var used = new HashSet<int>(_settings.CombatSkills.Select(k => k.Key));
        var key = SkillKeyCycle.FirstOrDefault(k => !used.Contains(k), 0x51);
        _settings.CombatSkills.Add(new CombatSkill { Key = key, CooldownMs = Math.Clamp(_settings.CombatCooldownMs, 0, 60000) });
        _settings.Save();
    }

    private void OnSkillAction(string[] parts)
    {
        var list = _settings.CombatSkills;
        if (list is null || parts.Length < 4 || !int.TryParse(parts[3], out var i) || (uint)i >= (uint)list.Count) return;
        var sk = list[i];
        switch (parts[2])
        {
            case "del":
                list.RemoveAt(i);
                break;
            case "flip" when parts.Length == 5:
                if (parts[4] == "enabled") sk.Enabled = !sk.Enabled;
                else if (parts[4] == "rareOnly") sk.RareOnly = !sk.RareOnly;
                else if (parts[4] == "dodgeAfter") sk.DodgeAfter = !sk.DodgeAfter;
                else return;
                break;
            case "adj" when parts.Length == 6 && float.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d):
                switch (parts[4])
                {
                    case "key":
                    {
                        var idx = Array.IndexOf(SkillKeyCycle, sk.Key);
                        var n = SkillKeyCycle.Length;
                        idx = ((idx < 0 ? 0 : idx) + (d > 0 ? 1 : -1) + n) % n;
                        sk.Key = SkillKeyCycle[idx];
                        break;
                    }
                    case "cd": sk.CooldownMs = Math.Clamp(sk.CooldownMs + (int)d, 0, 60000); break;
                    case "range": sk.Range = Math.Clamp(sk.Range + d, 0f, 200f); break;
                    case "min": sk.MinTargets = Math.Clamp(sk.MinTargets + (int)d, 1, 20); break;
                    case "hp": sk.HpBelowPct = Math.Clamp(sk.HpBelowPct + d, 0f, 100f); break;
                    case "repeat": sk.Repeat = Math.Clamp(sk.Repeat + (int)d, 1, 10); break;
                    case "gap": sk.RepeatGapMs = Math.Clamp(sk.RepeatGapMs + (int)d, 30, 2000); break;
                    case "hold": sk.HoldMs = Math.Clamp(sk.HoldMs + (int)d, 0, 10000); break;
                    case "next": sk.NextDelayMs = Math.Clamp(sk.NextDelayMs + (int)d, 0, 10000); break;
                    default: return;
                }
                break;
            default: return;
        }
        _settings.Save();
    }

    /// <summary>Poll overlay hotkeys: F8 auto-flask toggle, F4 combat, F3 bot master (quest follow), F2 map clear, F5 path move, F9 quit, F12 dashboard, F6/F7 path targets.
    /// Map calibration is web-config-only (no in-game keys, to avoid accidental presses).</summary>
    private void HandleHotkeys()
    {
        // INSERT opens/closes the in-game menu (debounced). Only while PoE2 is foreground.
        if (Down(0x2D) && DateTime.UtcNow >= _nextInsToggleAt
            && _gameHwnd != 0 && GameHost.GetForegroundWindow() == _gameHwnd)
        {
            _nextInsToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            _insMenuOpen = !_insMenuOpen;
        }
        // F8 master kill-switch for auto-flask (debounced).
        if (Down(0x77) && DateTime.UtcNow >= _nextToggleAt)
        {
            _nextToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            ToggleAutoFlask();
        }
        // F4 master kill-switch for combat assist (debounced). Not writable via the dashboard.
        if (Down(0x73) && DateTime.UtcNow >= _nextCombatToggleAt)
        {
            _nextCombatToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            ToggleCombatAssist();
        }
        // F3 bot master (debounced). Arms quest follow + path move + combat. Not writable via the dashboard.
        // F4 combat stays independently toggleable (OR'd with the bot). F2 map-clear pauses quest follow.
        // F8 flask stays independent.
        if (Down(QuestFollowVk) && DateTime.UtcNow >= _nextQuestToggleAt)
        {
            _nextQuestToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            ToggleBot();
        }
        // F2 map-clear (debounced). Walks unexplored cells; unique bosses + hostiles first.
        // Arms path move + combat. Pauses F3 quest follow while on. Not writable via the dashboard.
        if (Down(MapClearVk) && DateTime.UtcNow >= _nextMapClearToggleAt)
        {
            _nextMapClearToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            ToggleMapClear();
        }
        // F5 master kill-switch for path move (debounced). Not writable via the dashboard.
        if (Down(PathMoveVk) && DateTime.UtcNow >= _nextMoveToggleAt)
        {
            _nextMoveToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            TogglePathMove();
        }
        // F9 quits the overlay (besides the tray-icon Exit).
        if (Down(0x78)) { Console.WriteLine("\nF9 — exiting."); RequestShutdown(); }

        // F12 opens the web dashboard in the default browser — only while PoE2 is the foreground
        // window (debounced). Purely launches a browser; sends nothing to the game.
        if (Down(0x7B) && DateTime.UtcNow >= _nextBrowserAt
            && _gameHwnd != 0 && GameHost.GetForegroundWindow() == _gameHwnd)
        {
            _nextBrowserAt = DateTime.UtcNow.AddMilliseconds(800);
            OpenDashboard();
        }

        // F6: while quest follow is on, cycle a SINGLE pinned quest target (last press wins).
        // Otherwise add the nearest not-yet-selected landmark. F7 clears selection + pin.
        if (DateTime.UtcNow >= _nextPathKeyAt)
        {
            if (Down(AddNearestVk))
            {
                AddNearestPathTarget();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else if (Down(ClearPathsVk))
            {
                ClearPathTargets();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
        }

        // Atlas tile inspector: F10 = dump the tile under the cursor (map/content/biome/flags) as an
        // on-atlas tooltip so you can see what to set as a web-UI filter.
        if (Down(0x79) && DateTime.UtcNow >= _nextInspectAt) // F10
        {
            _nextInspectAt = DateTime.UtcNow.AddMilliseconds(250);
            AtlasRoutePick();
        }
    }

    /// <summary>Open the web dashboard in the user's default browser (F12). Launches a browser only —
    /// nothing is sent to the game.</summary>
    private void OpenDashboard()
    {
        var url = $"http://localhost:{_settings.ApiPort}/";
        try
        {
            Console.WriteLine($"F12 — opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Console.Error.WriteLine($"Open dashboard failed: {ex.Message}"); }
    }

    private static bool Down(int vk) => GameHost.IsKeyDown(vk);

    // ── Arm-bit toggles shared by the F-key hotkeys and the INSERT menu (never the HTTP API). ──
    private void ToggleAutoFlask()
    {
        _autoFlask = !_autoFlask;
        _settings.AutoFlaskEnabled = _autoFlask;   // persist so the choice survives a restart
        _settings.Save();
        Console.WriteLine($"\nAuto-flask: {(_autoFlask ? "ON" : "OFF")}");
    }

    private void ToggleCombatAssist()
    {
        _combatAssist = !_combatAssist;
        _settings.CombatAssistEnabled = _combatAssist;
        _settings.Save();
        Console.WriteLine($"\nCombat assist: {(_combatAssist ? "ON" : "OFF")}");
    }

    private void ToggleBot()
    {
        _botEnabled = !_botEnabled;
        _questFollow = _botEnabled && !_mapClear;
        _settings.BotEnabled = _botEnabled;
        _settings.QuestFollowEnabled = _botEnabled;
        _settings.Save();
        _botNote = _botEnabled ? "armed" : "OFF (F3)";
        _questFollowNote = _questFollow ? "armed" : (_botEnabled ? "paused (clear)" : "OFF (F3)");
        if (_questFollow) PinLastSelection();
        else
        {
            _questPinId = null;
            if (!_mapClear) { _questFollowId = null; _questFollowHasGrid = false; }
        }
        _moveNote = MoveArmed ? "armed" : "OFF (F5)";
        Console.WriteLine($"\nBot (quest follow): {(_botEnabled ? "ON" : "OFF")}");
    }

    private void ToggleMapClear()
    {
        _mapClear = !_mapClear;
        _questFollow = _botEnabled && !_mapClear;
        _settings.MapClearEnabled = _mapClear;
        _settings.Save();
        _mapClearNote = _mapClear ? "armed" : "OFF (F2)";
        _questFollowNote = _questFollow ? "armed" : (_botEnabled ? "paused (clear)" : "OFF (F3)");
        _questFollowId = null;
        _questFollowHasGrid = false;
        if (_questFollow) PinLastSelection();
        else _questPinId = null;
        _moveNote = MoveArmed ? "armed" : "OFF (F5)";
        Console.WriteLine($"\nMap clear: {(_mapClear ? "ON" : "OFF")}");
    }

    private void TogglePathMove()
    {
        _moveEnabled = !_moveEnabled;
        if (!MoveArmed) ReleaseHeldKeys();
        _settings.MoveEnabled = _moveEnabled;
        _settings.Save();
        _moveNote = MoveArmed ? "armed" : "OFF (F5)";
        Console.WriteLine($"\nPath move: {(_moveEnabled ? "ON" : "OFF")}");
    }
}
