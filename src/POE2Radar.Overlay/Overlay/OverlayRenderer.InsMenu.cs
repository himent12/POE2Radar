using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// INSERT in-game menu. Styled after the game's own panels: near-black parchment, gold hairlines,
/// serif title, square corners, monospace values. Left sidebar = sections, right = controls.
///
/// Click grammar (<see cref="_legendRowRects"/> → <c>RadarApp.OnOverlayClick</c>):
/// <c>ins:panel</c> (swallow) · <c>ins:close</c> · <c>ins:tab:N</c> ·
/// <c>ins:toggle:bot|clear|combat|move|flask</c> · <c>ins:slider:&lt;key&gt;</c> (value from click X) ·
/// <c>ins:adj:&lt;key&gt;:&lt;delta&gt;</c> · <c>ins:set:&lt;key&gt;:&lt;value&gt;</c> (segmented) ·
/// <c>ins:skill:add</c> · <c>ins:skill:del:i</c> · <c>ins:skill:flip:i:enabled|rareOnly</c> ·
/// <c>ins:skill:adj:i:key|cd|range|min|hp:&lt;delta&gt;</c>.
/// </summary>
public sealed partial class OverlayRenderer
{
    // ── Palette (PoE-ish: black leather, brass/gold, bone text) ──
    private static readonly Color4 PBg       = new(0.043f, 0.039f, 0.035f, 0.965f);
    private static readonly Color4 PBgSide   = new(0.030f, 0.027f, 0.024f, 0.975f);
    private static readonly Color4 PBgRow    = new(1f, 1f, 1f, 0.028f);
    private static readonly Color4 PBgRowAlt = new(1f, 1f, 1f, 0.012f);
    private static readonly Color4 PGold     = new(0.79f, 0.66f, 0.40f, 1f);
    private static readonly Color4 PGoldDim  = new(0.79f, 0.66f, 0.40f, 0.45f);
    private static readonly Color4 PGoldLine = new(0.62f, 0.50f, 0.28f, 0.75f);
    private static readonly Color4 PLine     = new(1f, 1f, 1f, 0.07f);
    private static readonly Color4 PText     = new(0.86f, 0.82f, 0.74f, 1f);
    private static readonly Color4 PMuted    = new(0.56f, 0.52f, 0.45f, 1f);
    private static readonly Color4 PDim      = new(0.40f, 0.37f, 0.32f, 1f);
    private static readonly Color4 POn       = new(0.52f, 0.80f, 0.42f, 1f);
    private static readonly Color4 POff      = new(0.36f, 0.33f, 0.29f, 1f);
    private static readonly Color4 PRed      = new(0.86f, 0.32f, 0.26f, 1f);
    private static readonly Color4 PBlue     = new(0.40f, 0.60f, 0.95f, 1f);
    private static readonly Color4 PTeal     = new(0.45f, 0.82f, 0.86f, 1f);
    private static readonly Color4 PInk      = new(0.08f, 0.07f, 0.06f, 1f);

    private const float MenuW = 760f, MenuH = 480f, SideW = 176f, Pad = 16f, RowH = 34f, FootH = 30f;
    private static readonly string[] Sections = { "Overview", "Combat", "Skills", "Clearing", "Movement" };

    private DrawTextFormat? _tfTitle, _tfBody, _tfBold, _tfSmall, _tfMono, _tfMonoSmall, _tfCaps;
    private DrawBrush? _bUi;

    private void EnsureInsResources()
    {
        if (_tfTitle is not null) return;
        _tfTitle     = _window.CreateUiTextFormat(20f, bold: true, serif: true);
        _tfBody      = _window.CreateUiTextFormat(12.5f);
        _tfBold      = _window.CreateUiTextFormat(12.5f, bold: true);
        _tfSmall     = _window.CreateUiTextFormat(10.5f);
        _tfCaps      = _window.CreateUiTextFormat(10f, bold: true);
        _tfMono      = _window.CreateTextFormat("Consolas", 12f);
        _tfMonoSmall = _window.CreateTextFormat("Consolas", 10.5f);
        _bUi = _window.RenderTarget.CreateSolidColorBrush(PText);
    }

    private DrawBrush B(Color4 c) { _bUi!.Color = c; return _bUi; }
    private void T(DrawTarget rt, string s, DrawTextFormat tf, float x, float cy, Color4 c) => rt.DrawTextVCentered(s, tf, x, cy, B(c));
    private void TR(DrawTarget rt, string s, DrawTextFormat tf, float right, float cy, Color4 c) => T(rt, s, tf, right - rt.MeasureText(s, tf), cy, c);
    private void TC(DrawTarget rt, string s, DrawTextFormat tf, float cx, float cy, Color4 c) => T(rt, s, tf, cx - rt.MeasureText(s, tf) * 0.5f, cy, c);
    private void HLine(DrawTarget rt, float x1, float x2, float y, Color4 c) => rt.DrawLine(new NumVec2(x1, y), new NumVec2(x2, y), B(c), 1f);
    private static string Caps(string s) => s.ToUpperInvariant();

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

        var left = MathF.Round((ctx.WindowWidth - MenuW) * 0.5f);
        var top = MathF.Round((ctx.WindowHeight - MenuH) * 0.5f);
        var panel = new RawRectF(left, top, left + MenuW, top + MenuH);

        // Frame: shadow, body, sidebar, double gold hairline.
        rt.DrawShadow(panel, 2f, 18f, 0.6f);
        rt.FillRectangle(panel, B(PBg));
        rt.FillRectangle(new RawRectF(left, top, left + SideW, top + MenuH), B(PBgSide));
        rt.DrawRectangle(panel, B(PGoldLine), 1f);
        rt.DrawRectangle(new RawRectF(left + 3f, top + 3f, left + MenuW - 3f, top + MenuH - 3f), B(new Color4(PGold.R, PGold.G, PGold.B, 0.18f)), 1f);
        rt.DrawLine(new NumVec2(left + SideW, top + 3f), new NumVec2(left + SideW, top + MenuH - 3f), B(PLine), 1f);
        _legendRowRects.Add((panel, "ins:panel"));

        DrawSidebar(rt, ctx, m, left, top);

        var cx0 = left + SideW + Pad;
        var cw = MenuW - SideW - Pad * 2f;
        var cy0 = top + Pad;

        // Section header + close.
        T(rt, Caps(Sections[Math.Clamp(m.Tab, 0, Sections.Length - 1)]), _tfCaps!, cx0, cy0 + 8f, PGold);
        HLine(rt, cx0, cx0 + cw, cy0 + 22f, PGoldDim);
        var closeR = new RawRectF(left + MenuW - Pad - 18f, cy0, left + MenuW - Pad, cy0 + 18f);
        rt.DrawRectangle(closeR, B(PLine), 1f);
        rt.DrawLine(new NumVec2(closeR.Left + 5f, closeR.Top + 5f), new NumVec2(closeR.Right - 5f, closeR.Bottom - 5f), B(PMuted), 1.4f);
        rt.DrawLine(new NumVec2(closeR.Right - 5f, closeR.Top + 5f), new NumVec2(closeR.Left + 5f, closeR.Bottom - 5f), B(PMuted), 1.4f);
        _legendRowRects.Add((closeR, "ins:close"));

        var y = cy0 + 32f;
        var bottom = top + MenuH - FootH - 8f;
        switch (m.Tab)
        {
            case 0: DrawOverview(rt, ctx, m, cx0, y, cw); break;
            case 1: DrawCombat(rt, m, cx0, y, cw); break;
            case 2: DrawSkills(rt, m, cx0, y, cw, bottom); break;
            case 3: DrawClearing(rt, m, cx0, y, cw); break;
            default: DrawMovement(rt, m, cx0, y, cw); break;
        }

        // Footer: live notes + key hints.
        var fy = top + MenuH - FootH;
        HLine(rt, left + SideW + 1f, left + MenuW - 3f, fy, PLine);
        var fcy = fy + FootH * 0.5f;
        var nx = cx0;
        nx = Note(rt, "combat", ctx.CombatNote, ctx.CombatAssist, nx, fcy);
        nx = Note(rt, "move", ctx.PathMoveNote, ctx.PathMove, nx, fcy);
        Note(rt, ctx.MapClear ? "clear" : "quest", ctx.MapClear ? ctx.MapClearNote : ctx.QuestFollowNote, ctx.MapClear || ctx.QuestFollow, nx, fcy);
        TR(rt, "INS close", _tfMonoSmall!, left + MenuW - Pad, fcy, PDim);
    }

    private float Note(DrawTarget rt, string tag, string? note, bool on, float x, float cy)
    {
        var n = string.IsNullOrEmpty(note) ? "—" : (note.Length > 24 ? note[..23] + "…" : note);
        rt.FillRectangle(new RawRectF(x, cy - 3f, x + 6f, cy + 3f), B(on ? POn : POff));
        T(rt, tag, _tfMonoSmall!, x + 11f, cy, PMuted);
        var tw = rt.MeasureText(tag, _tfMonoSmall!);
        T(rt, n, _tfMonoSmall!, x + 11f + tw + 6f, cy, on ? PText : PDim);
        return x + 11f + tw + 6f + rt.MeasureText(n, _tfMonoSmall!) + 18f;
    }

    // ── Sidebar ──
    private void DrawSidebar(DrawTarget rt, RenderContext ctx, InsMenuData m, float left, float top)
    {
        var x = left + Pad;
        T(rt, "POE2 Radar", _tfTitle!, x, top + 30f, PGold);
        T(rt, "OVERLAY CONSOLE", _tfCaps!, x, top + 50f, PDim);
        HLine(rt, x, left + SideW - Pad, top + 64f, PGoldDim);

        var y = top + 78f;
        var leds = new[] { ctx.BotEnabled || ctx.MapClear || ctx.CombatAssist || ctx.PathMove || ctx.AutoFlask, ctx.CombatAssist, ctx.CombatAssist, ctx.MapClear, ctx.PathMove };
        for (var i = 0; i < Sections.Length; i++)
        {
            var r = new RawRectF(left + 1f, y, left + SideW - 1f, y + 28f);
            var active = i == m.Tab;
            if (active)
            {
                rt.FillRectangle(r, B(new Color4(PGold.R, PGold.G, PGold.B, 0.10f)));
                rt.FillRectangle(new RawRectF(left + 1f, y, left + 4f, y + 28f), B(PGold));
            }
            T(rt, Sections[i], active ? _tfBold! : _tfBody!, x + 4f, y + 14f, active ? PText : PMuted);
            rt.FillRectangle(new RawRectF(left + SideW - Pad - 5f, y + 11.5f, left + SideW - Pad, y + 16.5f), B(leds[i] ? POn : POff));
            _legendRowRects.Add((r, "ins:tab:" + i));
            y += 28f;
        }

        // Character / area / perf at the bottom of the sidebar.
        var by = top + MenuH - FootH - 44f;
        HLine(rt, x, left + SideW - Pad, by, PLine);
        T(rt, string.IsNullOrEmpty(m.CharName) ? "—" : m.CharName, _tfBold!, x, by + 14f, PText);
        T(rt, $"Lv {ctx.CharLevel}  ·  {ctx.AreaCode}", _tfSmall!, x, by + 30f, PMuted);
        TR(rt, $"{m.Fps} fps", _tfMonoSmall!, left + SideW - Pad, top + MenuH - FootH * 0.5f, PDim);
        T(rt, $"{m.WorldMs:0.0}/{m.RenderMs:0.0} ms", _tfMonoSmall!, x, top + MenuH - FootH * 0.5f, PDim);
    }

    // ── Overview: arm toggles + vitals ──
    private void DrawOverview(DrawTarget rt, RenderContext ctx, InsMenuData m, float x, float y, float w)
    {
        var half = (w - 12f) * 0.5f;
        var ty = y;
        ty = Toggle(rt, x, ty, half, "Bot master", "F3", ctx.BotEnabled, "ins:toggle:bot", 0);
        ty = Toggle(rt, x, ty, half, "Map clear", "F2", ctx.MapClear, "ins:toggle:clear", 1);
        ty = Toggle(rt, x, ty, half, "Combat assist", "F4", ctx.CombatAssist, "ins:toggle:combat", 2);
        ty = Toggle(rt, x, ty, half, "Path move", "F5", ctx.PathMove, "ins:toggle:move", 3);
        Toggle(rt, x, ty, half, "Auto flask", "F8", ctx.AutoFlask, "ins:toggle:flask", 4);

        var vx = x + half + 12f;
        var vy = y;
        vy = Vital(rt, vx, vy, half, "Life", ctx.HpPct, PRed, m.LifeThresholdPct);
        vy = Vital(rt, vx, vy, half, "Mana", ctx.ManaPct, PBlue, m.ManaThresholdPct);
        vy = Vital(rt, vx, vy, half, "Energy shield", ctx.EsPct, PTeal, 0f);

        vy = ToggleWide(rt, vx, vy, half, "Auto-respawn", string.IsNullOrEmpty(m.RespawnNote) ? "resurrect at checkpoint on death" : m.RespawnNote, m.AutoRespawn, "ins:flag:autoRespawn", 3);

        // Live tactical line.
        rt.FillRectangle(new RawRectF(vx, vy + 4f, vx + half, vy + 4f + RowH * 1.6f), B(PBgRowAlt));
        rt.DrawRectangle(new RawRectF(vx, vy + 4f, vx + half, vy + 4f + RowH * 1.6f), B(PLine), 1f);
        var sit = m.HostilesNear > 0 ? $"{m.HostilesNear} hostile(s) in attack range" : "no hostiles in range";
        T(rt, sit, _tfBody!, vx + 10f, vy + 4f + 16f, m.HostilesNear > 0 ? PText : PMuted);
        var target = ctx.MapClear ? ctx.MapClearNote : ctx.QuestFollowNote;
        T(rt, string.IsNullOrEmpty(target) ? "no nav target" : (target.Length > 34 ? target[..33] + "…" : target), _tfSmall!, vx + 10f, vy + 4f + 36f, PMuted);
    }

    private float Toggle(DrawTarget rt, float x, float y, float w, string name, string key, bool on, string action, int i)
    {
        var r = new RawRectF(x, y, x + w, y + RowH);
        rt.FillRectangle(r, B(i % 2 == 0 ? PBgRow : PBgRowAlt));
        var cy = y + RowH * 0.5f;
        // Square check.
        var box = new RawRectF(x + 10f, cy - 6f, x + 22f, cy + 6f);
        rt.DrawRectangle(box, B(on ? PGold : PDim), 1f);
        if (on) rt.FillRectangle(new RawRectF(box.Left + 3f, box.Top + 3f, box.Right - 3f, box.Bottom - 3f), B(PGold));
        T(rt, name, _tfBody!, x + 32f, cy, on ? PText : PMuted);
        TR(rt, key, _tfMonoSmall!, x + w - 40f, cy, PDim);
        TR(rt, on ? "ON" : "OFF", _tfMono!, x + w - 10f, cy, on ? POn : PDim);
        _legendRowRects.Add((r, action));
        return y + RowH + 2f;
    }

    private float Vital(DrawTarget rt, float x, float y, float w, string label, float pct, Color4 col, float threshold)
    {
        var r = new RawRectF(x, y, x + w, y + RowH);
        rt.FillRectangle(r, B(PBgRowAlt));
        var cy = y + RowH * 0.5f;
        T(rt, label, _tfBody!, x + 10f, cy, PMuted);
        var track = new RawRectF(x + 110f, cy - 5f, x + w - 54f, cy + 5f);
        rt.FillRectangle(track, B(new Color4(0f, 0f, 0f, 0.5f)));
        var fw = track.Width * Math.Clamp(pct, 0f, 100f) / 100f;
        if (fw > 0.5f) rt.FillRectangle(new RawRectF(track.Left, track.Top, track.Left + fw, track.Bottom), B(col));
        rt.DrawRectangle(track, B(PLine), 1f);
        if (threshold > 0f)
        {
            var tx = track.Left + track.Width * Math.Clamp(threshold, 0f, 100f) / 100f;
            rt.DrawLine(new NumVec2(tx, track.Top - 2f), new NumVec2(tx, track.Bottom + 2f), B(PGold), 1f);
        }
        TR(rt, $"{Math.Clamp(pct, 0f, 100f):0}%", _tfMono!, x + w - 10f, cy, col);
        return y + RowH + 2f;
    }

    // ── Combat ──
    private void DrawCombat(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Segmented(rt, x, y, w, "Target", "who the rotation aims at", m.TargetMode, "combatTargetMode",
            new[] { ("Nearest", "Nearest"), ("Rarity", "Rarity"), ("LowestHp", "Weakest"), ("HighestHp", "Tank") }, 0);
        y = Segmented(rt, x, y, w, "Rotation", "cycle the list, or first ready slot", m.RotationMode, "combatRotationMode",
            new[] { ("RoundRobin", "Round-robin"), ("Priority", "Priority") }, 1);
        y = Slider(rt, x, y, w, "Attack range", "fire when a hostile is within", m.CombatRange, "combatRange", 2);
        y = Slider(rt, x, y, w, "Engage range", "stop walking to fight inside", m.CombatEngageRange, "combatEngageRange", 3);
        y = Slider(rt, x, y, w, "Keep distance", "back off if a mob gets closer (ranged)", m.KeepDistance, "combatKeepDistance", 4);
        y = Slider(rt, x, y, w, "Flee below", "run from the pack under this life", m.CombatFleeHpPct, "combatFleeHpPct", 5);
        y = Slider(rt, x, y, w, "Resume at", "return to the fight at this life", m.CombatFleeRecoverPct, "combatFleeRecoverPct", 6);
        y = Slider(rt, x, y, w, "Flee distance", "cells to run, most open direction", m.CombatFleeDistance, "combatFleeDistance", 7);
        Slider(rt, x, y, w, "Give up after", "no damage for this long → skip pack", m.CombatStallMs, "combatStallMs", 8);
    }

    // ── Skills table ──
    private static readonly (string Field, string Head, float W)[] SkillCols =
    {
        ("key", "KEY", 62f), ("cd", "COOLDOWN", 92f), ("range", "RANGE", 78f), ("min", "MIN MOBS", 78f), ("hp", "LIFE <", 78f),
    };

    private void DrawSkills(DrawTarget rt, InsMenuData m, float x, float y, float w, float bottom)
    {
        var skills = m.Skills ?? Array.Empty<CombatSkill>();
        // Header row.
        var cx = x + 30f;
        T(rt, "#", _tfCaps!, x + 8f, y + 8f, PDim);
        foreach (var (_, head, cw) in SkillCols) { TC(rt, head, _tfCaps!, cx + cw * 0.5f, y + 8f, PDim); cx += cw; }
        TC(rt, "RARE", _tfCaps!, cx + 24f, y + 8f, PDim); cx += 48f;
        TC(rt, "ON", _tfCaps!, cx + 20f, y + 8f, PDim);
        HLine(rt, x, x + w, y + 18f, PLine);
        y += 22f;

        for (var i = 0; i < skills.Count && y + 28f < bottom - 30f; i++)
        {
            var sk = skills[i];
            var r = new RawRectF(x, y, x + w, y + 28f);
            rt.FillRectangle(r, B(i % 2 == 0 ? PBgRow : PBgRowAlt));
            var cy = y + 14f;
            var ink = sk.Enabled ? PText : PDim;
            T(rt, (i + 1).ToString(), _tfMonoSmall!, x + 8f, cy, PDim);
            cx = x + 30f;
            Cell(rt, cx, cy, SkillCols[0].W, KeyLabel(sk.Key), i, "key", 1f, ink); cx += SkillCols[0].W;
            Cell(rt, cx, cy, SkillCols[1].W, $"{sk.CooldownMs}ms", i, "cd", 50f, ink); cx += SkillCols[1].W;
            Cell(rt, cx, cy, SkillCols[2].W, sk.Range <= 0f ? "auto" : $"{sk.Range:0}", i, "range", 5f, ink); cx += SkillCols[2].W;
            Cell(rt, cx, cy, SkillCols[3].W, sk.MinTargets <= 1 ? "1" : $"≥{sk.MinTargets}", i, "min", 1f, ink); cx += SkillCols[3].W;
            Cell(rt, cx, cy, SkillCols[4].W, sk.HpBelowPct <= 0f ? "any" : $"{sk.HpBelowPct:0}%", i, "hp", 5f, ink); cx += SkillCols[4].W;
            Check(rt, cx + 24f, cy, sk.RareOnly, $"ins:skill:flip:{i}:rareOnly"); cx += 48f;
            Check(rt, cx + 20f, cy, sk.Enabled, $"ins:skill:flip:{i}:enabled");
            // Remove.
            var del = new RawRectF(x + w - 22f, cy - 8f, x + w - 6f, cy + 8f);
            rt.DrawLine(new NumVec2(del.Left + 4f, del.Top + 4f), new NumVec2(del.Right - 4f, del.Bottom - 4f), B(PDim), 1.2f);
            rt.DrawLine(new NumVec2(del.Right - 4f, del.Top + 4f), new NumVec2(del.Left + 4f, del.Bottom - 4f), B(PDim), 1.2f);
            _legendRowRects.Add((del, $"ins:skill:del:{i}"));
            y += 30f;
        }

        if (skills.Count < 8)
        {
            var add = new RawRectF(x, y + 2f, x + 110f, y + 24f);
            rt.DrawRectangle(add, B(PGoldDim), 1f);
            TC(rt, "+ add skill", _tfSmall!, (add.Left + add.Right) * 0.5f, (add.Top + add.Bottom) * 0.5f, PGold);
            _legendRowRects.Add((add, "ins:skill:add"));
        }
        T(rt, "‹ › step a value  ·  key cycles Q W E R T 1-5 and mouse buttons  ·  RANGE auto = attack range", _tfSmall!, x, bottom - 12f, PDim);
    }

    private void Cell(DrawTarget rt, float x, float cy, float w, string value, int i, string field, float step, Color4 ink)
    {
        var l = new RawRectF(x + 2f, cy - 9f, x + 16f, cy + 9f);
        var r = new RawRectF(x + w - 16f, cy - 9f, x + w - 2f, cy + 9f);
        TC(rt, "‹", _tfBold!, (l.Left + l.Right) * 0.5f, cy, PMuted);
        TC(rt, "›", _tfBold!, (r.Left + r.Right) * 0.5f, cy, PMuted);
        TC(rt, value, _tfMono!, x + w * 0.5f, cy, ink);
        _legendRowRects.Add((l, $"ins:skill:adj:{i}:{field}:{-step}"));
        _legendRowRects.Add((r, $"ins:skill:adj:{i}:{field}:{step}"));
    }

    private void Check(DrawTarget rt, float cx, float cy, bool on, string action)
    {
        var box = new RawRectF(cx - 6f, cy - 6f, cx + 6f, cy + 6f);
        rt.DrawRectangle(box, B(on ? PGold : PDim), 1f);
        if (on) rt.FillRectangle(new RawRectF(box.Left + 3f, box.Top + 3f, box.Right - 3f, box.Bottom - 3f), B(PGold));
        _legendRowRects.Add((new RawRectF(cx - 14f, cy - 12f, cx + 14f, cy + 12f), action));
    }

    internal static string KeyLabel(int vk) => vk switch
    {
        0x01 => "LMB", 0x02 => "RMB", 0x04 => "MMB", 0x05 => "M4", 0x06 => "M5",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        0x20 => "SPC",
        _ => $"0x{vk:X2}",
    };

    // ── Clearing / Movement ──
    private void DrawClearing(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Slider(rt, x, y, w, "Stamp radius", "cells marked explored around you", m.MapClearStampRadius, "mapClearStampRadius", 0);
        y = Slider(rt, x, y, w, "Aggro range", "chase packs this close", m.MapClearAggroRange, "mapClearAggroRange", 1);
        y = Slider(rt, x, y, w, "Stuck timeout", "skip a target you stop getting closer to", m.MapClearStuckMs, "mapClearStuckMs", 2);
        rt.FillRectangle(new RawRectF(x, y + 6f, x + w, y + 6f + RowH), B(PBgRowAlt));
        T(rt, "explored this zone", _tfBody!, x + 10f, y + 6f + RowH * 0.5f, PMuted);
        TR(rt, $"{m.VisitedCells:N0} cells", _tfMono!, x + w - 10f, y + 6f + RowH * 0.5f, PText);
        T(rt, "Sweep heads for the fog: biggest unexplored area per step, steps through fog cost less than cleared ground,", _tfSmall!, x, y + 6f + RowH + 18f, PDim);
        T(rt, "slivers are skipped and the zone counts as cleared when only slivers remain. Bosses first, packs inside aggro.", _tfSmall!, x, y + 6f + RowH + 32f, PDim);
    }

    private void DrawMovement(DrawTarget rt, InsMenuData m, float x, float y, float w)
    {
        y = Segmented(rt, x, y, w, "Method", "WASD taps or click-to-move", m.MoveMethod, "moveMethod",
            new[] { ("WASD", "WASD"), ("Click", "Click") }, 0);
        y = ToggleWide(rt, x, y, w, "Run while moving", $"hold {KeyLabel(m.MoveRunKey)} whenever the bot travels", m.MoveRunEnabled, "ins:flag:moveRunEnabled", 1);
        y = ToggleWide(rt, x, y, w, "Diagonals", "hold two keys at once (8-way)", m.MoveDiagonals, "ins:flag:moveDiagonals", 2);
        y = Slider(rt, x, y, w, "Look-ahead", "steer at the farthest visible waypoint", m.MoveLookAhead, "moveLookAhead", 3);
        y = Slider(rt, x, y, w, "Axis rotation", "if the character walks off at an angle", m.MoveAxisRotationDeg, "moveAxisRotationDeg", 4);
        y = Slider(rt, x, y, w, "Arrive radius", "waypoint counts as reached inside", m.MoveArriveRadius, "moveArriveRadius", 5);
        y = Slider(rt, x, y, w, "Click interval", "click-to-move only", m.MoveCooldownMs, "moveCooldownMs", 6);
        y = Slider(rt, x, y, w, "Life flask at", "auto-flask trigger", m.LifeThresholdPct, "lifeThresholdPct", 7);
        Slider(rt, x, y, w, "Mana flask at", "auto-flask trigger", m.ManaThresholdPct, "manaThresholdPct", 8);
    }

    private float ToggleWide(DrawTarget rt, float x, float y, float w, string name, string desc, bool on, string action, int i)
    {
        var r = new RawRectF(x, y, x + w, y + RowH);
        rt.FillRectangle(r, B(i % 2 == 0 ? PBgRow : PBgRowAlt));
        var cy = y + RowH * 0.5f;
        T(rt, name, _tfBody!, x + 10f, cy - 6f, PText);
        T(rt, desc, _tfSmall!, x + 10f, cy + 8f, PDim);
        var box = new RawRectF(x + w - 10f - 12f, cy - 6f, x + w - 10f, cy + 6f);
        rt.DrawRectangle(box, B(on ? PGold : PDim), 1f);
        if (on) rt.FillRectangle(new RawRectF(box.Left + 3f, box.Top + 3f, box.Right - 3f, box.Bottom - 3f), B(PGold));
        TR(rt, on ? "ON" : "OFF", _tfMono!, box.Left - 10f, cy, on ? POn : PDim);
        _legendRowRects.Add((r, action));
        return y + RowH + 2f;
    }

    // ── Shared controls ──
    private float Slider(DrawTarget rt, float x, float y, float w, string name, string desc, float value, string key, int i)
    {
        var spec = InsSliderSpec.All[key];
        var r = new RawRectF(x, y, x + w, y + RowH);
        rt.FillRectangle(r, B(i % 2 == 0 ? PBgRow : PBgRowAlt));
        var cy = y + RowH * 0.5f;
        T(rt, name, _tfBody!, x + 10f, cy - 6f, PText);
        T(rt, desc, _tfSmall!, x + 10f, cy + 8f, PDim);

        const float valW = 58f, arrow = 14f;
        var trackR = new RawRectF(x + w - 10f - valW - arrow - 180f, cy - 3f, x + w - 10f - valW - arrow, cy + 3f);
        rt.FillRectangle(trackR, B(new Color4(0f, 0f, 0f, 0.5f)));
        var frac = spec.Fraction(value);
        if (frac > 0f) rt.FillRectangle(new RawRectF(trackR.Left, trackR.Top, trackR.Left + trackR.Width * frac, trackR.Bottom), B(PGold));
        rt.DrawRectangle(trackR, B(PLine), 1f);
        var kx = trackR.Left + trackR.Width * frac;
        rt.FillRectangle(new RawRectF(kx - 3f, cy - 7f, kx + 3f, cy + 7f), B(PText));
        _legendRowRects.Add((new RawRectF(trackR.Left, cy - 12f, trackR.Right, cy + 12f), "ins:slider:" + key));

        // ‹ value ›
        var lb = new RawRectF(trackR.Left - arrow - 4f, cy - 10f, trackR.Left - 4f, cy + 10f);
        var rb = new RawRectF(trackR.Right + 4f, cy - 10f, trackR.Right + 4f + arrow, cy + 10f);
        TC(rt, "‹", _tfBold!, (lb.Left + lb.Right) * 0.5f, cy, PMuted);
        TC(rt, "›", _tfBold!, (rb.Left + rb.Right) * 0.5f, cy, PMuted);
        _legendRowRects.Add((lb, $"ins:adj:{key}:{(-spec.Step).ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        _legendRowRects.Add((rb, $"ins:adj:{key}:{spec.Step.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        TR(rt, spec.Format(value), _tfMono!, x + w - 10f, cy, PGold);
        return y + RowH + 2f;
    }

    private float Segmented(DrawTarget rt, float x, float y, float w, string name, string desc, string current, string key, (string Value, string Label)[] options, int i)
    {
        var r = new RawRectF(x, y, x + w, y + RowH);
        rt.FillRectangle(r, B(i % 2 == 0 ? PBgRow : PBgRowAlt));
        var cy = y + RowH * 0.5f;
        T(rt, name, _tfBody!, x + 10f, cy - 6f, PText);
        T(rt, desc, _tfSmall!, x + 10f, cy + 8f, PDim);
        var cx = x + w - 10f;
        for (var k = options.Length - 1; k >= 0; k--)
        {
            var (value, label) = options[k];
            var ow = rt.MeasureText(label, _tfSmall!) + 18f;
            var o = new RawRectF(cx - ow, cy - 10f, cx, cy + 10f);
            var sel = string.Equals(current, value, StringComparison.OrdinalIgnoreCase)
                      || (value == "Click" && current.StartsWith("Click", StringComparison.OrdinalIgnoreCase));
            if (sel) rt.FillRectangle(o, B(PGold));
            rt.DrawRectangle(o, B(sel ? PGold : PLine), 1f);
            TC(rt, label, _tfSmall!, (o.Left + o.Right) * 0.5f, cy, sel ? PInk : PMuted);
            _legendRowRects.Add((o, $"ins:set:{key}:{value}"));
            cx = o.Left - 4f;
        }
        return y + RowH + 2f;
    }
}
