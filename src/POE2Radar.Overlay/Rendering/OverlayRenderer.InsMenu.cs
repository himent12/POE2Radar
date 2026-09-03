using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// INSERT in-game menu — a clean settings panel: top tab bar, one column of labelled rows with the control
/// on the right, a single accent colour, no decoration. Every row is "label + one-line help | control".
///
/// Click grammar (<see cref="_legendRowRects"/> → <c>RadarApp.OnOverlayClick</c>):
/// <c>ins:panel</c> (swallow) · <c>ins:close</c> · <c>ins:tab:N</c> ·
/// <c>ins:toggle:bot|clear|combat|move|flask</c> · <c>ins:flag:&lt;setting&gt;</c> ·
/// <c>ins:slider:&lt;key&gt;</c> (value from click X) · <c>ins:adj:&lt;key&gt;:&lt;delta&gt;</c> ·
/// <c>ins:set:&lt;key&gt;:&lt;value&gt;</c> · <c>ins:skill:add</c> · <c>ins:skill:del:i</c> ·
/// <c>ins:skill:flip:i:&lt;field&gt;</c> · <c>ins:skill:adj:i:&lt;field&gt;:&lt;delta&gt;</c>.
/// </summary>
public sealed partial class OverlayRenderer
{
    // ── Palette: one accent, neutral greys ──
    private static readonly Color4 UBg     = new(0.075f, 0.078f, 0.085f, 0.97f);
    private static readonly Color4 UBg2    = new(1f, 1f, 1f, 0.035f);   // card / control fill
    private static readonly Color4 UBg3    = new(1f, 1f, 1f, 0.07f);    // hover-ish / button fill
    private static readonly Color4 ULine   = new(1f, 1f, 1f, 0.08f);
    private static readonly Color4 UText   = new(0.93f, 0.93f, 0.94f, 1f);
    private static readonly Color4 UMuted  = new(0.62f, 0.63f, 0.66f, 1f);
    private static readonly Color4 UDim    = new(0.42f, 0.43f, 0.46f, 1f);
    private static readonly Color4 UAccent = new(0.95f, 0.76f, 0.35f, 1f);  // amber
    private static readonly Color4 UAccentDim = new(0.95f, 0.76f, 0.35f, 0.25f);
    private static readonly Color4 UOn     = new(0.40f, 0.80f, 0.50f, 1f);
    private static readonly Color4 UOff    = new(0.30f, 0.31f, 0.34f, 1f);
    private static readonly Color4 URed    = new(0.90f, 0.36f, 0.34f, 1f);
    private static readonly Color4 UBlue   = new(0.40f, 0.62f, 0.95f, 1f);
    private static readonly Color4 UTeal   = new(0.45f, 0.82f, 0.86f, 1f);
    private static readonly Color4 UInk    = new(0.10f, 0.09f, 0.07f, 1f);

    private const float MW = 820f, MH = 640f, PadX = 24f, TopH = 56f, TabsH = 44f, FootH = 40f, Row = 44f, R = 5f;
    private static readonly string[] Tabs = { "Overview", "Combat", "Skills", "Clearing", "Movement", "Boss", "Roll" };
    static OverlayRenderer() => System.Diagnostics.Debug.Assert(Tabs.Length == InsMenuData.TabCount);

    private DrawTextFormat? _tfTitle, _tfBody, _tfBold, _tfSmall, _tfMono, _tfMonoSmall, _tfCaps;
    private DrawBrush? _bUi;

    private void EnsureInsResources()
    {
        if (_tfTitle is not null) return;
        _tfTitle     = _window.CreateUiTextFormat(17f, bold: true);
        _tfBody      = _window.CreateUiTextFormat(13f);
        _tfBold      = _window.CreateUiTextFormat(13f, bold: true);
        _tfSmall     = _window.CreateUiTextFormat(11f);
        _tfCaps      = _window.CreateUiTextFormat(10.5f, bold: true);
        _tfMono      = _window.CreateTextFormat("Consolas", 12.5f);
        _tfMonoSmall = _window.CreateTextFormat("Consolas", 11f);
        _bUi = _window.RenderTarget.CreateSolidColorBrush(UText);
    }

    // ── primitives ──
    private DrawBrush B(Color4 c) { _bUi!.Color = c; return _bUi; }
    private void T(DrawTarget rt, string s, DrawTextFormat tf, float x, float cy, Color4 c) => rt.DrawTextVCentered(s, tf, x, cy, B(c));
    private void TR(DrawTarget rt, string s, DrawTextFormat tf, float right, float cy, Color4 c) => T(rt, s, tf, right - rt.MeasureText(s, tf), cy, c);
    private void TC(DrawTarget rt, string s, DrawTextFormat tf, float cx, float cy, Color4 c) => T(rt, s, tf, cx - rt.MeasureText(s, tf) * 0.5f, cy, c);
    private void HLine(DrawTarget rt, float x1, float x2, float y, Color4 c) => rt.DrawLine(new NumVec2(x1, y), new NumVec2(x2, y), B(c), 1f);
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static Color4 A(Color4 c, float a) => new(c.R, c.G, c.B, a);

    /// <summary>Headless draw of just the INSERT menu onto the window surface (preview/tests).</summary>
    internal void RenderInsMenuPreview(RenderContext ctx, Color4 background)
    {
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(background);
        _legendRowRects.Clear();
        DrawInsMenu(rt, ctx);
        rt.EndDraw();
    }

    private void DrawInsMenu(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.InsMenu is not { } m) return;
        EnsureInsResources();

        var left = MathF.Round((ctx.WindowWidth - MW) * 0.5f);
        var top = MathF.Round((ctx.WindowHeight - MH) * 0.5f);
        var panel = new RawRectF(left, top, left + MW, top + MH);
        rt.DrawShadow(panel, R, 24f, 0.5f);
        rt.FillRoundedRectangle(panel, R, B(UBg));
        rt.DrawRoundedRectangle(panel, R, B(ULine), 1f);
        _legendRowRects.Add((panel, "ins:panel"));

        // ── Top bar: title · context · close ──
        var ty = top + TopH * 0.5f;
        T(rt, "POE2 Radar", _tfTitle!, left + PadX, ty - 1f, UText);
        var ctxText = string.IsNullOrEmpty(m.CharName) ? "" : $"{m.CharName} · Lv {ctx.CharLevel} · {ctx.AreaCode}";
        T(rt, ctxText, _tfSmall!, left + PadX + rt.MeasureText("POE2 Radar", _tfTitle!) + 16f, ty, UDim);
        TR(rt, $"{m.Fps} fps", _tfMonoSmall!, left + MW - PadX - 40f, ty, UDim);
        var closeR = new RawRectF(left + MW - PadX - 28f, ty - 14f, left + MW - PadX, ty + 14f);
        rt.FillRoundedRectangle(closeR, 4f, B(UBg2));
        rt.DrawLine(new NumVec2(closeR.Left + 9f, closeR.Top + 9f), new NumVec2(closeR.Right - 9f, closeR.Bottom - 9f), B(UMuted), 1.6f);
        rt.DrawLine(new NumVec2(closeR.Right - 9f, closeR.Top + 9f), new NumVec2(closeR.Left + 9f, closeR.Bottom - 9f), B(UMuted), 1.6f);
        _legendRowRects.Add((closeR, "ins:close"));
        HLine(rt, left, left + MW, top + TopH, ULine);

        // ── Tabs ──
        var tabY = top + TopH;
        var tx = left + PadX;
        for (var i = 0; i < Tabs.Length; i++)
        {
            var active = i == m.Tab;
            var tw = rt.MeasureText(Tabs[i], _tfBold!);
            var r = new RawRectF(tx - 8f, tabY + 4f, tx + tw + 8f, tabY + TabsH - 4f);
            T(rt, Tabs[i], active ? _tfBold! : _tfBody!, tx, tabY + TabsH * 0.5f, active ? UText : UMuted);
            if (active) rt.FillRectangle(new RawRectF(tx, tabY + TabsH - 3f, tx + tw, tabY + TabsH - 1f), B(UAccent));
            _legendRowRects.Add((r, "ins:tab:" + i));
            tx += tw + 28f;
        }
        HLine(rt, left, left + MW, tabY + TabsH, ULine);

        // ── Content ──
        var cx = left + PadX;
        var cw = MW - PadX * 2f;
        var cy = tabY + TabsH + 14f;
        var bottom = top + MH - FootH - 8f;
        switch (m.Tab)
        {
            case 0: DrawOverview(rt, ctx, m, cx, cy, cw); break;
            case 1: DrawCombat(rt, m, cx, cy, cw); break;
            case 2: DrawSkills(rt, m, cx, cy, cw, bottom); break;
            case 3: DrawClearing(rt, m, cx, cy, cw); break;
            case 5: DrawBoss(rt, m, cx, cy, cw); break;
            case 6: DrawRoll(rt, m, cx, cy, cw); break;
            default: DrawMovement(rt, m, cx, cy, cw); break;
        }

        // ── Footer: live status ──
        var fy = top + MH - FootH;
        HLine(rt, left, left + MW, fy, ULine);
        var fcy = fy + FootH * 0.5f;
        var sx = left + PadX;
        sx = Status(rt, sx, fcy, "Combat", ctx.CombatNote, ctx.CombatAssist);
        sx = Status(rt, sx, fcy, "Move", ctx.PathMoveNote, ctx.PathMove);
        Status(rt, sx, fcy, ctx.MapClear ? "Clear" : "Quest", ctx.MapClear ? ctx.MapClearNote : ctx.QuestFollowNote, ctx.MapClear || ctx.QuestFollow);
        TR(rt, "Insert to close", _tfSmall!, left + MW - PadX, fcy, UDim);
    }

    private float Status(DrawTarget rt, float x, float cy, string tag, string? note, bool on)
    {
        rt.FillEllipse(new Ellipse(new NumVec2(x + 4f, cy), 3.5f, 3.5f), B(on ? UOn : UOff));
        T(rt, tag, _tfSmall!, x + 13f, cy, UMuted);
        var tw = rt.MeasureText(tag, _tfSmall!);
        var n = Cut(string.IsNullOrEmpty(note) ? "—" : note!, 30);
        T(rt, n, _tfSmall!, x + 13f + tw + 6f, cy, on ? UText : UDim);
        return x + 13f + tw + 6f + rt.MeasureText(n, _tfSmall!) + 22f;
    }

    // ── Row components: label + help on the left, control on the right, hairline below ──
    private float RowBase(DrawTarget rt, float x, float y, float w, string label, string? help)
    {
        var cy = y + Row * 0.5f;
        if (string.IsNullOrEmpty(help)) T(rt, label, _tfBody!, x, cy, UText);
        else
        {
            T(rt, label, _tfBody!, x, cy - 7f, UText);
            T(rt, help, _tfSmall!, x, cy + 9f, UDim);
        }
        HLine(rt, x, x + w, y + Row, ULine);
        return cy;
    }

    private float Section(DrawTarget rt, float x, float y, string title)
    {
        T(rt, title.ToUpperInvariant(), _tfCaps!, x, y + 13f, UAccent);
        return y + 24f;
    }

    private float Toggle(DrawTarget rt, float x, float y, float w, string label, string? help, bool on, string action, string? hotkey = null)
    {
        var cy = RowBase(rt, x, y, w, label, help);
        if (hotkey is not null)
        {
            var kw = rt.MeasureText(hotkey, _tfMonoSmall!) + 10f;
            var kr = new RawRectF(x + w - 54f - kw, cy - 9f, x + w - 54f, cy + 9f);
            rt.DrawRoundedRectangle(kr, 3f, B(ULine), 1f);
            TC(rt, hotkey, _tfMonoSmall!, (kr.Left + kr.Right) * 0.5f, cy, UDim);
        }
        Switch(rt, x + w - 42f, cy, on);
        _legendRowRects.Add((new RawRectF(x, y, x + w, y + Row), action));
        return y + Row;
    }

    private void Switch(DrawTarget rt, float x, float cy, bool on)
    {
        var track = new RawRectF(x, cy - 10f, x + 42f, cy + 10f);
        rt.FillRoundedRectangle(track, 10f, B(on ? UOn : UOff));
        rt.FillEllipse(new Ellipse(new NumVec2(on ? track.Right - 10f : track.Left + 10f, cy), 7.5f, 7.5f), B(UText));
    }

    private float Slider(DrawTarget rt, float x, float y, float w, string label, string? help, float value, string key)
    {
        var spec = InsSliderSpec.All[key];
        var cy = RowBase(rt, x, y, w, label, help);
        const float trackW = 220f, valW = 62f, btn = 24f;
        var right = x + w;
        // [ − ] track [ + ]  value
        var plus = new RawRectF(right - valW - btn, cy - btn * 0.5f, right - valW, cy + btn * 0.5f);
        var track = new RawRectF(plus.Left - 10f - trackW, cy - 2f, plus.Left - 10f, cy + 2f);
        var minus = new RawRectF(track.Left - 10f - btn, plus.Top, track.Left - 10f, plus.Bottom);
        rt.FillRoundedRectangle(minus, 4f, B(UBg3));
        rt.FillRoundedRectangle(plus, 4f, B(UBg3));
        TC(rt, "−", _tfBold!, (minus.Left + minus.Right) * 0.5f, cy, UText);
        TC(rt, "+", _tfBold!, (plus.Left + plus.Right) * 0.5f, cy, UText);
        rt.FillRoundedRectangle(track, 2f, B(UBg3));
        var f = spec.Fraction(value);
        if (f > 0f) rt.FillRoundedRectangle(new RawRectF(track.Left, track.Top, track.Left + trackW * f, track.Bottom), 2f, B(UAccent));
        rt.FillEllipse(new Ellipse(new NumVec2(track.Left + trackW * f, cy), 7f, 7f), B(UText));
        TR(rt, spec.Format(value), _tfMono!, right, cy, UText);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _legendRowRects.Add((minus, $"ins:adj:{key}:{(-spec.Step).ToString(ci)}"));
        _legendRowRects.Add((plus, $"ins:adj:{key}:{spec.Step.ToString(ci)}"));
        _legendRowRects.Add((new RawRectF(track.Left - 4f, cy - 14f, track.Right + 4f, cy + 14f), "ins:slider:" + key));
        return y + Row;
    }

    private float Choice(DrawTarget rt, float x, float y, float w, string label, string? help, string current, string key, (string Value, string Label)[] options)
    {
        var cy = RowBase(rt, x, y, w, label, help);
        var right = x + w;
        for (var i = options.Length - 1; i >= 0; i--)
        {
            var (value, text) = options[i];
            var ow = rt.MeasureText(text, _tfSmall!) + 20f;
            var r = new RawRectF(right - ow, cy - 12f, right, cy + 12f);
            var sel = string.Equals(current, value, StringComparison.OrdinalIgnoreCase)
                      || (value == "Click" && current.StartsWith("Click", StringComparison.OrdinalIgnoreCase));
            rt.FillRoundedRectangle(r, 4f, B(sel ? UAccent : UBg3));
            TC(rt, text, sel ? _tfBold! : _tfSmall!, (r.Left + r.Right) * 0.5f, cy, sel ? UInk : UMuted);
            _legendRowRects.Add((r, $"ins:set:{key}:{value}"));
            right = r.Left - 6f;
        }
        return y + Row;
    }

    private float InfoRow(DrawTarget rt, float x, float y, float w, string label, string value, Color4 col)
    {
        var cy = RowBase(rt, x, y, w, label, null);
        TR(rt, value, _tfMono!, x + w, cy, col);
        return y + Row;
    }

    private float Bar(DrawTarget rt, float x, float y, float w, string label, float pct, Color4 col, float threshold)
    {
        var cy = RowBase(rt, x, y, w, label, null);
        var track = new RawRectF(x + 130f, cy - 4f, x + w - 60f, cy + 4f);
        rt.FillRoundedRectangle(track, 4f, B(UBg3));
        var fw = track.Width * Math.Clamp(pct, 0f, 100f) / 100f;
        if (fw > 1f) rt.FillRoundedRectangle(new RawRectF(track.Left, track.Top, track.Left + fw, track.Bottom), 4f, B(col));
        if (threshold > 0f)
        {
            var tx = track.Left + track.Width * Math.Clamp(threshold, 0f, 100f) / 100f;
            rt.DrawLine(new NumVec2(tx, track.Top - 3f), new NumVec2(tx, track.Bottom + 3f), B(UText), 1.5f);
        }
        TR(rt, $"{Math.Clamp(pct, 0f, 100f):0}%", _tfMono!, x + w, cy, col);
        return y + Row;
    }

    // ── Tabs ──
    private void DrawOverview(DrawTarget rt, RenderContext ctx, InsMenuData m, float x, float y, float w)
    {
        var colW = (w - 32f) * 0.5f;
        var lx = x; var ly = Section(rt, lx, y, "Automation");
        ly = Toggle(rt, lx, ly, colW, "Bot", "quest follow + move + combat", ctx.BotEnabled, "ins:toggle:bot", "F3");
        ly = Toggle(rt, lx, ly, colW, "Map clear", "sweep the zone, run events", ctx.MapClear, "ins:toggle:clear", "F2");
        ly = Toggle(rt, lx, ly, colW, "Combat", "aimed skill rotation", ctx.CombatAssist, "ins:toggle:combat", "F4");
        ly = Toggle(rt, lx, ly, colW, "Movement", "walk the route", ctx.PathMove, "ins:toggle:move", "F5");
        ly = Toggle(rt, lx, ly, colW, "Auto flask", "life / mana under threshold", ctx.AutoFlask, "ins:toggle:flask", "F8");
        ly = Toggle(rt, lx, ly, colW, "Auto respawn", string.IsNullOrEmpty(m.RespawnNote) ? "click Resurrect on death" : m.RespawnNote, m.AutoRespawn, "ins:flag:autoRespawn");

        var rx = x + colW + 32f; var ry = Section(rt, rx, y, "Character");
        ry = Bar(rt, rx, ry, colW, "Life", ctx.HpPct, URed, m.LifeThresholdPct);
        ry = Bar(rt, rx, ry, colW, "Mana", ctx.ManaPct, UBlue, m.ManaThresholdPct);
        ry = Bar(rt, rx, ry, colW, "Energy shield", ctx.EsPct, UTeal, 0f);
        ry = Section(rt, rx, ry + 2f, "Now");
        ry = InfoRow(rt, rx, ry, colW, "Hostiles in range", m.HostilesNear.ToString(), m.HostilesNear > 0 ? UText : UDim);
        var target = ctx.MapClear ? ctx.MapClearNote : ctx.QuestFollowNote;
        ry = InfoRow(rt, rx, ry, colW, "Target", Cut(string.IsNullOrEmpty(target) ? "none" : target.TrimStart('→', ' '), 22), UMuted);
        InfoRow(rt, rx, ry, colW, "Frame time", $"{m.WorldMs:0.0} / {m.RenderMs:0.0} ms", UDim);
    }

    private void DrawCombat(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Section(rt, x, y, "Targeting");
        y = Choice(rt, x, y, w, "Pick target", "who the rotation aims at", m.TargetMode, "combatTargetMode",
            new[] { ("Nearest", "Nearest"), ("Rarity", "Rarity"), ("LowestHp", "Weakest"), ("HighestHp", "Tank") });
        y = Choice(rt, x, y, w, "Rotation", "cycle the list, or always the first ready slot", m.RotationMode, "combatRotationMode",
            new[] { ("RoundRobin", "Round-robin"), ("Priority", "Priority") });
        y = Section(rt, x, y + 2f, "Ranges");
        y = Slider(rt, x, y, w, "Attack range", "fire when a hostile is within", m.CombatRange, "combatRange");
        y = Slider(rt, x, y, w, "Engage range", "stop walking to fight inside", m.CombatEngageRange, "combatEngageRange");
        y = Slider(rt, x, y, w, "Keep distance", "back off if a mob gets closer — ranged builds", m.KeepDistance, "combatKeepDistance");
        y = Slider(rt, x, y, w, "Give up after", "no damage dealt for this long → skip (immune)", m.CombatStallMs, "combatStallMs");
        y = Section(rt, x, y + 2f, "Retreat");
        y = Slider(rt, x, y, w, "Flee below", "run when life drops under", m.CombatFleeHpPct, "combatFleeHpPct");
        y = Slider(rt, x, y, w, "Resume at", "back to the fight at", m.CombatFleeRecoverPct, "combatFleeRecoverPct");
        Slider(rt, x, y, w, "Flee distance", "cells to run, most open direction", m.CombatFleeDistance, "combatFleeDistance");
    }

    private void DrawClearing(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Section(rt, x, y, "Sweep");
        y = Slider(rt, x, y, w, "Explored radius", "cells counted as seen around you", m.MapClearStampRadius, "mapClearStampRadius");
        y = Slider(rt, x, y, w, "Chase packs within", "farther packs are met by the sweep", m.MapClearAggroRange, "mapClearAggroRange");
        y = Slider(rt, x, y, w, "Stuck timeout", "skip a target you stop getting closer to", m.MapClearStuckMs, "mapClearStuckMs");
        y = Section(rt, x, y + 2f, "Events · walk up and click");
        var colW = (w - 32f) * 0.5f;
        var ly = y; var ry = y; var rx = x + colW + 32f;
        ly = Toggle(rt, x, ly, colW, "Essences", "click the crystal, then fight", m.EventEssence, "ins:flag:eventEssence");
        ry = Toggle(rt, rx, ry, colW, "Strongboxes", null, m.EventStrongbox, "ins:flag:eventStrongbox");
        ly = Toggle(rt, x, ly, colW, "Shrines", null, m.EventShrine, "ins:flag:eventShrine");
        ry = Toggle(rt, rx, ry, colW, "Breach hands", null, m.EventBreach, "ins:flag:eventBreach");
        ly = Toggle(rt, x, ly, colW, "Ritual altars", "starts a ritual", m.EventRitual, "ins:flag:eventRitual");
        ry = Toggle(rt, rx, ry, colW, "Plain chests", null, m.EventChests, "ins:flag:eventChests");
        ly = Toggle(rt, x, ly, colW, "Stalled monsters", "immune rare → click it", m.EventClickStalled, "ins:flag:eventClickStalled");
        y = Math.Max(ly, ry);
        y = Slider(rt, x, y, w, "Event range", "only detour to events this close", m.EventRange, "eventRange");
        TR(rt, $"explored {m.VisitedCells:N0} cells this zone", _tfSmall!, x + w, y + 14f, UDim);
    }

    private void DrawMovement(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Section(rt, x, y, "Walking");
        y = Choice(rt, x, y, w, "Method", "WASD keys held, or click-to-move", m.MoveMethod, "moveMethod", new[] { ("WASD", "WASD"), ("Click", "Click") });
        y = Toggle(rt, x, y, w, "Run", $"{KeyLabel(m.MoveRunKey)} while travelling — dodge-rolls via the arbiter when it is the dodge key (Roll tab)", m.MoveRunEnabled, "ins:flag:moveRunEnabled");
        y = Toggle(rt, x, y, w, "Diagonals", "two keys at once, 8-way", m.MoveDiagonals, "ins:flag:moveDiagonals");
        y = Slider(rt, x, y, w, "Look-ahead", "steer at the farthest visible waypoint", m.MoveLookAhead, "moveLookAhead");
        y = Slider(rt, x, y, w, "Axis rotation", "if the character walks off at an angle", m.MoveAxisRotationDeg, "moveAxisRotationDeg");
        y = Slider(rt, x, y, w, "Arrive radius", "waypoint counts as reached inside", m.MoveArriveRadius, "moveArriveRadius");
        y = Section(rt, x, y + 2f, "Flasks");
        y = Slider(rt, x, y, w, "Life flask at", null, m.LifeThresholdPct, "lifeThresholdPct");
        Slider(rt, x, y, w, "Mana flask at", null, m.ManaThresholdPct, "manaThresholdPct");
    }

    private void DrawBoss(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Section(rt, x, y, "Boss mode · a unique inside the engage range");
        y = InfoRow(rt, x, y, w, "Now", string.IsNullOrEmpty(m.BossNote) ? "no boss engaged" : m.BossNote, string.IsNullOrEmpty(m.BossNote) ? UDim : UText);
        y = Slider(rt, x, y, w, "Flee below", "tighter than the normal flee threshold", m.BossFleeHpPct, "bossFleeHpPct");
        y = Slider(rt, x, y, w, "Keep distance", "back off from the boss inside this", m.BossKeepDistance, "bossKeepDistance");
        y = Slider(rt, x, y, w, "Timed roll every", "dodge on a clock while engaged", m.BossDodgeIntervalMs, "bossDodgeIntervalMs");
        y = Slider(rt, x, y, w, "Roll on life spike", "life lost within 0.6 s that triggers a dodge", m.BossDodgeSpikePct, "bossDodgeSpikePct");
        y = Toggle(rt, x, y, w, "Walk back after death", "re-engage where the boss was seen last", m.BossReengage, "ins:flag:bossReengage");
        y = Section(rt, x, y + 2f, "Combos");
        Slider(rt, x, y, w, "No combo under", "finish a nearly dead target with a quick slot", m.CombatComboSkipHpPct, "combatComboSkipHpPct");
    }

    private void DrawRoll(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Section(rt, x, y, "Dodge roll · one owner at a time (mover / combo / flee / boss)");
        y = InfoRow(rt, x, y, w, "Now", string.IsNullOrEmpty(m.RollNote) ? "idle" : m.RollNote, string.IsNullOrEmpty(m.RollNote) ? UDim : UText);
        y = Slider(rt, x, y, w, "Key press", "how long the dodge key is held", m.RollPressMs, "rollPressMs");
        y = Slider(rt, x, y, w, "Recovery", "no roll, no cast until the animation is done", m.CombatDodgeRecoverMs, "combatDodgeRecoverMs");
        y = Slider(rt, x, y, w, "Dodge-after delay", "extra wait past the last cast's gap before rolling (queued taps)", m.CombatDodgeDelayMs, "combatDodgeDelayMs");
        y = Section(rt, x, y + 2f, "Run · dodge key held while travelling, re-pressed when speed says it was ignored");
        y = InfoRow(rt, x, y, w, "Speed", string.IsNullOrEmpty(m.SpeedNote) ? "learning walk / run speed" : m.SpeedNote, string.IsNullOrEmpty(m.SpeedNote) ? UDim : UText);
        y = Slider(rt, x, y, w, "Straight needed", "cells of visible route ahead before holding run", m.MoveRollMinCells, "moveRollMinCells");
        y = Section(rt, x, y + 2f, "Route");
        y = Slider(rt, x, y, w, "Coast on replan", "keep walking while the route is rebuilt", m.MoveCoastMs, "moveCoastMs");
        y = Slider(rt, x, y, w, "Arrive at monsters", "close enough to attack", m.MoveArriveRadiusMob, "moveArriveRadiusMob");
        Slider(rt, x, y, w, "Arrive at events", "close enough to click", m.MoveArriveRadiusEvent, "moveArriveRadiusEvent");
    }

    // ── Skills: one card per slot ──
    private void DrawSkills(DrawTarget rt, InsMenuData m, float x, float y, float w, float bottom)
    {
        var skills = m.Skills ?? Array.Empty<CombatSkill>();
        const float cardH = 80f, gap = 8f;
        var shown = 0;
        for (var i = 0; i < skills.Count && y + cardH <= bottom - 34f; i++, shown++)
        {
            var sk = skills[i];
            var card = new RawRectF(x, y, x + w, y + cardH);
            rt.FillRoundedRectangle(card, R, B(UBg2));
            if (!sk.Enabled) rt.DrawRoundedRectangle(card, R, B(ULine), 1f);
            var ink = sk.Enabled ? UText : UDim;

            // Key badge.
            var badge = new RawRectF(x + 12f, y + 20f, x + 52f, y + 60f);
            rt.FillRoundedRectangle(badge, 6f, B(sk.Enabled ? UAccentDim : UBg3));
            rt.DrawRoundedRectangle(badge, 6f, B(sk.Enabled ? UAccent : ULine), 1f);
            TC(rt, KeyLabel(sk.Key), _tfBold!, (badge.Left + badge.Right) * 0.5f, (badge.Top + badge.Bottom) * 0.5f, sk.Enabled ? UAccent : UDim);
            _legendRowRects.Add((new RawRectF(badge.Left, badge.Top, (badge.Left + badge.Right) * 0.5f, badge.Bottom), $"ins:skill:adj:{i}:key:-1"));
            _legendRowRects.Add((new RawRectF((badge.Left + badge.Right) * 0.5f, badge.Top, badge.Right, badge.Bottom), $"ins:skill:adj:{i}:key:1"));

            // Line 1: cast conditions.  Line 2: combo.
            var fx = x + 66f;
            var l1 = y + 18f; var l2 = y + 50f;
            fx = Field(rt, fx, l1, "Cooldown", $"{sk.CooldownMs} ms", i, "cd", 50f, ink);
            fx = Field(rt, fx, l1, "Range", sk.Range <= 0f ? "auto" : $"{sk.Range:0}", i, "range", 5f, ink);
            fx = Field(rt, fx, l1, "Min mobs", sk.MinTargets <= 1 ? "1" : $"{sk.MinTargets}+", i, "min", 1f, ink);
            fx = Field(rt, fx, l1, "Life below", sk.HpBelowPct <= 0f ? "off" : $"{sk.HpBelowPct:0}%", i, "hp", 5f, ink);
            Check(rt, fx + 8f, l1, "Rare only", sk.RareOnly, $"ins:skill:flip:{i}:rareOnly");

            fx = x + 66f;
            fx = Field(rt, fx, l2, "Taps", $"×{Math.Max(1, sk.Repeat)}", i, "repeat", 1f, ink);
            fx = Field(rt, fx, l2, "Cast time", $"{sk.RepeatGapMs} ms", i, "gap", 50f, ink);
            fx = Field(rt, fx, l2, "Hold", sk.HoldMs <= 0 ? "tap" : $"{sk.HoldMs} ms", i, "hold", 100f, ink);
            fx = Field(rt, fx, l2, "Then wait", sk.NextDelayMs <= 0 ? "0" : $"{sk.NextDelayMs} ms", i, "next", 100f, ink);
            Check(rt, fx + 8f, l2, "Dodge after", sk.DodgeAfter, $"ins:skill:flip:{i}:dodgeAfter");

            // Enabled switch + remove, right edge.
            Switch(rt, x + w - 58f, y + 26f, sk.Enabled);
            _legendRowRects.Add((new RawRectF(x + w - 64f, y + 10f, x + w - 10f, y + 42f), $"ins:skill:flip:{i}:enabled"));
            TR(rt, "remove", _tfSmall!, x + w - 16f, y + 60f, UDim);
            _legendRowRects.Add((new RawRectF(x + w - 70f, y + 48f, x + w - 8f, y + 72f), $"ins:skill:del:{i}"));
            y += cardH + gap;
        }

        if (skills.Count < 8)
        {
            var add = new RawRectF(x, y, x + 120f, y + 30f);
            rt.FillRoundedRectangle(add, 4f, B(UBg3));
            TC(rt, "+ Add skill", _tfBold!, (add.Left + add.Right) * 0.5f, (add.Top + add.Bottom) * 0.5f, UText);
            _legendRowRects.Add((add, "ins:skill:add"));
        }
        if (shown < skills.Count) T(rt, $"{skills.Count - shown} more not shown", _tfSmall!, x + 136f, y + 15f, UDim);
        T(rt, "Taps ×N with Cast time between them (also the wait before a dodge, so the last cast finishes) · Hold = keep the key down instead", _tfSmall!, x, bottom - 10f, UDim);
    }

    /// <summary>Small labelled field: caption above, "‹ value ›" below. Returns the x after it.</summary>
    private float Field(DrawTarget rt, float x, float cy, string caption, string value, int i, string field, float step, Color4 ink)
    {
        const float w = 118f;
        T(rt, caption, _tfSmall!, x + 14f, cy - 9f, UDim);
        var l = new RawRectF(x, cy + 1f, x + 14f, cy + 17f);
        var r = new RawRectF(x + w - 14f, cy + 1f, x + w, cy + 17f);
        TC(rt, "‹", _tfBold!, (l.Left + l.Right) * 0.5f, cy + 9f, UMuted);
        TC(rt, "›", _tfBold!, (r.Left + r.Right) * 0.5f, cy + 9f, UMuted);
        T(rt, value, _tfMono!, x + 14f, cy + 9f, ink);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _legendRowRects.Add((l, $"ins:skill:adj:{i}:{field}:{(-step).ToString(ci)}"));
        _legendRowRects.Add((r, $"ins:skill:adj:{i}:{field}:{step.ToString(ci)}"));
        return x + w + 6f;
    }

    private void Check(DrawTarget rt, float x, float cy, string label, bool on, string action)
    {
        var box = new RawRectF(x, cy + 2f, x + 14f, cy + 16f);
        rt.FillRoundedRectangle(box, 3f, B(on ? UAccent : UBg3));
        rt.DrawRoundedRectangle(box, 3f, B(on ? UAccent : ULine), 1f);
        if (on)
        {
            rt.DrawLine(new NumVec2(box.Left + 3f, cy + 9f), new NumVec2(box.Left + 6f, cy + 12.5f), B(UInk), 1.8f);
            rt.DrawLine(new NumVec2(box.Left + 6f, cy + 12.5f), new NumVec2(box.Right - 3f, cy + 5f), B(UInk), 1.8f);
        }
        T(rt, label, _tfSmall!, x + 20f, cy + 9f, on ? UText : UMuted);
        _legendRowRects.Add((new RawRectF(x - 4f, cy - 4f, x + 24f + rt.MeasureText(label, _tfSmall!), cy + 20f), action));
    }

    internal static string KeyLabel(int vk) => vk switch
    {
        0x01 => "LMB", 0x02 => "RMB", 0x04 => "MMB", 0x05 => "M4", 0x06 => "M5",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        0x20 => "Space",
        _ => $"0x{vk:X2}",
    };
}
