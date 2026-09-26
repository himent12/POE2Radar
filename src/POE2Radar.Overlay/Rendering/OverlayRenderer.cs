using System.Globalization;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// PoE2 radar overlay. When the large map is open, draws the walkable-terrain bitmap, entity
/// dots (enemies, NPCs, etc.), and the player blip, projected player-centered onto the map with
/// the same isometric math the PoE Radar plugin uses. Projection scale/offset are calibratable
/// at runtime (see <see cref="RadarApp"/>).
/// </summary>
public sealed partial class OverlayRenderer : IDisposable
{
    // Entity dot colors now live per-item in RadarSettings.Styles (these were the old hardcoded
    // values, preserved as the style defaults). Only the HUD/nav/landmark-label colors remain here.
    private static readonly Color4 ColPlayer  = new(0.30f, 0.95f, 1.00f, 1.00f);

    private static readonly Color4 ColOther   = new(0.70f, 0.70f, 0.70f, 0.60f);

    private static readonly Color4 ColText    = new(1f, 1f, 1f, 1f);

    private static readonly Color4 ColPanel   = new(0.05f, 0.05f, 0.05f, 0.78f);

    private static readonly Color4 ColLandmark = new(0.95f, 0.35f, 0.95f, 1f);

    private static readonly Color4 ColTargetMark = new(1f, 1f, 1f, 1f);

    private static readonly Color4 ColLowHp   = new(1.00f, 0.20f, 0.20f, 0.95f);

    // Distinct, evenly-spread hues for per-landmark guidance paths / legend swatches.
    private static readonly Color4[] PathPalette =
    {
        new(0.20f, 0.90f, 0.40f, 1f), // green
        new(1.00f, 0.55f, 0.10f, 1f), // orange
        new(0.30f, 0.70f, 1.00f, 1f), // sky blue
        new(1.00f, 0.30f, 0.70f, 1f), // pink
        new(0.95f, 0.90f, 0.20f, 1f), // yellow
        new(0.60f, 0.40f, 1.00f, 1f), // violet
        new(0.20f, 1.00f, 0.85f, 1f), // teal
        new(1.00f, 0.40f, 0.40f, 1f), // salmon
    };

    /// <summary>The color a guidance path (and its legend swatch) draws in, by selection-order color slot.</summary>
    private static Color4 PathColor(int slot) => PathPalette[((slot % PathPalette.Length) + PathPalette.Length) % PathPalette.Length];

    /// <summary>
    /// Screen rectangles of the interactive navigation-menu widget, rebuilt every frame the widget
    /// draws (and cleared when the overlay isn't Active/InGame). RadarApp hit-tests pointer clicks
    /// against these to toggle the menu, pin a corner, or toggle a path target. Each entry pairs a
    /// rect with an Action string: <c>"menu-toggle"</c>, <c>"corner:TopLeft|TopRight|BottomLeft|
    /// BottomRight"</c>, or <c>"target:&lt;navTargetId&gt;"</c> (dropdown rows, only when expanded).
    /// </summary>
    public IReadOnlyList<(RawRectF Rect, string Action)> LegendRowRects => _legendRowRects;

    private readonly List<(RawRectF Rect, string Action)> _legendRowRects = new();

    private readonly OverlayWindow _window;

    private AtlasIconCache? _atlasIcons;

    private DrawBrush? _bPlayer, _bOther, _bText, _bPanel, _bLandmark;

    private DrawBrush? _bPath;

    private DrawBrush? _bStyle;

    private DrawTextFormat? _tf;

    private bool _ready;

    public OverlayRenderer(OverlayWindow window) { _window = window; }

    private void EnsureResources()
    {
        if (_ready) return;
        var rt = _window.RenderTarget;
        _bPlayer   = rt.CreateSolidColorBrush(ColPlayer);
        _bOther    = rt.CreateSolidColorBrush(ColOther);
        _bText     = rt.CreateSolidColorBrush(ColText);
        _bPanel    = rt.CreateSolidColorBrush(ColPanel);
        _bLandmark = rt.CreateSolidColorBrush(ColLandmark);
        _bPath     = rt.CreateSolidColorBrush(PathPalette[0]);
        _bStyle    = rt.CreateSolidColorBrush(ColText);
        _tf = _window.CreateTextFormat("Consolas", 12f);
        _atlasIcons = new AtlasIconCache();   // decode embedded atlas content PNGs once (#5)
        _ready = true;
    }

    public void Render(RenderContext ctx)
    {
        if (!_window.IsValid) return;
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(new Color4(0f, 0f, 0f, 0f));
        rt.TextAntialiasMode = 0;
        try
        {
            // Draw nothing unless PoE2 is the foreground window — so the overlay never shows
            // over other apps when you alt-tab. (The cleared frame above hides prior content.)
            if (ctx.Active && ctx.InGame && ctx.AtlasOpen)
            {
                // The Atlas screen is open: draw the highlight rings + off-screen arrows and the F10 route,
                // never the world radar/minimap (meaningless over the atlas).
                DrawAtlasRoute(rt, ctx);                   // F10 route line + START/END markers (under the rings)
                DrawAtlas(rt, ctx);                        // tracked-map rings + off-screen arrows
                _legendRowRects.Clear();
            }
            else if (ctx.Active && ctx.InGame)
            {
                DrawNameplates(rt, ctx);                   // world-space HP bars over hostile mobs
                DrawItemLabels(rt, ctx);                   // priced unique drops over their loot icons
                if (ctx.Map.IsVisible)
                    DrawMap(rt, ctx);                      // terrain + dots + on-map path polylines
                else
                    DrawPathsWorld(rt, ctx);               // ground waypoints + lines when the map is closed

                // The navigation-menu widget is ALWAYS interactive in-game (map open or not). It
                // (re)builds _legendRowRects, so it must run last; nothing else touches those rects now.
                DrawNavMenu(rt, ctx);
            }
            else
            {
                _legendRowRects.Clear(); // not active/in-game: no stale click rects
            }

            // Rune-crafting reward prices: screen-space labels drawn on top of whatever's below (radar
            // or atlas), gated only on the panel being open (RuneLabels populated). No-op otherwise.
            if (ctx.Active && ctx.InGame)
            {
                DrawRuneforge(rt, ctx);
                DrawRitualRewards(rt, ctx);            // value chips on the ritual tribute-shop tiles (screen-space)
                DrawLootTags(rt, ctx);                 // value chips on the game's own loot tags (screen-space)
                if (ctx.PriceCheck is null) DrawHoverPrice(rt, ctx); // price chip under the tooltip (the panel supersedes it)
                DrawMonolithPanel(rt, ctx);            // nearby-monolith reward list (screen-space)
                DrawCurrencyExchange(rt, ctx);         // currency-exchange order-book depth panel (top-right, screen-space)
                DrawTradePanel(rt, ctx);               // whisper-driven trade cards with invite/trade/thanks buttons
                DrawPriceCheck(rt, ctx);               // hotkey price check: estimate vs live listings
                DrawToast(rt, ctx);                    // transient hotkey feedback
                if (ctx.InsMenu is null) DrawStatusStrip(rt, ctx);
                DrawInsMenu(rt, ctx);                  // INSERT menu — last so it sits on top and its click rects win
            }
        }
        finally { rt.EndDraw(); }
        _window.Present();
    }

    private static readonly Color4 ColItemHi   = new(1.00f, 0.80f, 0.20f, 1.0f);

    private static readonly Color4 ColItemText = new(0.92f, 0.92f, 0.92f, 1.0f);

    private static readonly Color4 ColDanger   = new(1.00f, 0.38f, 0.32f, 1.0f);

    private static readonly Color4 ColExchangeRec = ColorFromU(0xFF66E066u);

    private static readonly Color4 ColExchangeVol = ColorFromU(0xFFE6C84Du);

    private static readonly Color4 ColExchangeDim = ColorFromU(0xFF9A9488u);

    private static readonly Color4 ColExchangeFill = ColorFromU(0xFF8FE3FFu);

    private static readonly Color4 ColExchangeGold = ColorFromU(0xFFC8A24Du);

    private static readonly Color4 ColExchangeBar = ColorFromU(0xFF6E5A30u);

    private static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, a);

    public void Dispose()
    {
        _bPlayer?.Dispose(); _bOther?.Dispose(); _bText?.Dispose(); _bPanel?.Dispose(); _bLandmark?.Dispose();
        _bPath?.Dispose(); _bStyle?.Dispose();
        foreach (var geo in _geoCache.Values.Where(g => g is not null).Distinct()) geo!.Dispose();
        _geoCache.Clear();
        _tf?.Dispose();
        DisposeUi();
        _bUi?.Dispose();
        _terrain?.Dispose();
        _terrainLayer.Dispose();
        _atlasIcons?.Dispose();
    }
}
