namespace POE2Radar.Overlay.Web;

/// <summary>
/// Self-contained web dashboard served at <c>GET /</c> by <see cref="ApiServer"/>. One inlined
/// HTML/CSS/JS document — no external assets (system fonts, inline SVG). A fixed left nav rail routes
/// (by URL hash, e.g. <c>#trade</c>, <c>#radar/landmarks</c>) between Overview, Trade, Macros, Radar,
/// Atlas, Item Value and Settings. Reads the same-origin endpoints (<c>/state</c>, <c>/api/trade</c>,
/// <c>/api/buffs</c>, <c>/api/settings</c>, …) — each page polls only what it shows — and writes settings
/// per key via <c>POST /api/settings</c>. Arm bits (auto-flask, buff keeper) are never written from here.
/// </summary>
internal static partial class DashboardHtml
{
    /// <summary>The whole inlined document (concatenated at compile time).</summary>
    public const string Page = Head + Body + Script + Script2 + Script3 + Script4;
}
