using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

public sealed partial class ApiServer
{
    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var q = ctx.Request.QueryString;
        var s = _state();

        switch (path)
        {
            case "/":
                WriteHtml(ctx, DashboardHtml.Page);
                break;

            case "/health":
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, inGame = s.InGame }, Json));
                break;

            case "/state":
            {
                var counts = s.Entities.GroupBy(e => e.Category)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    // Character name + level intentionally omitted (privacy: this endpoint is local
                    // but unauthenticated, and screenshots/streams shouldn't leak the character).
                    s.InGame, areaCode = s.AreaCode, areaHash = s.AreaHash, areaLevel = s.AreaLevel,
                    areaName = ZoneGuide.Shared.FriendlyName(s.AreaCode),
                    areaAct = ZoneGuide.Shared.Area(s.AreaCode)?.Act ?? 0,
                    mapVisible = s.MapVisible, zoom = s.Zoom,
                    hpPct = s.HpPct, manaPct = s.ManaPct, esPct = s.EsPct, autoFlask = s.AutoFlask, flask = s.FlaskNote,
                    combatAssist = s.CombatAssist, combat = s.CombatNote,
                    questFollow = s.QuestFollow, quest = s.QuestFollowNote,
                    pathMove = s.PathMove, move = s.PathMoveNote,
                    bot = s.Bot, botNote = s.BotNote,
                    mapClear = s.MapClear, clear = s.MapClearNote,
                    player = new { x = s.Player.X, y = s.Player.Y },
                    entityCount = s.Entities.Count,
                    poiCount = s.Entities.Count(e => e.Poi),
                    landmarkCount = s.Landmarks.Count,
                    counts,
                    worldMs = s.WorldMs, renderMs = s.RenderMs, fps = s.Fps,
                    // Runeshape monoliths in the area (slot count + anchor + priced reward set) for the
                    // dashboard's Monolith Rewards card. Rewards are pre-sorted by value, server-side.
                    monoliths = (s.Monoliths ?? Array.Empty<MonolithMarker>()).Select(m => new
                    {
                        holes = m.Holes, unique = m.IsUnique, collected = m.Collected, anchor = m.AnchorName,
                        bestEx = m.BestEx, bestName = m.BestName, color = m.Color,
                        rewards = m.Rewards.Select(r => new { name = r.Name, count = r.Count, ex = r.Ex, size = r.Size, runes = r.Runes }),
                    }),
                    // Currency-exchange live order book (when the exchange panel is open) for the dashboard
                    // depth card + autonomous verification. Ratio = Get/Give; cum = running stock total.
                    currencyExchange = new
                    {
                        open = s.ExchangeOpen,
                        summary = s.ExchangeSummary,
                        haveQty = s.ExchangeHaveQty,
                        fillNote = s.ExchangeFillNote,
                        offered = (s.ExchangeOffered ?? Array.Empty<ExchangeRow>()).Select(r => new { ratio = r.Ratio, stock = r.Stock, cum = r.CumStock, rec = r.Recommended }),
                        wanted = (s.ExchangeWanted ?? Array.Empty<ExchangeRow>()).Select(r => new { ratio = r.Ratio, stock = r.Stock, cum = r.CumStock, rec = r.Recommended }),
                    },
                }, Json));
                break;
            }

            case "/api/icons":
            {
                // Read-only icon library for the dashboard's icon picker previews (name + viewBox + paths).
                var icons = IconLibrary.Ordered.Select(d => new { name = d.Name, viewBox = d.ViewBox, paths = d.Paths });
                Write(ctx, 200, JsonSerializer.Serialize(icons, Json));
                break;
            }

            case "/landmarks":
            {
                var list = s.Landmarks
                    .OrderBy(l => Dist(l.Center, s.Player))
                    .Select(l => new
                    {
                        name = l.Name, curatedName = l.CuratedName, path = l.Path, tiles = l.TileCount,
                        x = l.Center.X, y = l.Center.Y, dist = (int)Dist(l.Center, s.Player),
                    });
                Write(ctx, 200, JsonSerializer.Serialize(list, Json));
                break;
            }

            case "/entities":
            {
                var category = q["category"];
                var aliveOnly = string.Equals(q["alive"], "true", StringComparison.OrdinalIgnoreCase);
                _ = float.TryParse(q["radius"], out var radius);
                _ = int.TryParse(q["limit"], out var limit);
                if (limit <= 0) limit = 500;

                IEnumerable<Poe2Live.EntityDot> q2 = s.Entities;
                if (!string.IsNullOrEmpty(category))
                    q2 = q2.Where(e => string.Equals(e.Category.ToString(), category, StringComparison.OrdinalIgnoreCase));
                if (aliveOnly) q2 = q2.Where(e => e.HpCur > 0);
                if (radius > 0) q2 = q2.Where(e => Dist(e.Grid, s.Player) <= radius);

                var list = q2
                    .OrderBy(e => Dist(e.Grid, s.Player))
                    .Take(limit)
                    .Select(e => new
                    {
                        id = e.Id, addr = $"0x{e.Address:X}", category = e.Category.ToString(), metadata = e.Metadata,
                        name = EntityNameResolver.Shared.ResolveOrShorten(e.Metadata),
                        poi = e.Poi, iconComplete = e.IconComplete, opened = e.Opened, reaction = e.Reaction, friendly = e.IsFriendly, rarity = e.Rarity.ToString(),
                        mods = e.ModList, itemArt = e.ItemArt, itemName = e.ItemName,
                        x = e.Grid.X, y = e.Grid.Y, hpCur = e.HpCur, hpMax = e.HpMax,
                        alive = e.HpMax <= 0 || e.HpCur > 0,
                        dist = (int)Dist(e.Grid, s.Player),
                    });
                Write(ctx, 200, JsonSerializer.Serialize(list, Json));
                break;
            }

            case "/api/settings":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(ReadSettings(), Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // CSRF / DNS-rebinding guard: only honor writes whose Host header is the loopback
                    // name we bind to. A page on another origin that rebinds DNS to 127.0.0.1 still
                    // sends its own hostname in Host, so this rejects drive-by writes. (Reads are
                    // already unreadable cross-origin since we emit no Access-Control-Allow-Origin.)
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    var applied = ApplySettings(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, applied, settings = ReadSettings() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/nav":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { selected = NavSelection() }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // Same CSRF / DNS-rebinding guard as POST /api/settings: only honor writes whose
                    // Host header is our loopback name. (This is draw-only selection — it never sends
                    // input to the game — but we still gate it so a cross-origin page can't drive it.)
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyNav(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, selected = NavSelection() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/hidden":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { patterns = _hidden.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    // Same CSRF / DNS-rebinding guard as the other writes: loopback Host only.
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyHidden(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, patterns = _hidden.All }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/zone":
            {
                // Static zone reference for the current area: friendly name, act/level, flags, and
                // optional leveling notes (zone-specific, else act fallback). Read-only.
                var area = ZoneGuide.Shared.Area(s.AreaCode);
                var notes = ZoneGuide.Shared.Notes(s.AreaCode);
                Write(ctx, 200, JsonSerializer.Serialize(new
                {
                    code = s.AreaCode,
                    name = ZoneGuide.Shared.FriendlyName(s.AreaCode),
                    act = area?.Act ?? 0,
                    level = area?.Level ?? s.AreaLevel,
                    waypoint = area?.Waypoint ?? false,
                    town = area?.Town ?? false,
                    title = notes?.Title ?? "",
                    notes = notes?.Notes ?? "",
                }, Json));
                break;
            }

            case "/api/tiles":
                // Distinct terrain-tile paths in the current area — the add-rule picker browses these
                // so a Tile rule can target any tile. Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new { tiles = _tiles() }, Json));
                break;

            case "/api/mods":
                // Every monster affix-mod id ever seen (persistent catalog) — the add-rule picker browses
                // these so a Mods matcher can target any known aura/buff. Read-only.
                Write(ctx, 200, JsonSerializer.Serialize(new { mods = _knownMods() }, Json));
                break;

            case "/api/prices":
                // PriceBook status — league, loaded count, rates, last fetch (dashboard ground-item panel).
                Write(ctx, 200, JsonSerializer.Serialize(_prices?.Invoke() ?? new { loaded = false, status = "pricing unavailable" }, Json));
                break;

            case "/api/version":
                // This build's version + latest known on GitHub + download URL (for the update banner).
                Write(ctx, 200, JsonSerializer.Serialize(_version?.Invoke() ?? new { current = "?", latest = (string?)null, updateAvailable = false, url = "" }, Json));
                break;

            case "/api/atlas":
                // Inspection view of the atlas map-data we can read (catalog + current-region map set).
                // Read-only; the provider scans + caches, so the first call after entering the atlas
                // may take a moment. Returns {located:false,...} when the catalog can't be found.
                Write(ctx, 200, JsonSerializer.Serialize(_atlas?.Invoke() ?? new { located = false, note = "atlas reader unavailable" }, Json));
                break;

            case "/api/atlas-select":
            {
                // Set which atlas nodes (by element address) to highlight in-game. Draw-only; loopback-gated.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var els = new List<long>();
                try
                {
                    using var doc = JsonDocument.Parse(ReadBody(ctx));
                    if (doc.RootElement.TryGetProperty("els", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var e in arr.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var v)) els.Add(v);
                            else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) els.Add(n);
                }
                catch (JsonException) { }
                _atlasSelect?.Invoke(els);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, count = els.Count }, Json));
                break;
            }

            case "/api/atlas-highlight":
            {
                // Set the active atlas highlight rules (content tags). Only matching nodes draw in-game.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                var rules = new List<(string tag, string color, bool track, bool nav, bool arrow)>();
                try
                {
                    using var doc = JsonDocument.Parse(ReadBody(ctx));
                    // "rules":[{ "tag":"…", "color":"#RRGGBB", "track":true, "nav":false, "arrow":false }].
                    if (doc.RootElement.TryGetProperty("rules", out var rs) && rs.ValueKind == JsonValueKind.Array)
                        foreach (var r in rs.EnumerateArray())
                        {
                            var tg = r.TryGetProperty("tag", out var tv) ? tv.GetString() : null;
                            var col = r.TryGetProperty("color", out var cv) ? cv.GetString() : null;
                            var track = !r.TryGetProperty("track", out var tk) || tk.ValueKind != JsonValueKind.False; // default true
                            var nav = r.TryGetProperty("nav", out var nv) && nv.ValueKind == JsonValueKind.True;
                            var arrow = r.TryGetProperty("arrow", out var aw) && aw.ValueKind == JsonValueKind.True;
                            if (!string.IsNullOrEmpty(tg)) rules.Add((tg!, col ?? "", track, nav, arrow));
                        }
                    else if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var t in arr.EnumerateArray())
                            if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tg) rules.Add((tg, "", true, false, false));
                }
                catch (JsonException) { }
                _atlasHighlight?.Invoke(rules);
                Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, count = rules.Count }, Json));
                break;
            }

            case "/api/landmarks":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    // ?export=1 → the effective merged table as a clean JSON (for download / submission).
                    if (ctx.Request.QueryString["export"] != null)
                        Write(ctx, 200, _landmarkStore.ExportJson());
                    else
                        Write(ctx, 200, JsonSerializer.Serialize(new { entries = _landmarkStore.All() }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyLandmarks(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, entries = _landmarkStore.All() }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            case "/api/display-rules":
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    Write(ctx, 200, JsonSerializer.Serialize(new { rules = _displayRules.All }, Json));
                }
                else if (ctx.Request.HttpMethod == "POST")
                {
                    if (!IsLoopbackHost(ctx.Request))
                    {
                        Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json));
                        break;
                    }
                    ApplyDisplayRules(ReadBody(ctx));
                    Write(ctx, 200, JsonSerializer.Serialize(new { ok = true, rules = _displayRules.All }, Json));
                }
                else
                {
                    Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json));
                }
                break;
            }

            default:
                Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found", path }, Json));
                break;
        }
    }
}
