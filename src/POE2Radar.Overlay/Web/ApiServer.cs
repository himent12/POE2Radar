using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Tiny read-only HTTP API for live troubleshooting (the PoE2 stand-in for POEMCP). Serves the
/// latest <see cref="RadarState"/> published by <see cref="RadarApp"/> each world tick.
///
/// Endpoints (localhost:7777, read-only — no CORS header, so only the same-origin dashboard can read them):
///   GET /                         — the web dashboard (see <see cref="DashboardHtml"/>)
///   GET /health                   — liveness probe
///   GET /state                    — player, area, map visibility, entity counts by category
///   GET /entities                 — all entities (id, category, metadata, pos, hp, dist)
///       ?category=Monster         — filter by category (case-insensitive)
///       &amp;alive=true               — only entities with HP &gt; 0
///       &amp;radius=80                — only within N grid units of the player
///       &amp;limit=50                 — cap results (default 500)
///   GET  /api/icons               — the icon library (name + viewBox + paths) for the dashboard pickers
///   GET  /api/settings            — current radar/visual settings (+ read-only flask mirror)
///   POST /api/settings            — write whitelisted radar/visual settings only (flags + calibration);
///                                   loopback-Host-gated; never exposes flask/automation writes
///   GET  /api/nav                 — current navigation-target selection (ids + color slots)
///   POST /api/nav                 — toggle/clear a navigation target (draw-only; never sends input to
///                                   the game); loopback-Host-gated like POST /api/settings
///   GET  /api/hidden              — user cull patterns (entities matching these are hidden everywhere)
///   POST /api/hidden              — add/remove/clear a cull pattern ({add|remove|clear}); loopback-Host-gated
///   GET  /api/watched             — user highlight rules (pattern/label/color/shape/size/enabled)
///   POST /api/watched             — add/update/remove a highlight rule; loopback-Host-gated
///   GET  /api/zone                — static zone reference: friendly name, act/level, flags, leveling notes
///   GET  /api/landmark-patterns   — user tile-path patterns surfaced as landmarks (pattern/label/enabled)
///   POST /api/landmark-patterns   — add/update/remove a landmark pattern; loopback-Host-gated
/// </summary>
public sealed partial class ApiServer : IDisposable
{
    private readonly HttpListener _listener = new();

    private readonly Func<RadarState> _state;

    private readonly RadarSettings _settings;

    // Navigation selection controller, supplied by RadarApp. These only mutate the draw-only path
    // selection — they NEVER send input to the game.
    private readonly Func<IReadOnlyList<(string Id, int Slot)>> _navGet;

    private readonly Action<string> _navToggle;

    private readonly Action _navClear;

    private readonly HiddenEntities _hidden;

    private readonly DisplayRules _displayRules;

    private readonly LandmarkStore _landmarkStore;

    private readonly Func<IReadOnlyList<string>> _tiles;

    // Persistent catalog of every monster affix-mod id ever seen — the vocabulary the rule editor
    // browses to author a Mods matcher. Read-only provider supplied by RadarApp.
    private readonly Func<IReadOnlyList<string>> _knownMods;

    // PriceBook status provider ({league, count, status, exPerDivine, exPerChaos}) for the dashboard.
    private readonly Func<object>? _prices;

    // Atlas map-data provider (catalog + current-region map set). Read-only, computed on demand (it
    // scans memory + caches), returns a JSON-ready object. Null when atlas reading is unavailable.
    private readonly Func<object>? _atlas;

    // Atlas node selection (element addresses) to highlight in-game; draw-only, loopback-gated.
    private readonly Action<IReadOnlyList<long>>? _atlasSelect;

    // Atlas highlight rules (tag + colour + track/arrow) — only matching nodes draw in-game; loopback-gated.
    private readonly Action<IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)>>? _atlasHighlight;

    // Version/update info provider ({current, latest, updateAvailable, url}) for the dashboard banner.
    private readonly Func<object>? _version;

    /// <summary>Live buffs + per-rule buff-keeper notes (GET /api/buffs). Read-only.</summary>
    public Func<object>? BuffsProvider { get; init; }

    /// <summary>Trade sessions + tracker summary + history (GET /api/trade).</summary>
    public Func<object>? TradeProvider { get; init; }

    /// <summary>Trade bookkeeping ops (POST /api/trade: dismiss a session, delete/clear history). Never sends
    /// chat — whispers/invites only go out from the in-game panel while PoE2 is focused.</summary>
    public Func<JsonElement, object>? TradeCommand { get; init; }

    private volatile bool _running;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ApiServer(
        Func<RadarState> state,
        RadarSettings settings,
        Func<IReadOnlyList<(string Id, int Slot)>> navGet,
        Action<string> navToggle,
        Action navClear,
        HiddenEntities hidden,
        DisplayRules displayRules,
        LandmarkStore landmarkStore,
        Func<IReadOnlyList<string>> tilesProvider,
        Func<IReadOnlyList<string>> knownModsProvider,
        Func<object>? pricesProvider = null,
        Func<object>? atlasProvider = null,
        Action<IReadOnlyList<long>>? atlasSelect = null,
        Action<IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)>>? atlasHighlight = null,
        Func<object>? versionProvider = null,
        int port = 7777)
    {
        _state = state;
        _atlas = atlasProvider;
        _atlasSelect = atlasSelect;
        _atlasHighlight = atlasHighlight;
        _version = versionProvider;
        _settings = settings;
        _navGet = navGet;
        _navToggle = navToggle;
        _navClear = navClear;
        _hidden = hidden;
        _displayRules = displayRules;
        _landmarkStore = landmarkStore;
        _tiles = tilesProvider;
        _knownMods = knownModsProvider;
        _prices = pricesProvider;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "POE2Radar.Api" };
        t.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; } // listener stopped
            try { Handle(ctx); }
            catch (Exception ex) { TryWrite(ctx, 500, JsonSerializer.Serialize(new { error = ex.Message }, Json)); }
        }
    }

    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    // Valid rule categories: the entity categories plus the pseudo-category "Tile" (matches terrain
    // tiles by path instead of an entity).
    private static readonly string[] CategoryNames = Enum.GetNames<Poe2Live.EntityCategory>().Append("Tile").ToArray();

    private static bool TryBool(JsonElement e, out bool v)
    {
        if (e.ValueKind == JsonValueKind.True) { v = true; return true; }
        if (e.ValueKind == JsonValueKind.False) { v = false; return true; }
        v = false; return false;
    }

    private static bool TryFloat(JsonElement e, out float v)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out v)) return true;
        v = 0f; return false;
    }

    private static bool TryInt(JsonElement e, out int v)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v)) return true;
        if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out v)) return true;
        v = 0; return false;
    }

    private static bool IsLoopbackHost(HttpListenerRequest req)
    {
        var host = req.UserHostName; // includes port, e.g. "localhost:7777"
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.Split(':')[0];
        return name is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private static string ReadBody(HttpListenerContext ctx)
    {
        using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        return r.ReadToEnd();
    }

    private static float Dist(System.Numerics.Vector2 a, System.Numerics.Vector2 b)
        => (a - b).Length();

    private static void Write(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        // Read-only API: no Access-Control-Allow-Origin header, so a browser on another origin
        // cannot read these responses. The dashboard is served same-origin from "/".
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void TryWrite(HttpListenerContext ctx, int status, string body)
    {
        try { Write(ctx, status, body); } catch { /* client gone */ }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
