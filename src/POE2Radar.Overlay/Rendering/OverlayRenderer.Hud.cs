using System.Globalization;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

public sealed partial class OverlayRenderer
{
    // ── Navigation-menu widget geometry (all in client/device pixels at 96 DPI). ──
    private const float NavPad = 6f, NavRowH = 18f, NavHeaderH = 22f, NavSwatch = 10f, NavPanelW = 230f;

    private const float NavMargin = 6f;

    // ASCII corner buttons (Consolas lacks reliable ↖↗↙↘ glyphs, so we use these per spec).
    private static readonly (string Label, string Corner)[] NavCorners =
    {
        ("[TL]", "TopLeft"), ("[TR]", "TopRight"), ("[BL]", "BottomLeft"), ("[BR]", "BottomRight"),
    };

    /// <summary>
    /// The collapsible, corner-pinnable "POE2Radar" navigation menu. Always drawn (map open or not)
    /// while the overlay is Active + InGame; replaces the old status line AND the bottom-left legend.
    ///
    /// <para>COLLAPSED: a "POE2Radar" chip plus four corner buttons (<c>[TL] [TR] [BL] [BR]</c>).
    /// EXPANDED: the navigation targets (ctx.Legend) under the chip — a colored swatch + curated name
    /// per row; selected rows show a filled swatch in their route color + a highlight, unselected rows
    /// a dim outline swatch. No hotkey-hint line.</para>
    ///
    /// <para>Pinned via <see cref="RenderContext.NavMenuCorner"/>. The panel is anchored at the chosen
    /// corner and CLAMPED so the whole expanded dropdown stays on-screen: <c>*Right</c> right-aligns,
    /// <c>Bottom*</c> grows upward (chip pinned to the bottom edge, rows stacked above it). Every
    /// clickable rect is recorded into <see cref="LegendRowRects"/> with its Action string.</para>
    /// </summary>
    private void DrawNavMenu(DrawTarget rt, RenderContext ctx)
    {
        _legendRowRects.Clear();

        var expanded = ctx.NavMenuExpanded;
        var rowCount = expanded ? ctx.Legend.Count : 0;
        var panelW   = NavPanelW;
        var panelH   = NavHeaderH + rowCount * NavRowH + NavPad * 2;

        // Anchor at the chosen corner, then clamp so the whole panel (incl. dropdown) is on-screen.
        var corner   = ctx.NavMenuCorner;
        var isRight  = corner is "TopRight" or "BottomRight";
        var isBottom = corner is "BottomLeft" or "BottomRight";

        var left = isRight ? ctx.WindowWidth - NavMargin - panelW : NavMargin;
        var top  = isBottom ? ctx.WindowHeight - NavMargin - panelH : NavMargin;
        // Clamp into the window in case it's narrower/shorter than the panel.
        left = Math.Clamp(left, NavMargin, Math.Max(NavMargin, ctx.WindowWidth  - NavMargin - panelW));
        top  = Math.Clamp(top,  NavMargin, Math.Max(NavMargin, ctx.WindowHeight - NavMargin - panelH));

        rt.FillRectangle(new RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        // Header row: when pinned to a Bottom corner the dropdown grows UPWARD, so the chip sits at
        // the BOTTOM of the panel and rows stack above it; otherwise the chip is at the top.
        var headerY = isBottom && expanded ? top + panelH - NavPad - NavHeaderH : top + NavPad;

        // "POE2Radar" chip (click → toggle dropdown). Sized to its text so the corner buttons sit after it.
        const string chip = "POE2Radar";
        var chipW = chip.Length * 7.3f + 8f;
        var chipRect = new RawRectF(left + NavPad, headerY, left + NavPad + chipW, headerY + NavHeaderH - 2f);
        rt.FillRectangle(chipRect, _bPanel!);
        rt.DrawRectangle(chipRect, _bPlayer!, 1f);
        rt.DrawText((expanded ? "v " : "> ") + chip, _tf!,
            new Rect(chipRect.Left + 4f, headerY + 2f, chipRect.Right, headerY + NavHeaderH), _bText!, DrawTextOptions.Clip);
        _legendRowRects.Add((chipRect, "menu-toggle"));

        // Four corner buttons after the chip. The one matching the current corner is highlighted.
        var bx = chipRect.Right + 6f;
        foreach (var (label, c) in NavCorners)
        {
            var bw = label.Length * 7.3f + 4f;
            var bRect = new RawRectF(bx, headerY, bx + bw, headerY + NavHeaderH - 2f);
            var sel = c == corner;
            rt.DrawText(label, _tf!, new Rect(bRect.Left, headerY + 2f, bRect.Right + 6f, headerY + NavHeaderH),
                sel ? _bPlayer! : _bOther!, DrawTextOptions.Clip);
            _legendRowRects.Add((bRect, "corner:" + c));
            bx += bw + 4f;
        }

        if (!expanded) return;

        // Dropdown rows. For Bottom corners the chip is at the bottom, so rows fill from the panel top.
        var rowTop = isBottom ? top + NavPad : headerY + NavHeaderH;
        var y = rowTop;
        foreach (var row in ctx.Legend)
        {
            var rowRect = new RawRectF(left, y, left + panelW, y + NavRowH);
            _legendRowRects.Add((rowRect, "target:" + row.Target.Id)); // click → TogglePathTarget(id)

            // Swatch: selected rows fill with their selection-order route color (matches DrawPaths);
            // unselected rows get just a dim outline so the click target is still visible.
            var swatchRect = new RawRectF(left + NavPad, y + 3f, left + NavPad + NavSwatch, y + 3f + NavSwatch);
            if (row.IsSelected)
            {
                _bPath!.Color = PathColor(row.ColorSlot);
                rt.FillRectangle(swatchRect, _bPath);
            }
            else
            {
                _bPath!.Color = WithAlpha(_bOther!.Color, 0.45f);
                rt.DrawRectangle(swatchRect, _bPath, 1f);
            }

            // Selected rows get a "> " marker + the highlight color. Entity POIs get a "*" prefix so
            // they're distinguishable from tile landmarks at a glance. Name is already prettified/curated.
            var prefix = row.IsSelected ? "> " : (row.Target.IsEntity ? "* " : "  ");
            var text = prefix + row.Target.Name;
            var textBrush = row.IsSelected ? _bPlayer! : (row.Target.IsEntity ? _bLandmark! : _bText!);
            rt.DrawText(text, _tf!, new Rect(left + NavPad + NavSwatch + 5f, y, left + panelW - 4f, y + NavRowH), textBrush, DrawTextOptions.Clip);
            y += NavRowH;
        }
    }

    private static readonly Color4 ColOn = new(0.35f, 0.95f, 0.45f, 1f);

    private static readonly Color4 ColOff = new(0.55f, 0.50f, 0.42f, 0.85f);

    /// <summary>
    /// Live ON/OFF strip for bot/clear/combat/move/quest/flask. Top-right unless the nav menu is
    /// already pinned there, in which case it drops to the bottom-right. Not clickable.
    /// </summary>
    private void DrawBotStatus(DrawTarget rt, RenderContext ctx)
    {
        const float w = 228f, rowH = 16f, pad = 6f, titleH = 16f, dot = 7f;
        const int n = 6;
        var h = pad * 2f + titleH + n * rowH;
        var x = ctx.WindowWidth - NavMargin - w;
        var y = NavMargin;
        if (ctx.NavMenuCorner is "TopRight")
            y = Math.Max(NavMargin, ctx.WindowHeight - NavMargin - h);

        rt.FillRectangle(new RawRectF(x, y, x + w, y + h), _bPanel!);
        rt.DrawText("STATUS", _tf!, new Rect(x + pad, y + pad, x + w - pad, y + pad + titleH), _bText!, DrawTextOptions.Clip);

        DrawStatusRow(rt, x, y + pad + titleH, w, rowH, pad, dot, "F3 Bot", ctx.BotEnabled, ctx.BotNote);
        DrawStatusRow(rt, x, y + pad + titleH + rowH, w, rowH, pad, dot, "F2 Clear", ctx.MapClear, ctx.MapClearNote);
        DrawStatusRow(rt, x, y + pad + titleH + rowH * 2, w, rowH, pad, dot, "F4 Combat", ctx.CombatAssist, ctx.CombatNote);
        DrawStatusRow(rt, x, y + pad + titleH + rowH * 3, w, rowH, pad, dot, "F5 Move", ctx.PathMove, ctx.PathMoveNote);
        DrawStatusRow(rt, x, y + pad + titleH + rowH * 4, w, rowH, pad, dot, "Quest", ctx.QuestFollow, ctx.QuestFollowNote);
        DrawStatusRow(rt, x, y + pad + titleH + rowH * 5, w, rowH, pad, dot, "F8 Flask", ctx.AutoFlask, ctx.FlaskNote);
    }

    private void DrawStatusRow(DrawTarget rt, float x, float y, float w, float rowH, float pad, float dot,
        string label, bool on, string? note)
    {
        _bStyle!.Color = on ? ColOn : ColOff;
        rt.FillEllipse(new Ellipse(new NumVec2(x + pad + dot * 0.5f, y + rowH * 0.5f), dot * 0.45f, dot * 0.45f), _bStyle);
        var tag = on ? "ON " : "off";
        var extra = string.IsNullOrEmpty(note) ? "" : " " + TrimNote(note);
        var line = tag + " " + label + extra;
        rt.DrawText(line, _tf!, new Rect(x + pad + dot + 4f, y, x + w - pad, y + rowH), _bStyle, DrawTextOptions.Clip);
    }

    private static string TrimNote(string note)
    {
        if (note.StartsWith("OFF ", StringComparison.Ordinal)) return "";
        return note.Length <= 18 ? note : note[..17] + "…";
    }
}
