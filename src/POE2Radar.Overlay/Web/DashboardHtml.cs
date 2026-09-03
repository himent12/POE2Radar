namespace POE2Radar.Overlay.Web;

/// <summary>
/// Self-contained web dashboard served at <c>GET /</c> by <see cref="ApiServer"/>. One inlined
/// HTML/CSS/JS document — no external assets beyond Google Fonts. The Console tab reads/writes
/// radar/visual settings via <c>/api/settings</c> (the only writes it makes — flags + calibration,
/// never flask/automation); the Filters tab manages watched/hidden lists via <c>/api/watched</c> /
/// <c>/api/hidden</c>; the Dashboard tab polls the same-origin read endpoints (<c>/state</c>,
/// <c>/entities</c>, <c>/landmarks</c>, <c>/api/nav</c>).
/// </summary>
internal static partial class DashboardHtml
{
    /// <summary>The whole inlined document: head + body + script (concatenated at compile time).</summary>
    public const string Page = Head + Body + Script + Script2;
}
