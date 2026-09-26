using POE2Radar.Core.Game;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Input;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // ── Buff keeper (opt-in input): recast a self-buff when it expires / goes missing. Render thread;
    //    armed only by its hotkey or the INSERT menu (never HTTP). Same gates as auto-flask. ──
    private bool _buffKeeperArmed;

    private DateTime[] _buffFiredAt = Array.Empty<DateTime>();

    private DateTime _buffAnyFiredAt;

    private readonly List<float> _hostileDistances = new(64);

    // Published for the dashboard/menu (render thread writes, HTTP thread reads): the live buff list (so the
    // user can pick exact names) + one note per rule.
    private volatile IReadOnlyList<Poe2Live.BuffInfo>? _liveBuffs;

    private volatile IReadOnlyList<string> _buffRuleNotes = Array.Empty<string>();

    private string _buffNote = "";

    private DateTime _nextBuffNotesAt;

    // ── Chat macros / bookmarks / inspect links. The sender types on its own worker thread. ──
    private readonly ChatSender _chat;

    private readonly HotkeyWatcher _hotkeys = new();

    // Per-frame key-state memo: every hotkey check asks for its key + three modifiers, and on Linux each
    // GameHost.IsKeyDown is an X server round trip — so each vk is queried at most once per frame.
    private readonly Dictionary<int, bool> _keyMemo = new();

    private readonly Func<int, bool> _isDownMemo;

    // Parsed hotkey strings (settings hold strings; parse once per distinct string, not per frame).
    private readonly Dictionary<string, Hotkey> _hotkeyCache = new(StringComparer.Ordinal);

    private bool IsDownMemo(int vk)
    {
        if (_keyMemo.TryGetValue(vk, out var down)) return down;
        down = GameHost.IsKeyDown(vk);
        _keyMemo[vk] = down;
        return down;
    }

    private Hotkey Hk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return default;
        if (!_hotkeyCache.TryGetValue(text, out var hk))
        {
            if (_hotkeyCache.Count > 256) _hotkeyCache.Clear();
            _hotkeyCache[text] = hk = Hotkey.ParseOrNone(text);
        }
        return hk;
    }

    // Render thread writes; the chat worker reads it inside canSend.
    private volatile bool _inGameNow;

    // Last hovered item (world thread writes, render thread reads for the inspect hotkeys).
    private sealed record HoverRef(string Name, DateTime At);

    private volatile HoverRef? _lastHover;

    private void TickBuffKeeper(bool inGame, nint localPlayer, bool focused, IReadOnlyList<Poe2Live.EntityDot> entities,
        NumVec2 player, string areaCode)
    {
        var cfg = _settings.BuffKeeper;
        var rules = cfg.Rules;
        if (_buffFiredAt.Length != rules.Count) _buffFiredAt = new DateTime[rules.Count];

        // Read buffs whenever in game (cached ~100 ms inside Poe2Live) so the dashboard's "current buffs"
        // list works even before a rule is armed.
        var buffs = inGame ? _liveRender.PlayerBuffs(localPlayer) : null;
        _liveBuffs = buffs;

        _hostileDistances.Clear();
        foreach (var e in entities)
            if (e.Category == Poe2Live.EntityCategory.Monster && !e.IsFriendly && e.HpCur > 0)
                _hostileDistances.Add(NumVec2.Distance(e.Grid, player));

        var now = DateTime.UtcNow;
        var snap = new BuffKeeper.Snapshot(
            NowUtc: now,
            Armed: _buffKeeperArmed,
            // Never press while our own chat macro is typing (it owns the keyboard for a moment).
            Focused: focused && !_chat.IsSending,
            InGame: inGame,
            Alive: _playerAlive,
            InTown: IsTownOrHideout(areaCode),
            HostileDistances: _hostileDistances,
            Buffs: buffs,
            Rules: rules,
            LastFiredUtc: _buffFiredAt,
            LastAnyFiredUtc: _buffAnyFiredAt,
            GlobalGapMs: cfg.GlobalGapMs);

        if (_insMenuOpen && _buffKeeperArmed)
            _buffNote = "paused while this menu is open";
        else if (BuffKeeper.Decide(snap) is { } d)
        {
            GameHost.TapKey((ushort)d.Key);
            _buffFiredAt[d.RuleIndex] = now;
            _buffAnyFiredAt = now;
            _buffNote = d.Note;
        }
        else if (!_buffKeeperArmed) _buffNote = $"OFF ({cfg.ToggleHotkey})";
        else if (!rules.Any(r => r.Enabled)) _buffNote = "armed · no rules enabled";
        else if (!focused) _buffNote = "waiting — PoE2 isn't the active window";
        else if (_buffNote.StartsWith("OFF", StringComparison.Ordinal) || _buffNote.StartsWith("paused", StringComparison.Ordinal)
                 || _buffNote.StartsWith("waiting", StringComparison.Ordinal) || _buffNote.Length == 0) _buffNote = "armed";

        // Per-rule notes are UI-only — refresh a few times a second, not every frame.
        if (now >= _nextBuffNotesAt)
        {
            _nextBuffNotesAt = now.AddMilliseconds(250);
            _buffRuleNotes = BuffKeeper.RuleNotes(snap);
        }
    }

    /// <summary>API (/api/buffs): the buffs on the character right now (so rules can use exact names), the
    /// keeper's arm state, and one live note per rule.</summary>
    private object BuffsJson()
    {
        var buffs = _liveBuffs;
        return new
        {
            readable = buffs is not null,
            armed = _buffKeeperArmed,
            note = _buffNote,
            buffs = (buffs ?? Array.Empty<Poe2Live.BuffInfo>()).Select(b => new
            {
                name = b.Name, timeLeft = b.IsInfinite ? (float?)null : b.TimeLeft,
                total = b.IsInfinite ? (float?)null : b.TotalTime, charges = b.Charges,
            }),
            notes = _buffRuleNotes,
            chat = _chat.LastResult,
        };
    }

    private static bool IsTownOrHideout(string areaCode)
        => areaCode.Length > 0 && (areaCode.Contains("Hideout", StringComparison.OrdinalIgnoreCase)
            || ZoneGuide.Shared.Area(areaCode)?.Town == true
            || areaCode.Contains("town", StringComparison.OrdinalIgnoreCase));

    private void ToggleBuffKeeper()
    {
        _buffKeeperArmed = !_buffKeeperArmed;
        _settings.BuffKeeper.Enabled = _buffKeeperArmed;
        _settings.Save();
        _buffNote = _buffKeeperArmed ? "armed" : $"OFF ({_settings.BuffKeeper.ToggleHotkey})";
        var rules = _settings.BuffKeeper.Rules.Count(r => r.Enabled && r.Key != 0);
        ShowToast(_buffKeeperArmed
            ? $"Buff keeper ON — watching {rules} rule{(rules == 1 ? "" : "s")}" + (rules == 0 ? " (tick one in Insert → Macros)" : "")
            : $"Buff keeper OFF — press {_settings.BuffKeeper.ToggleHotkey} to turn it back on");
    }

    /// <summary>User-configurable hotkeys (render thread, only while PoE2 is foreground): buff-keeper arm,
    /// chat commands, bookmarks, item inspect. Skipped while our own chat line is being typed — on Linux the
    /// typed characters are real key presses and could re-trigger a letter-bound macro.</summary>
    private void HandleMacroHotkeys()
    {
        var foreground = GameFocused();
        if (!foreground || _chat.IsSending)
        {
            if (!foreground) _hotkeys.Reset();
            return;
        }

        var isDown = _isDownMemo;
        if (_hotkeys.Pressed(Hk(_settings.BuffKeeper.ToggleHotkey), isDown)) ToggleBuffKeeper();
        if (_hotkeys.Pressed(Hk(_settings.HoverPrice.PriceCheckHotkey), isDown)) _pcRequest = true;

        var cmd = _settings.Commands;
        if (!cmd.Enabled) return;
        foreach (var c in cmd.Commands)
            if (c.Enabled && _hotkeys.Pressed(Hk(c.Hotkey), isDown))
                RunChatCommand(c.Text);
        foreach (var b in cmd.Bookmarks)
            if (b.Enabled && _hotkeys.Pressed(Hk(b.Hotkey), isDown))
                OpenBookmark(b.Url);
        if (_hotkeys.Pressed(Hk(cmd.InspectWikiHotkey), isDown)) InspectHovered(ItemLinks.Wiki);
        if (_hotkeys.Pressed(Hk(cmd.InspectDbHotkey), isDown)) InspectHovered(ItemLinks.Poe2Db);
    }

    private PlaceholderContext Placeholders() => new(
        Character: _charName.Length > 0 ? _charName : null,
        LastWhisper: LastWhisperPartner(),
        League: _priceBook.League is { Length: > 0 } l ? l : null,
        Area: _world.AreaCode is { Length: > 0 } a ? ZoneGuide.Shared.FriendlyName(a) : null);

    /// <summary>Expand placeholders and queue the line(s) for the chat worker. Returns a status for the UI.</summary>
    private string RunChatCommand(string text)
    {
        if (!ChatCommands.TryExpand(text, Placeholders(), out var lines, out var error))
        {
            Console.WriteLine($"\nChat macro skipped: {error}");
            return error ?? "not sent";
        }
        if (!_chat.Enqueue(lines)) return "chat queue full";
        return "sent";
    }

    private void OpenBookmark(string url)
    {
        var ctx = new UrlContext(_priceBook.League, _lastHover?.Name, _charName);
        if (!Bookmarks.TryExpand(url, ctx, out var expanded, out var error)) { Console.WriteLine($"\nBookmark: {error}"); return; }
        if (!BrowserLauncher.Open(expanded, out var openError)) Console.Error.WriteLine($"Bookmark open failed: {openError}");
    }

    private void InspectHovered(Func<string?, string?> link)
    {
        if (_lastHover is not { } hover || DateTime.UtcNow - hover.At > TimeSpan.FromSeconds(1.5)) return;
        if (link(hover.Name) is not { } url) return;
        if (!BrowserLauncher.Open(url, out var error)) Console.Error.WriteLine($"Inspect open failed: {error}");
    }

    /// <summary>Chat worker gate (worker thread): PoE2 foreground AND in game, read from render-thread flags
    /// plus a live foreground check right before typing.</summary>
    private bool CanSendChat()
        => _inGameNow && GameFocused();
}
