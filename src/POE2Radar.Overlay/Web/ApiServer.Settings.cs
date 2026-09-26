using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

public sealed partial class ApiServer
{
    /// <summary>
    /// The settings the dashboard may read AND write. Covers radar/visual options plus auto-flask
    /// tuning (thresholds, cooldowns, keys). Arm flags (autoFlaskEnabled, the buff keeper's Enabled) are
    /// omitted — hotkeys / INSERT menu only. All writes are loopback-Host-gated (see Handle). The
    /// API port is read-only here (changing it needs a restart). This object also doubles as the GET payload.
    /// </summary>
    private object ReadSettings() => new
    {
        hideJunk = _settings.HideJunk,
        showPath = _settings.ShowPath,
        alwaysShowOverlay = _settings.AlwaysShowOverlay,
        reduceMotion = _settings.ReduceMotion,
        useCuratedLandmarks = _settings.UseCuratedLandmarks,
        landmarkClusterGap = _settings.LandmarkClusterGap,
        showMonsters = _settings.ShowMonsters,
        showTerrain = _settings.ShowTerrain,
        showPlayerBlip = _settings.ShowPlayerBlip,
        fpsCap = _settings.FpsCap,
        hpBarNormal = _settings.HpBarNormal,
        hpBarMagic = _settings.HpBarMagic,
        hpBarRare = _settings.HpBarRare,
        hpBarUnique = _settings.HpBarUnique,
        scaleMul = _settings.ScaleMul,
        offX = _settings.OffX,
        offY = _settings.OffY,
        lifeFlaskMode = _settings.LifeFlaskMode,
        lifeThresholdPct = _settings.LifeThresholdPct,
        esThresholdPct = _settings.EsThresholdPct,
        manaThresholdPct = _settings.ManaThresholdPct,
        lifeCooldownMs = _settings.LifeCooldownMs,
        manaCooldownMs = _settings.ManaCooldownMs,
        lifeKey = _settings.LifeKey,
        manaKey = _settings.ManaKey,
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        styles = _settings.Styles,   // per-item icon shapes/colors/sizes + mechanic overrides
        hpBars = _settings.HpBars,   // monster HP-bar geometry (width/height/offset)
        terrain = _settings.Terrain, // walkable-terrain bitmap colors/transparency
        groundItems = _settings.GroundItems, // ground-item value overlay (enabled / highlight threshold / league)
        hoverPrice = _settings.HoverPrice, // hover-tooltip price chip (enabled / highlight threshold)
        mapCheck = _settings.MapCheck,     // waystone dangerous-mod list
        // Buff keeper WITHOUT its arm bit (hotkey / INSERT menu only).
        buffKeeper = new { toggleHotkey = _settings.BuffKeeper.ToggleHotkey, globalGapMs = _settings.BuffKeeper.GlobalGapMs, rules = _settings.BuffKeeper.Rules },
        commands = _settings.Commands,     // chat macros, bookmarks, inspect hotkeys
        trade = _settings.Trade,           // trade panel + tracker options
        monoliths = _settings.Monoliths, // runeshape-monolith (expedition) reward overlay + value gate
        currencyExchange = _settings.CurrencyExchange, // currency-exchange order-book depth panel (enabled / max rows)
        // Atlas declutter / content-icon / route-chevron options + colour groups (#3/#4/#5/#7).
        atlasHideCompleted = _settings.AtlasHideCompleted,
        atlasHideAccessible = _settings.AtlasHideAccessible,
        atlasShowContentIcons = _settings.AtlasShowContentIcons,
        atlasContentIconSize = _settings.AtlasContentIconSize,
        atlasRouteArrowSpacing = _settings.AtlasRouteArrowSpacing,
        atlasGroups = _settings.AtlasGroups,
    };

    /// <summary>Apply only whitelisted radar/visual keys from a posted JSON object; persists on change.</summary>
    private string[] ApplySettings(string body)
    {
        var applied = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return applied.ToArray();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return applied.ToArray();

        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "hideJunk" when TryBool(p.Value, out var b): _settings.HideJunk = b; applied.Add(p.Name); break;
                case "showPath" when TryBool(p.Value, out var b): _settings.ShowPath = b; applied.Add(p.Name); break;
                case "alwaysShowOverlay" when TryBool(p.Value, out var b): _settings.AlwaysShowOverlay = b; applied.Add(p.Name); break;
                case "reduceMotion" when TryBool(p.Value, out var b): _settings.ReduceMotion = b; applied.Add(p.Name); break;
                case "useCuratedLandmarks" when TryBool(p.Value, out var b): _settings.UseCuratedLandmarks = b; applied.Add(p.Name); break;
                case "landmarkClusterGap" when TryInt(p.Value, out var n): _settings.LandmarkClusterGap = Math.Clamp(n, 0, 64); applied.Add(p.Name); break;
                case "scaleMul" when TryFloat(p.Value, out var f): _settings.ScaleMul = f; applied.Add(p.Name); break;
                case "offX" when TryFloat(p.Value, out var f): _settings.OffX = f; applied.Add(p.Name); break;
                case "offY" when TryFloat(p.Value, out var f): _settings.OffY = f; applied.Add(p.Name); break;
                case "showMonsters" when TryBool(p.Value, out var b): _settings.ShowMonsters = b; applied.Add(p.Name); break;
                case "showTerrain" when TryBool(p.Value, out var b): _settings.ShowTerrain = b; applied.Add(p.Name); break;
                case "showPlayerBlip" when TryBool(p.Value, out var b): _settings.ShowPlayerBlip = b; applied.Add(p.Name); break;
                case "fpsCap" when TryInt(p.Value, out var n): _settings.FpsCap = Math.Clamp(n, 15, 360); applied.Add(p.Name); break;
                case "hpBarNormal" when TryBool(p.Value, out var b): _settings.HpBarNormal = b; applied.Add(p.Name); break;
                case "hpBarMagic" when TryBool(p.Value, out var b): _settings.HpBarMagic = b; applied.Add(p.Name); break;
                case "hpBarRare" when TryBool(p.Value, out var b): _settings.HpBarRare = b; applied.Add(p.Name); break;
                case "hpBarUnique" when TryBool(p.Value, out var b): _settings.HpBarUnique = b; applied.Add(p.Name); break;
                case "lifeFlaskMode" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } m
                    && (m is "Health" or "EnergyShield" or "Either"): _settings.LifeFlaskMode = m; applied.Add(p.Name); break;
                case "lifeThresholdPct" when TryFloat(p.Value, out var f): _settings.LifeThresholdPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "esThresholdPct" when TryFloat(p.Value, out var f): _settings.EsThresholdPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "manaThresholdPct" when TryFloat(p.Value, out var f): _settings.ManaThresholdPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "lifeCooldownMs" when TryInt(p.Value, out var n): _settings.LifeCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "manaCooldownMs" when TryInt(p.Value, out var n): _settings.ManaCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "lifeKey" when TryInt(p.Value, out var n): _settings.LifeKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "manaKey" when TryInt(p.Value, out var n): _settings.ManaKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                // Arm bits (AutoFlaskEnabled, BuffKeeper.Enabled) are hotkey/INSERT-menu only — never settings POST keys.
                // Atlas declutter + content-icon + route-chevron options (#3/#4/#5).
                case "atlasHideCompleted" when TryBool(p.Value, out var b): _settings.AtlasHideCompleted = b; applied.Add(p.Name); break;
                case "atlasHideAccessible" when TryBool(p.Value, out var b): _settings.AtlasHideAccessible = b; applied.Add(p.Name); break;
                case "atlasShowContentIcons" when TryBool(p.Value, out var b): _settings.AtlasShowContentIcons = b; applied.Add(p.Name); break;
                case "atlasContentIconSize" when TryFloat(p.Value, out var f): _settings.AtlasContentIconSize = Math.Clamp(f, 12f, 64f); applied.Add(p.Name); break;
                case "atlasRouteArrowSpacing" when TryFloat(p.Value, out var f): _settings.AtlasRouteArrowSpacing = Math.Clamp(f, 1.5f, 18f); applied.Add(p.Name); break;
                // Whole-object writes (the dashboard re-POSTs the full sub-object on edit). Parsed,
                // sanitized/clamped, then swapped in. A malformed sub-object is skipped, not fatal.
                case "styles" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseStyles(p.Value, out var styles)) { _settings.Styles = styles; applied.Add(p.Name); }
                    break;
                case "hpBars" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseHpBars(p.Value, out var hpBars)) { _settings.HpBars = hpBars; applied.Add(p.Name); }
                    break;
                case "terrain" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseTerrain(p.Value, out var terrain)) { _settings.Terrain = terrain; applied.Add(p.Name); }
                    break;
                case "groundItems" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseGroundItems(p.Value, out var gi)) { _settings.GroundItems = gi; applied.Add(p.Name); }
                    break;
                case "hoverPrice" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseHoverPrice(p.Value, out var hp)) { _settings.HoverPrice = hp; applied.Add(p.Name); }
                    break;
                case "monoliths" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseMonoliths(p.Value, out var mono)) { _settings.Monoliths = mono; applied.Add(p.Name); }
                    break;
                case "currencyExchange" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseCurrencyExchange(p.Value, out var ce)) { _settings.CurrencyExchange = ce; applied.Add(p.Name); }
                    break;
                case "mapCheck" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseMapCheck(p.Value, out var mc)) { _settings.MapCheck = mc; applied.Add(p.Name); }
                    break;
                case "buffKeeper" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseBuffKeeper(p.Value, _settings.BuffKeeper.Enabled, out var bk)) { _settings.BuffKeeper = bk; applied.Add(p.Name); }
                    break;
                case "commands" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseCommands(p.Value, _settings.BuffKeeper.ToggleHotkey, out var cmd, _settings.HoverPrice.PriceCheckHotkey)) { _settings.Commands = cmd; applied.Add(p.Name); }
                    break;
                case "trade" when p.Value.ValueKind == JsonValueKind.Object:
                    if (TryParseTrade(p.Value, out var tr)) { _settings.Trade = tr; applied.Add(p.Name); }
                    break;
                // Atlas colour groups (#7): the dashboard re-POSTs the full array on edit.
                case "atlasGroups" when p.Value.ValueKind == JsonValueKind.Array:
                    if (TryParseAtlasGroups(p.Value, out var grps)) { _settings.AtlasGroups = grps; _settings.AtlasGroupsSeeded = true; applied.Add(p.Name); }
                    break;
                // Anything else (apiPort, unknown keys) is ignored by design.
            }
        }

        if (applied.Count > 0) _settings.Save();
        return applied.ToArray();
    }

    /// <summary>Parse the atlas colour-groups array (#7) the dashboard re-POSTs on edit: each entry is
    /// <c>{ name, color (#RRGGBB), maps[] }</c>. Sanitized + capped; a malformed entry is skipped.</summary>
    private static bool TryParseAtlasGroups(JsonElement el, out List<AtlasMapGroup> groups)
    {
        groups = new List<AtlasMapGroup>();
        try
        {
            foreach (var g in el.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var name = g.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? (nv.GetString() ?? "").Trim() : "";
                if (name.Length > 64) name = name[..64];
                var color = g.TryGetProperty("color", out var cv) && cv.ValueKind == JsonValueKind.String ? (cv.GetString() ?? "") : "";
                if (!HexColor.IsMatch(color)) color = "#E0B341";
                var maps = new List<string>();
                if (g.TryGetProperty("maps", out var mv) && mv.ValueKind == JsonValueKind.Array)
                    foreach (var m in mv.EnumerateArray())
                        if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } ms && maps.Count < 200)
                            maps.Add(ms.Trim());
                groups.Add(new AtlasMapGroup { Name = name, Color = color.ToUpperInvariant(), Maps = maps });
                if (groups.Count >= 64) break;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="RadarStyles"/> from posted JSON. Returns false
    /// (and leaves settings untouched) if the JSON can't be parsed.</summary>
    private static bool TryParseStyles(JsonElement el, out RadarStyles styles)
    {
        styles = new RadarStyles();
        try
        {
            var parsed = JsonSerializer.Deserialize<RadarStyles>(el.GetRawText(), Json);
            if (parsed == null) return false;
            foreach (var ic in new[] { parsed.MonsterNormal, parsed.MonsterMagic, parsed.MonsterRare, parsed.MonsterUnique,
                                       parsed.Player, parsed.Npc, parsed.ChestRare, parsed.ChestUnique, parsed.Transition, parsed.Poi, parsed.Landmark })
                SanitizeIcon(ic);
            parsed.Mechanics ??= new List<MechanicStyle>();
            if (parsed.Mechanics.Count > 24) parsed.Mechanics = parsed.Mechanics.Take(24).ToList();
            foreach (var m in parsed.Mechanics)
            {
                m.Shape = IconLibrary.Canonical(m.Shape) ?? "Circle";
                m.Color = m.Color != null && HexColor.IsMatch(m.Color) ? m.Color.ToUpperInvariant() : "#FFFFFF";
                m.Opacity = Math.Clamp(m.Opacity, 0f, 1f);
                m.Size = Math.Clamp(m.Size, 0.5f, 40f);
                m.Name = (m.Name ?? "").Trim();
                if (m.Name.Length > 40) m.Name = m.Name[..40];
                m.Match ??= new List<string>();
                m.Match = m.Match.Where(x => !string.IsNullOrWhiteSpace(x))
                                 .Select(x => x.Trim() is var t && t.Length > 64 ? t[..64] : x.Trim())
                                 .Take(8).ToList();
                // Keep only valid EntityCategory names (canonicalized), deduped. Empty = applies to all.
                m.Categories ??= new List<string>();
                m.Categories = m.Categories
                    .Select(x => Enum.TryParse<Poe2Live.EntityCategory>(x, ignoreCase: true, out var c) ? c.ToString() : null)
                    .Where(x => x != null).Distinct().ToList()!;
            }
            styles = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void SanitizeIcon(IconStyle s)
    {
        s.Shape = IconLibrary.Canonical(s.Shape) ?? "Circle";
        s.Color = s.Color != null && HexColor.IsMatch(s.Color) ? s.Color.ToUpperInvariant() : "#FFFFFF";
        s.Opacity = Math.Clamp(s.Opacity, 0f, 1f);
        s.Size = Math.Clamp(s.Size, 0.5f, 40f);
    }

    /// <summary>Return <paramref name="c"/> upper-cased if it's a valid #RRGGBB, else <paramref name="fallback"/>.</summary>
    private static string ValidHexOr(string? c, string fallback)
        => c != null && HexColor.IsMatch(c) ? c.ToUpperInvariant() : fallback;

    /// <summary>Deserialize + clamp a full <see cref="HpBarSettings"/> from posted JSON.</summary>
    private static bool TryParseHpBars(JsonElement el, out HpBarSettings hp)
    {
        hp = new HpBarSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<HpBarSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Height = Math.Clamp(parsed.Height, 1f, 30f);
            parsed.OffsetX = Math.Clamp(parsed.OffsetX, -200f, 200f);
            parsed.OffsetY = Math.Clamp(parsed.OffsetY, -200f, 200f);
            parsed.WidthNormal = Math.Clamp(parsed.WidthNormal, 4f, 400f);
            parsed.WidthMagic = Math.Clamp(parsed.WidthMagic, 4f, 400f);
            parsed.WidthRare = Math.Clamp(parsed.WidthRare, 4f, 400f);
            parsed.WidthUnique = Math.Clamp(parsed.WidthUnique, 4f, 400f);
            parsed.BorderNormal = Math.Clamp(parsed.BorderNormal, 0f, 20f);
            parsed.BorderMagic = Math.Clamp(parsed.BorderMagic, 0f, 20f);
            parsed.BorderRare = Math.Clamp(parsed.BorderRare, 0f, 20f);
            parsed.BorderUnique = Math.Clamp(parsed.BorderUnique, 0f, 20f);
            parsed.BorderColorNormal = ValidHexOr(parsed.BorderColorNormal, "#FF3333");
            parsed.BorderColorMagic = ValidHexOr(parsed.BorderColorMagic, "#73A6FF");
            parsed.BorderColorRare = ValidHexOr(parsed.BorderColorRare, "#FFD926");
            parsed.BorderColorUnique = ValidHexOr(parsed.BorderColorUnique, "#FF7300");
            hp = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="TerrainSettings"/> from posted JSON. Colors are
    /// validated as #RRGGBB (falling back to the defaults) and opacities clamped to 0..1.</summary>
    private static bool TryParseTerrain(JsonElement el, out TerrainSettings t)
    {
        t = new TerrainSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<TerrainSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.InteriorColor = parsed.InteriorColor != null && HexColor.IsMatch(parsed.InteriorColor) ? parsed.InteriorColor.ToUpperInvariant() : "#506482";
            parsed.EdgeColor = parsed.EdgeColor != null && HexColor.IsMatch(parsed.EdgeColor) ? parsed.EdgeColor.ToUpperInvariant() : "#3CDCFF";
            parsed.InteriorOpacity = Math.Clamp(parsed.InteriorOpacity, 0f, 1f);
            parsed.EdgeOpacity = Math.Clamp(parsed.EdgeOpacity, 0f, 1f);
            t = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseMonoliths(JsonElement el, out MonolithSettings m)
    {
        m = new MonolithSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<MonolithSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.HighlightMinEx = Math.Max(0, parsed.HighlightMinEx);
            parsed.MinRewardEx = Math.Max(0, parsed.MinRewardEx);
            parsed.MinValueEx = Math.Max(0, parsed.MinValueEx);
            parsed.PanelMaxDistance = Math.Max(0, parsed.PanelMaxDistance);
            m = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseGroundItems(JsonElement el, out GroundItemSettings g)
    {
        g = new GroundItemSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<GroundItemSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.HighlightMinEx = Math.Max(0, parsed.HighlightMinEx);
            parsed.UniqueMinEx = Math.Max(0, parsed.UniqueMinEx);
            parsed.CurrencyMinEx = Math.Max(0, parsed.CurrencyMinEx);
            parsed.OtherMinEx = Math.Max(0, parsed.OtherMinEx);
            parsed.MinQuantity = Math.Clamp(parsed.MinQuantity, 0, 100000);
            parsed.League = (parsed.League ?? "").Trim();
            parsed.Categories = (parsed.Categories ?? new())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
            g = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : t;

    /// <summary>A hotkey string normalized to its canonical form, or "" when it doesn't parse or collides with
    /// one of the overlay's own keys / <paramref name="alsoReserved"/>.</summary>
    internal static string CleanHotkey(string? text, params string?[] alsoReserved)
    {
        if (!Input.Hotkey.TryParse(text, out var hk) || !hk.IsBound || CommandSettings.IsReserved(hk)) return "";
        foreach (var r in alsoReserved)
            if (r is not null && Input.Hotkey.TryParse(r, out var other) && other == hk) return "";
        return hk.ToString();
    }

    internal static bool TryParseMapCheck(JsonElement el, out MapCheckSettings m)
    {
        m = new MapCheckSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<MapCheckSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Dangerous = (parsed.Dangerous ?? new()).Select(d => Clip(d, 200)).Where(d => d.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            m = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Buff-keeper rules + toggle hotkey. <paramref name="armed"/> is carried over from the live
    /// settings: the arm bit is never taken from HTTP.</summary>
    internal static bool TryParseBuffKeeper(JsonElement el, bool armed, out BuffKeeperSettings b)
    {
        b = new BuffKeeperSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<BuffKeeperSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Enabled = armed;
            parsed.ToggleHotkey = CleanHotkey(parsed.ToggleHotkey) is { Length: > 0 } hk ? hk : "F4";
            parsed.GlobalGapMs = Math.Clamp(parsed.GlobalGapMs, Input.BuffKeeper.MinGlobalGapMs, 5000);
            parsed.Rules = (parsed.Rules ?? new()).Take(16).ToList();
            foreach (var r in parsed.Rules)
            {
                r.Name = Clip(r.Name, 60);
                r.BuffName = Clip(r.BuffName, 200);
                r.Key = r.Key is >= 1 and <= 255 ? r.Key : 0;
                r.Trigger = r.Trigger is Input.BuffKeeper.TriggerMissing or Input.BuffKeeper.TriggerExpiring or Input.BuffKeeper.TriggerInterval
                    ? r.Trigger : Input.BuffKeeper.TriggerMissing;
                r.RefreshBelowSec = Math.Clamp(r.RefreshBelowSec, 0f, 600f);
                r.IntervalMs = Math.Clamp(r.IntervalMs, Input.BuffKeeper.MinIntervalMs, 3_600_000);
                r.MinGapMs = Math.Clamp(r.MinGapMs, Input.BuffKeeper.MinRuleGapMs, 60_000);
                r.HostileRange = Math.Clamp(r.HostileRange, 1f, 300f);
                r.MinCharges = Math.Clamp(r.MinCharges, 0, 100);
            }
            b = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Chat commands / bookmarks / inspect hotkeys. Hotkeys that don't parse, or that collide with the
    /// overlay's own keys or the buff-keeper toggle, are cleared (the entry stays, unbound). Bookmark URLs must
    /// be absolute http(s) — anything else is dropped.</summary>
    internal static bool TryParseCommands(JsonElement el, string buffToggle, out CommandSettings c, string? priceCheck = null)
    {
        c = new CommandSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<CommandSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.Commands = (parsed.Commands ?? new()).Take(40).ToList();
            foreach (var cmd in parsed.Commands)
            {
                cmd.Name = Clip(cmd.Name, 60);
                cmd.Text = (cmd.Text ?? "").Length > 1000 ? cmd.Text![..1000] : cmd.Text ?? "";
                cmd.Hotkey = CleanHotkey(cmd.Hotkey, buffToggle, priceCheck);
            }
            parsed.Bookmarks = (parsed.Bookmarks ?? new()).Take(60)
                // Validate with placeholders filled so "{league}"-style templates are judged as real URLs.
                .Where(bm => Input.Bookmarks.TryExpand(bm.Url, new Input.UrlContext("x", "x", "x"), out _, out _)).ToList();
            foreach (var bm in parsed.Bookmarks)
            {
                bm.Name = Clip(bm.Name, 60);
                bm.Folder = Clip(bm.Folder, 60);
                bm.Hotkey = CleanHotkey(bm.Hotkey, buffToggle, priceCheck);
            }
            parsed.InspectWikiHotkey = CleanHotkey(parsed.InspectWikiHotkey, buffToggle, priceCheck);
            parsed.InspectDbHotkey = CleanHotkey(parsed.InspectDbHotkey, buffToggle, priceCheck);
            c = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryParseTrade(JsonElement el, out TradeSettings t)
    {
        t = new TradeSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<TradeSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.ClientLogPath = Clip(parsed.ClientLogPath, 1024);
            parsed.ThanksMessage = Clip(parsed.ThanksMessage, 200);
            parsed.BusyMessage = Clip(parsed.BusyMessage, 200);
            parsed.SoldMessage = Clip(parsed.SoldMessage, 200);
            parsed.StillInterestedMessage = Clip(parsed.StillInterestedMessage, 200);
            parsed.ExpireMinutes = Math.Clamp(parsed.ExpireMinutes, 1, 24 * 60);
            parsed.MaxSessions = Math.Clamp(parsed.MaxSessions, 1, 50);
            parsed.PanelX = Math.Clamp(parsed.PanelX, 0f, 0.95f);
            parsed.PanelY = Math.Clamp(parsed.PanelY, 0f, 0.95f);
            t = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="HoverPriceSettings"/> from posted JSON.
    /// Mirrors <see cref="TryParseGroundItems"/>. Returns false (settings untouched) on a parse failure.</summary>
    internal static bool TryParseHoverPrice(JsonElement el, out HoverPriceSettings h)
    {
        h = new HoverPriceSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<HoverPriceSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.HighlightMinEx = Math.Max(0, parsed.HighlightMinEx);
            // The price-check key may be any combo that isn't one of the overlay's fixed keys (Ctrl+D itself is
            // its default, so it is allowed here).
            parsed.PriceCheckHotkey = Input.Hotkey.TryParse(parsed.PriceCheckHotkey, out var pck) && pck.IsBound
                && (!CommandSettings.IsReserved(pck) || pck == new Input.Hotkey('D', true, false, false))
                ? pck.ToString() : "Ctrl+D";
            h = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Deserialize + sanitize a full <see cref="CurrencyExchangeSettings"/> from posted JSON.
    /// Mirrors <see cref="TryParseGroundItems"/>. Returns false (settings untouched) on a parse failure.</summary>
    private static bool TryParseCurrencyExchange(JsonElement el, out CurrencyExchangeSettings c)
    {
        c = new CurrencyExchangeSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<CurrencyExchangeSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.MaxRows = Math.Clamp(parsed.MaxRows, 1, 64);
            c = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SanitizeColor(string? c)
        => c != null && HexColor.IsMatch(c) ? c.ToUpperInvariant() : "#FFFFFF";

    private static string SanitizeShape(string? s)
        => IconLibrary.Canonical(s ?? "") ?? "Diamond";

    private static float SanitizeSize(JsonElement o, float fallback)
        => o.TryGetProperty("size", out var v) && TryFloat(v, out var f) ? Math.Clamp(f, 0.5f, 40f) : fallback;

    /// <summary>Clamp/validate a posted rule in place: known icon shape, #RRGGBB color, 0..1 opacity,
    /// size 0.5..40, valid category names, valid condition enums (else null = "any"), trimmed text.</summary>
    private static void SanitizeRule(DisplayRule r)
    {
        r.Name = (r.Name ?? "").Trim();
        if (r.Name.Length > 60) r.Name = r.Name[..60];
        r.Categories = (r.Categories ?? new())
            .Select(c => CategoryNames.FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c != null).Select(c => c!).Distinct().ToList();
        r.Match = (r.Match ?? new()).Select(m => (m ?? "").Trim()).Where(m => m.Length > 0).Take(32).ToList();
        r.Rarity    = OneOf(r.Rarity, "Normal", "Magic", "Rare", "Unique");
        r.Reaction  = OneOf(r.Reaction, "Hostile", "Friendly");
        r.Life      = OneOf(r.Life, "Alive", "Dead");
        r.Chest     = OneOf(r.Chest, "Opened", "Unopened");
        r.Poi       = OneOf(r.Poi, "Yes", "No");
        r.Encounter = OneOf(r.Encounter, "Active", "Complete");
        r.Shape = SanitizeShape(r.Shape);
        r.Color = SanitizeColor(r.Color);
        r.Opacity = Math.Clamp(r.Opacity, 0f, 1f);
        r.Size = Math.Clamp(r.Size, 0.5f, 40f);
        r.Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim();
        if (r.Label is { Length: > 60 }) r.Label = r.Label[..60];
    }

    private static string? OneOf(string? v, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        foreach (var a in allowed) if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}
