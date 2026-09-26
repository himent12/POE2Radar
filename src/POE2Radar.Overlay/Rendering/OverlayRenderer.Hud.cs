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
    private static readonly (string Label, string Corner, string Action)[] NavCorners =
    {
        ("[TL]", "TopLeft", "corner:TopLeft"), ("[TR]", "TopRight", "corner:TopRight"),
        ("[BL]", "BottomLeft", "corner:BottomLeft"), ("[BR]", "BottomRight", "corner:BottomRight"),
    };

    // Memoized two-part concatenations for strings the HUD rebuilds from the same pieces every frame (legend
    // rows, click actions, status lines) — the render loop would otherwise allocate them per frame. Bounded:
    // cleared wholesale if it ever grows past the cap (e.g. after many zones' worth of legend names).
    private readonly Dictionary<(string, string), string> _concat = new();

    private string Cat(string a, string b)
    {
        if (b.Length == 0) return a;
        if (a.Length == 0) return b;
        if (_concat.TryGetValue((a, b), out var s)) return s;
        if (_concat.Count >= 2048) _concat.Clear();
        return _concat[(a, b)] = a + b;
    }

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
        const string chipOpen = "v " + chip, chipClosed = "> " + chip;
        var chipRect = new RawRectF(left + NavPad, headerY, left + NavPad + chipW, headerY + NavHeaderH - 2f);
        rt.FillRectangle(chipRect, _bPanel!);
        rt.DrawRectangle(chipRect, _bPlayer!, 1f);
        rt.DrawText(expanded ? chipOpen : chipClosed, _tf!,
            new Rect(chipRect.Left + 4f, headerY + 2f, chipRect.Right, headerY + NavHeaderH), _bText!, DrawTextOptions.Clip);
        _legendRowRects.Add((chipRect, "menu-toggle"));

        // Four corner buttons after the chip. The one matching the current corner is highlighted.
        var bx = chipRect.Right + 6f;
        foreach (var (label, c, action) in NavCorners)
        {
            var bw = label.Length * 7.3f + 4f;
            var bRect = new RawRectF(bx, headerY, bx + bw, headerY + NavHeaderH - 2f);
            var sel = c == corner;
            rt.DrawText(label, _tf!, new Rect(bRect.Left, headerY + 2f, bRect.Right + 6f, headerY + NavHeaderH),
                sel ? _bPlayer! : _bOther!, DrawTextOptions.Clip);
            _legendRowRects.Add((bRect, action));
            bx += bw + 4f;
        }

        if (!expanded) return;

        // Dropdown rows. For Bottom corners the chip is at the bottom, so rows fill from the panel top.
        var rowTop = isBottom ? top + NavPad : headerY + NavHeaderH;
        var y = rowTop;
        for (var i = 0; i < ctx.Legend.Count; i++)
        {
            var row = ctx.Legend[i];
            var rowRect = new RawRectF(left, y, left + panelW, y + NavRowH);
            _legendRowRects.Add((rowRect, Cat("target:", row.Target.Id))); // click → TogglePathTarget(id)

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
            var text = Cat(prefix, row.Target.Name);
            var textBrush = row.IsSelected ? _bPlayer! : (row.Target.IsEntity ? _bLandmark! : _bText!);
            rt.DrawText(text, _tf!, new Rect(left + NavPad + NavSwatch + 5f, y, left + panelW - 4f, y + NavRowH), textBrush, DrawTextOptions.Clip);
            y += NavRowH;
        }
    }

    private static readonly Color4 ColOn = new(0.35f, 0.95f, 0.45f, 1f);

    private static readonly Color4 ColOff = new(0.55f, 0.50f, 0.42f, 0.85f);

    /// <summary>
    /// Live ON/OFF strip for the input modules (flask, macros, …). Top-right unless the nav menu is
    /// already pinned there, in which case it drops to the bottom-right. Not clickable.
    /// </summary>
    private void DrawStatusStrip(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.Status is not { Count: > 0 } chips) return;
        const float w = 228f, rowH = 16f, pad = 6f, titleH = 16f, dot = 7f;
        var h = pad * 2f + titleH + chips.Count * rowH;
        var x = ctx.WindowWidth - NavMargin - w;
        var y = NavMargin;
        if (ctx.NavMenuCorner is "TopRight")
            y = Math.Max(NavMargin, ctx.WindowHeight - NavMargin - h);

        rt.FillRectangle(new RawRectF(x, y, x + w, y + h), _bPanel!);
        rt.DrawText("STATUS", _tf!, new Rect(x + pad, y + pad, x + w - pad, y + pad + titleH), _bText!, DrawTextOptions.Clip);
        for (var i = 0; i < chips.Count; i++)
        {
            var c = chips[i];
            var label = c.Hotkey.Length > 0 ? Cat(Cat(c.Hotkey, " "), c.Name) : c.Name;
            DrawStatusRow(rt, x, y + pad + titleH + rowH * i, w, rowH, pad, dot, label, c.On, c.Note);
        }
    }

    private void DrawStatusRow(DrawTarget rt, float x, float y, float w, float rowH, float pad, float dot,
        string label, bool on, string? note)
    {
        _bStyle!.Color = on ? ColOn : ColOff;
        rt.FillEllipse(new Ellipse(new NumVec2(x + pad + dot * 0.5f, y + rowH * 0.5f), dot * 0.45f, dot * 0.45f), _bStyle);
        var tag = on ? "ON  " : "off ";   // "ON "/"off" + the separating space
        var extra = string.IsNullOrEmpty(note) ? "" : Cat(" ", TrimNote(note));
        var line = Cat(Cat(tag, label), extra);
        rt.DrawText(line, _tf!, new Rect(x + pad + dot + 4f, y, x + w - pad, y + rowH), _bStyle, DrawTextOptions.Clip);
    }

    private readonly Dictionary<string, string> _trimmedNotes = new();

    private string TrimNote(string note)
    {
        if (note.StartsWith("OFF ", StringComparison.Ordinal)) return "";
        if (note.Length <= 18) return note;
        if (_trimmedNotes.TryGetValue(note, out var t)) return t;
        if (_trimmedNotes.Count >= 256) _trimmedNotes.Clear();
        return _trimmedNotes[note] = note[..17] + "…";
    }
}
