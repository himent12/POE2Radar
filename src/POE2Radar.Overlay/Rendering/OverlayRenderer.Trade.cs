using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// Trade panel — one card per whisper-driven trade (Client.txt), anchored at a window fraction so it survives
/// resolution changes. Buttons register <c>trade:&lt;id&gt;:&lt;action&gt;</c> click rects; RadarApp turns them into
/// chat lines (/invite, /tradewith, @thanks …) sent through the gated chat worker.
/// </summary>
public sealed partial class OverlayRenderer
{
    private const float TradeW = 420f, TradeCardH = 104f;
    private const int TradeMaxCards = 4;

    private static readonly (string Label, string Action)[] IncomingButtons =
        { ("Invite", "invite"), ("Trade", "trade"), ("Thanks", "thanks"), ("Kick", "kick"), ("Sold", "sold") };

    private static readonly (string Label, string Action)[] OutgoingButtons =
        { ("Hideout", "visithideout"), ("Trade", "trade"), ("Thanks", "thanks"), ("Ask", "stillinterested") };

    /// <summary>Headless draw of just the trade panel (preview/tests).</summary>
    internal void RenderTradePanelPreview(RenderContext ctx)
    {
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(new Color4(0.12f, 0.10f, 0.09f, 1f));
        _legendRowRects.Clear();
        DrawTradePanel(rt, ctx);
        rt.EndDraw();
    }

    private void DrawTradePanel(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.Trades is not { Count: > 0 } cards) return;
        EnsureInsResources();
        BeginUiFrame(ctx);
        var shown = Math.Min(cards.Count, TradeMaxCards);
        var h = 52f + shown * TradeCardH + (cards.Count > shown ? 26f : 6f);
        var x = MathF.Round(Math.Clamp(ctx.TradePanelX, 0f, 1f) * ctx.WindowWidth);
        var y = MathF.Round(Math.Clamp(ctx.TradePanelY, 0f, 1f) * ctx.WindowHeight);
        x = Math.Clamp(x, 4f, Math.Max(4f, ctx.WindowWidth - TradeW - 4f));
        var panel = new RawRectF(x, y, x + TradeW, y + h);
        Frame(rt, panel);

        var open = 0;
        foreach (var c in cards) if (c.State is "New" or "Invited" or "InArea" or "Trading") open++;
        var hx = TT(rt, "TRADES", _tfCardTitle!, x + 22f, y + 26f, UWhite, 0.16f);
        if (open > 0) Chip(rt, $"{open} open", hx + 12f, y + 26f, ChipStyle.Live);
        TTR(rt, "ᛏᚱᚨᛞᛖ", _tfRuneSm!, x + TradeW - 22f, y + 26f, UFaint, 0.4f);
        y += 48f;

        for (var i = 0; i < shown; i++)
        {
            HLine(rt, x + 14f, x + TradeW - 14f, y, URule);
            DrawTradeCard(rt, cards[i], x, y);
            y += TradeCardH;
        }
        if (cards.Count > shown)
            TC(rt, $"+{cards.Count - shown} more on the dashboard", _tfSmall!, x + TradeW * 0.5f, y + 12f, UDim);
    }

    private void DrawTradeCard(DrawTarget rt, TradeCard c, float x, float y)
    {
        var done = c.State is "Completed" or "Cancelled";
        var side = done ? UDim : c.Incoming ? UOn : UBlue;
        DiamondMark(rt, x + 14f, y + 18f, side);

        // Row 1: player · where they are · age · dismiss
        var r1 = y + 18f;
        var px = x + 28f;
        var who = Fit(rt, c.Player, _tfName!, 170f);
        T(rt, who, _tfName!, px, r1, done ? UMuted : UWhite);
        px += rt.MeasureText(who, _tfName!) + 10f;
        var (state, stateCol) = c.InArea ? ("in your area", UOn)
            : done ? (c.State.ToLowerInvariant(), UDim)
            : c.State is "Invited" or "Trading" ? (c.State.ToLowerInvariant(), UText)
            : (c.Incoming ? "wants to buy" : "you whispered", UDim);
        T(rt, state, _tfSmall!, px, r1, stateCol);
        var close = new RawRectF(x + TradeW - 30f, r1 - 10f, x + TradeW - 10f, r1 + 10f);
        Icon(rt, "close", close.Left + 3f, close.Top + 3f, 14f, UMuted, 1.6f);
        _legendRowRects.Add((close, $"trade:{c.Id}:dismiss"));
        TR(rt, (c.Repeats > 0 ? $"×{c.Repeats + 1}  " : "") + c.Age, _tfSmall!, close.Left - 4f, r1, UDim);

        // Row 2: item · price (~ exalted)
        var r2 = y + 40f;
        var price = c.Price.ToUpperInvariant();
        var priceW = rt.MeasureText(price, _tfValue!);
        var valueW = c.Value.Length > 0 ? rt.MeasureText(c.Value, _tfSmall!) + 8f : 0f;
        TR(rt, price, _tfValue!, x + TradeW - 16f - valueW, r2, UWhite);
        if (valueW > 0f) TR(rt, c.Value, _tfSmall!, x + TradeW - 16f, r2, UDim);
        T(rt, Fit(rt, c.Item, _tfBody!, TradeW - priceW - valueW - 60f), _tfBody!, x + 28f, r2, done ? UMuted : UText);
        if (c.Where.Length > 0 || c.LastWhisper.Length > 0)
            T(rt, Fit(rt, c.LastWhisper.Length > 0 ? "“" + c.LastWhisper + "”" : "Stash " + c.Where, _tfSmall!, TradeW - 44f), _tfSmall!, x + 28f, y + 59f, UDim);

        // Row 3: actions
        var bx = x + 28f;
        var by = y + 84f;
        foreach (var (label, action) in c.Incoming ? IncomingButtons : OutgoingButtons)
        {
            if (done && action is not ("thanks" or "kick")) continue;
            var primary = action == (c.InArea ? "trade" : c.Incoming ? "invite" : "visithideout") && !done;
            var r = Button(rt, label, bx, by, $"trade:{c.Id}:{action}", primary, h: 26f, small: true);
            bx = r.Right + 5f;
        }
    }
}
