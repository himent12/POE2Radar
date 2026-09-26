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

    // ── Draw-only path guidance. ──
    // Unified navigation targets: a single list built each world tick from BOTH terrain-tile
    // landmarks AND entity POIs (bosses, expedition, waypoints…), each addressed by a STABLE STRING
    // id ("t:<path>" / "e:<entityId>"). Multi-select: each selected target draws its OWN full A*
    // route in its OWN color (by selection-order slot). F6 adds the nearest not-yet-selected target;
    // F7 clears the whole selection; clicking a legend row toggles that target. Selection is capped
    // at the palette size so colors stay distinct (and per-tick planning stays bounded). On a zone
    // change the selection is cleared, then the persistent auto-nav patterns re-select matching
    // targets in the new zone.
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

        if (action.StartsWith("pc:", StringComparison.Ordinal))
        {
            OnPriceCheckClick(action);
            return;
        }

        if (action.StartsWith("trade:", StringComparison.Ordinal))
        {
            OnTradeClick(action);
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

    /// <summary>INSERT-menu click dispatch (see OverlayRenderer.InsMenu for the action grammar). Every write
    /// persists immediately. Arm bits (flask / macros) are only toggled here or by their hotkeys — never HTTP.</summary>
    private void OnInsMenuClick(string action, RawRectF rect, int clientX)
    {
        switch (action)
        {
            case "ins:panel": return;
            case "ins:close": _insMenuOpen = false; return;
            case "ins:toggle:flask": ToggleAutoFlask(); return;
            case "ins:toggle:buffs": ToggleBuffKeeper(); return;
            case "ins:open:dashboard": OpenDashboard(); return;
        }
        var parts = action.Split(':');
        if (parts.Length < 3) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        switch (parts[1])
        {
            case "tab" when int.TryParse(parts[2], out var tab):
                _insMenuTab = Math.Clamp(tab, 0, OverlayRenderer.InsTabCount - 1);
                return;
            case "adj" when parts.Length == 4 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out var delta):
                if (InsSliderSpec.All.TryGetValue(parts[2], out var spec)) SetSlider(spec, spec.Get(_settings) + delta);
                return;
            case "slider" when InsSliderSpec.All.TryGetValue(parts[2], out var sspec):
            {
                var t = rect.Width > 0f ? Math.Clamp((clientX - rect.Left) / rect.Width, 0f, 1f) : 0f;
                SetSlider(sspec, sspec.Min + t * (sspec.Max - sspec.Min));
                return;
            }
            case "set" when parts.Length == 4:
                SetChoice(parts[2], parts[3]);
                return;
            case "key" when parts.Length == 4 && int.TryParse(parts[3], out var dir):
                CycleKey(parts[2], dir);
                return;
            case "flag":
                if (!FlipFlag(parts[2])) return;
                _settings.Save();
                return;
            case "buff":
                OnBuffRuleAction(parts, action);
                return;
            case "cmd" when parts.Length == 4 && parts[2] == "flip" && int.TryParse(parts[3], out var ci2)
                            && (uint)ci2 < (uint)_settings.Commands.Commands.Count:
                _settings.Commands.Commands[ci2].Enabled = !_settings.Commands.Commands[ci2].Enabled;
                _settings.Save();
                return;
        }
    }

    /// <summary>INSERT-menu edits to buff-keeper rules: <c>ins:buff:flip|trig:i</c>, <c>ins:buff:key:i:±1</c>, and
    /// <c>ins:buff:add:&lt;name&gt;</c> (click a live buff → a "keep it up" rule for it). Names/timers are
    /// dashboard-only (no text entry in the overlay).</summary>
    private void OnBuffRuleAction(string[] parts, string action)
    {
        var rules = _settings.BuffKeeper.Rules;
        if (parts[2] == "add")
        {
            var name = action["ins:buff:add:".Length..];
            if (name.Length == 0 || rules.Count >= 16) return;
            if (rules.Any(r => string.Equals(r.BuffName, name, StringComparison.OrdinalIgnoreCase))) return;
            // Replace the untouched shipped example instead of stacking beside it.
            rules.RemoveAll(r => !r.Enabled && r.BuffName.Length == 0 && r.Name == "Example buff");
            rules.Add(new BuffRule { Enabled = false, Name = name, BuffName = name, Key = 0x54, Trigger = BuffKeeper.TriggerMissing });
            _settings.Save();
            return;
        }
        if (parts.Length < 4 || !int.TryParse(parts[3], out var i) || (uint)i >= (uint)rules.Count) return;
        var rule = rules[i];
        switch (parts[2])
        {
            case "flip":
                rule.Enabled = !rule.Enabled;
                // Ticking a rule in the menu is a clear "I want this running": arm the keeper if it was off.
                if (rule.Enabled && !_buffKeeperArmed) ToggleBuffKeeper();
                break;
            case "trig":
                rule.Trigger = rule.Trigger switch
                {
                    BuffKeeper.TriggerMissing => BuffKeeper.TriggerExpiring,
                    BuffKeeper.TriggerExpiring => BuffKeeper.TriggerInterval,
                    _ => BuffKeeper.TriggerMissing,
                };
                break;
            case "key" when parts.Length == 5 && int.TryParse(parts[4], out var dir):
                var k = Array.IndexOf(SkillKeyCycle, rule.Key);
                var n = SkillKeyCycle.Length;
                rule.Key = SkillKeyCycle[((k < 0 ? 0 : k) + (dir >= 0 ? 1 : -1) + n) % n];
                break;
            default: return;
        }
        _settings.Save();
    }

    // Keys a buff rule's in-game picker cycles through: skill keys QWERT, 1-5, then mouse buttons.
    private static readonly int[] SkillKeyCycle = { 0x51, 0x57, 0x45, 0x52, 0x54, 0x31, 0x32, 0x33, 0x34, 0x35, 0x02, 0x04, 0x05, 0x06 };

    private void SetSlider(InsSliderSpec spec, float value)
    {
        spec.Set(_settings, spec.Clamp(value));
        _settings.Save();
    }

    private bool FlipFlag(string key)
    {
        var s = _settings;
        switch (key)
        {
            case "showMonsters": s.ShowMonsters = !s.ShowMonsters; return true;
            case "showTerrain": s.ShowTerrain = !s.ShowTerrain; return true;
            case "showPlayerBlip": s.ShowPlayerBlip = !s.ShowPlayerBlip; return true;
            case "showPath": s.ShowPath = !s.ShowPath; return true;
            case "hpBarMagic": s.HpBarMagic = !s.HpBarMagic; return true;
            case "hpBarRare": s.HpBarRare = !s.HpBarRare; return true;
            case "hpBarUnique": s.HpBarUnique = !s.HpBarUnique; return true;
            case "hpBarNormal": s.HpBarNormal = !s.HpBarNormal; return true;
            case "groundItems": s.GroundItems.Enabled = !s.GroundItems.Enabled; return true;
            case "hoverPrice": s.HoverPrice.Enabled = !s.HoverPrice.Enabled; return true;
            case "alwaysShowOverlay": s.AlwaysShowOverlay = !s.AlwaysShowOverlay; return true;
            case "reduceMotion": s.ReduceMotion = !s.ReduceMotion; return true;
            case "tradeEnabled": s.Trade.Enabled = !s.Trade.Enabled; return true;
            case "tradeShowPanel": s.Trade.ShowPanel = !s.Trade.ShowPanel; return true;
            case "tradeTrackHistory": s.Trade.TrackHistory = !s.Trade.TrackHistory; return true;
            case "tradeKickAfter": s.Trade.KickAfterTrade = !s.Trade.KickAfterTrade; return true;
            default: return false;
        }
    }

    private void SetChoice(string key, string value)
    {
        switch (key)
        {
            case "lifeFlaskMode" when value is "Health" or "EnergyShield" or "Either": _settings.LifeFlaskMode = value; break;
            default: return;
        }
        _settings.Save();
    }

    // Keys the in-game key pickers cycle through: flask row 1-5, then QWERT, then mouse side buttons.
    internal static readonly int[] KeyCycle = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x51, 0x57, 0x45, 0x52, 0x54, 0x05, 0x06 };

    private void CycleKey(string key, int dir)
    {
        static int Next(int vk, int d)
        {
            var i = Array.IndexOf(KeyCycle, vk);
            var n = KeyCycle.Length;
            return KeyCycle[((i < 0 ? 0 : i) + (d >= 0 ? 1 : -1) + n) % n];
        }
        switch (key)
        {
            case "lifeKey": _settings.LifeKey = Next(_settings.LifeKey, dir); break;
            case "manaKey": _settings.ManaKey = Next(_settings.ManaKey, dir); break;
            default: return;
        }
        _settings.Save();
    }

    /// <summary>Poll overlay hotkeys: Insert menu, F8 auto-flask toggle, F9 quit, F12 dashboard, F6/F7 path targets, F10 atlas.
    /// Map calibration is web-config-only (no in-game keys, to avoid accidental presses).</summary>
    private void HandleHotkeys()
    {
        // INSERT opens/closes the in-game menu (debounced). Only while PoE2 is foreground.
        if (Down(0x2D) && DateTime.UtcNow >= _nextInsToggleAt
            && GameFocused())
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
        // Esc closes the price-check panel (the game closes its inventory on the same key — both go away).
        if (_pcView is not null && Down(0x1B) && GameFocused()) _pcClose = true;
        // F9 quits the overlay (besides the tray-icon Exit).
        if (Down(0x78)) { Console.WriteLine("\nF9 — exiting."); RequestShutdown(); }

        // F12 opens the web dashboard in the default browser — only while PoE2 is the foreground
        // window (debounced). Purely launches a browser; sends nothing to the game.
        if (Down(0x7B) && DateTime.UtcNow >= _nextBrowserAt
            && GameFocused())
        {
            _nextBrowserAt = DateTime.UtcNow.AddMilliseconds(800);
            OpenDashboard();
        }

        // F6 adds the nearest not-yet-selected landmark; F7 clears the selection.
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

    /// <summary>Render-thread key check through the per-frame memo (see <see cref="IsDownMemo"/>).</summary>
    private bool Down(int vk) => IsDownMemo(vk);
}
