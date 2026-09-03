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
    /// tuning (thresholds, cooldowns, keys) and bot-profile tunables (combat range/skills, move
    /// keys/CDs). Arm flags (autoFlaskEnabled, combatAssistEnabled, questFollowEnabled, moveEnabled,
    /// botEnabled, mapClearEnabled) are omitted — F-keys only. All writes are loopback-Host-gated (see Handle). The
    /// API port is read-only here (changing it needs a restart). This object also doubles as the GET payload.
    /// </summary>
    private object ReadSettings() => new
    {
        hideJunk = _settings.HideJunk,
        showPath = _settings.ShowPath,
        alwaysShowOverlay = _settings.AlwaysShowOverlay,
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
        combatRange = _settings.CombatRange,
        combatEngageRange = _settings.CombatEngageRange,
        combatStallMs = _settings.CombatStallMs,
        autoRespawn = _settings.AutoRespawn,
        respawnKey = _settings.RespawnKey,
        respawnDelayMs = _settings.RespawnDelayMs,
        combatDodgeKey = _settings.CombatDodgeKey,
        combatIgnoreMs = _settings.CombatIgnoreMs,
        combatFleeHpPct = _settings.CombatFleeHpPct,
        combatFleeRecoverPct = _settings.CombatFleeRecoverPct,
        combatFleeDistance = _settings.CombatFleeDistance,
        combatTargetMode = _settings.CombatTargetMode,
        combatRotationMode = _settings.CombatRotationMode,
        combatKeepDistance = _settings.CombatKeepDistance,
        combatSkills = _settings.CombatSkills,
        questUseKey = _settings.QuestUseKey,
        questUseRadius = _settings.QuestUseRadius,
        questUseCooldownMs = _settings.QuestUseCooldownMs,
        mapClearStampRadius = _settings.MapClearStampRadius,
        mapClearAggroRange = _settings.MapClearAggroRange,
        mapClearStuckMs = _settings.MapClearStuckMs,
        eventEssence = _settings.EventEssence,
        eventStrongbox = _settings.EventStrongbox,
        eventShrine = _settings.EventShrine,
        eventBreach = _settings.EventBreach,
        eventRitual = _settings.EventRitual,
        eventChests = _settings.EventChests,
        eventClickStalled = _settings.EventClickStalled,
        eventRange = _settings.EventRange,
        eventUseRadius = _settings.EventUseRadius,
        eventMaxClicks = _settings.EventMaxClicks,
        moveMethod = _settings.MoveMethod,
        moveArriveRadius = _settings.MoveArriveRadius,
        moveRunEnabled = _settings.MoveRunEnabled,
        playInBackground = _settings.PlayInBackground,
        inputDisplay = _settings.InputDisplay,
        inputDisplayResolved = POE2Radar.Core.Native.GameHost.NestedInputDisplay,
        moveRunKey = _settings.MoveRunKey,
        moveLookAhead = _settings.MoveLookAhead,
        moveDiagonals = _settings.MoveDiagonals,
        moveAxisRotationDeg = _settings.MoveAxisRotationDeg,
        moveCooldownMs = _settings.MoveCooldownMs,
        moveKeyW = _settings.MoveKeyW,
        moveKeyA = _settings.MoveKeyA,
        moveKeyS = _settings.MoveKeyS,
        moveKeyD = _settings.MoveKeyD,
        moveClickKey = _settings.MoveClickKey,
        apiPort = _settings.ApiPort, // display only — changing it needs a restart
        styles = _settings.Styles,   // per-item icon shapes/colors/sizes + mechanic overrides
        hpBars = _settings.HpBars,   // monster HP-bar geometry (width/height/offset)
        terrain = _settings.Terrain, // walkable-terrain bitmap colors/transparency
        groundItems = _settings.GroundItems, // ground-item value overlay (enabled / highlight threshold / league)
        hoverPrice = _settings.HoverPrice, // hover-tooltip price chip (enabled / highlight threshold)
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
                // Combat assist ARM (CombatAssistEnabled) is F4-only — never a settings POST key.
                // Quest follow ARM (QuestFollowEnabled) is F3-only — never a settings POST key.
                // Path move ARM (MoveEnabled) is F5-only (also armed by F3 bot master) — never a settings POST key.
                // Bot master ARM (BotEnabled) is F3-only — never a settings POST key, never nested.
                // Map clear ARM (MapClearEnabled) is F2-only — never a settings POST key.
                case "combatRange" when TryFloat(p.Value, out var f): _settings.CombatRange = Math.Clamp(f, 1f, 200f); applied.Add(p.Name); break;
                case "combatEngageRange" when TryFloat(p.Value, out var f): _settings.CombatEngageRange = Math.Clamp(f, 1f, 200f); applied.Add(p.Name); break;
                case "combatStallMs" when TryInt(p.Value, out var n): _settings.CombatStallMs = n <= 0 ? 0 : Math.Clamp(n, 500, 60000); applied.Add(p.Name); break;
                case "combatFleeHpPct" when TryFloat(p.Value, out var f): _settings.CombatFleeHpPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "combatFleeRecoverPct" when TryFloat(p.Value, out var f): _settings.CombatFleeRecoverPct = Math.Clamp(f, 0f, 100f); applied.Add(p.Name); break;
                case "combatFleeDistance" when TryFloat(p.Value, out var f): _settings.CombatFleeDistance = Math.Clamp(f, 1f, 200f); applied.Add(p.Name); break;
                case "combatTargetMode" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } tm
                    && (tm is "Nearest" or "Rarity" or "LowestHp" or "HighestHp"): _settings.CombatTargetMode = tm; applied.Add(p.Name); break;
                case "combatRotationMode" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } rm
                    && (rm is "RoundRobin" or "Priority"): _settings.CombatRotationMode = rm; applied.Add(p.Name); break;
                case "combatKeepDistance" when TryFloat(p.Value, out var f): _settings.CombatKeepDistance = Math.Clamp(f, 0f, 200f); applied.Add(p.Name); break;
                case "combatIgnoreMs" when TryInt(p.Value, out var n): _settings.CombatIgnoreMs = Math.Clamp(n, 1000, 300000); applied.Add(p.Name); break;
                case "autoRespawn" when TryBool(p.Value, out var b): _settings.AutoRespawn = b; applied.Add(p.Name); break;
                case "respawnKey" when TryInt(p.Value, out var n): _settings.RespawnKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "respawnDelayMs" when TryInt(p.Value, out var n): _settings.RespawnDelayMs = Math.Clamp(n, 500, 30000); applied.Add(p.Name); break;
                case "combatDodgeKey" when TryInt(p.Value, out var n): _settings.CombatDodgeKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "combatSkills" when p.Value.ValueKind == JsonValueKind.Array:
                    if (TryParseCombatSkills(p.Value, out var csk)) { _settings.CombatSkills = csk; applied.Add(p.Name); }
                    break;
                case "questUseKey" when TryInt(p.Value, out var n): _settings.QuestUseKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "questUseRadius" when TryFloat(p.Value, out var f): _settings.QuestUseRadius = Math.Clamp(f, 0f, 64f); applied.Add(p.Name); break;
                case "questUseCooldownMs" when TryInt(p.Value, out var n): _settings.QuestUseCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "mapClearStampRadius" when TryInt(p.Value, out var n): _settings.MapClearStampRadius = Math.Clamp(n, 4, 64); applied.Add(p.Name); break;
                case "mapClearAggroRange" when TryFloat(p.Value, out var f): _settings.MapClearAggroRange = Math.Clamp(f, 0f, 500f); applied.Add(p.Name); break;
                case "mapClearStuckMs" when TryInt(p.Value, out var n): _settings.MapClearStuckMs = Math.Clamp(n, 1000, 120000); applied.Add(p.Name); break;
                case "eventEssence" when TryBool(p.Value, out var b): _settings.EventEssence = b; applied.Add(p.Name); break;
                case "eventStrongbox" when TryBool(p.Value, out var b): _settings.EventStrongbox = b; applied.Add(p.Name); break;
                case "eventShrine" when TryBool(p.Value, out var b): _settings.EventShrine = b; applied.Add(p.Name); break;
                case "eventBreach" when TryBool(p.Value, out var b): _settings.EventBreach = b; applied.Add(p.Name); break;
                case "eventRitual" when TryBool(p.Value, out var b): _settings.EventRitual = b; applied.Add(p.Name); break;
                case "eventChests" when TryBool(p.Value, out var b): _settings.EventChests = b; applied.Add(p.Name); break;
                case "eventClickStalled" when TryBool(p.Value, out var b): _settings.EventClickStalled = b; applied.Add(p.Name); break;
                case "eventRange" when TryFloat(p.Value, out var f): _settings.EventRange = Math.Clamp(f, 0f, 500f); applied.Add(p.Name); break;
                case "eventUseRadius" when TryFloat(p.Value, out var f): _settings.EventUseRadius = Math.Clamp(f, 1f, 30f); applied.Add(p.Name); break;
                case "eventMaxClicks" when TryInt(p.Value, out var n): _settings.EventMaxClicks = Math.Clamp(n, 1, 20); applied.Add(p.Name); break;
                case "moveMethod" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } mm
                    && (mm is "WASD" or "Click" or "ClickToMove"): _settings.MoveMethod = mm; applied.Add(p.Name); break;
                case "moveArriveRadius" when TryFloat(p.Value, out var f): _settings.MoveArriveRadius = Math.Clamp(f, 0f, 64f); applied.Add(p.Name); break;
                case "moveRunEnabled" when TryBool(p.Value, out var b): _settings.MoveRunEnabled = b; applied.Add(p.Name); break;
                case "playInBackground" when TryBool(p.Value, out var b): _settings.PlayInBackground = b; applied.Add(p.Name); break;
                case "inputDisplay" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } idn && idn.Length <= 32
                    && System.Text.RegularExpressions.Regex.IsMatch(idn, "^(auto|:[0-9]+(\\.[0-9]+)?)?$"):
                    _settings.InputDisplay = idn; applied.Add(p.Name); break;
                case "moveRunKey" when TryInt(p.Value, out var n): _settings.MoveRunKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "moveLookAhead" when TryFloat(p.Value, out var f): _settings.MoveLookAhead = Math.Clamp(f, 1f, 60f); applied.Add(p.Name); break;
                case "moveDiagonals" when TryBool(p.Value, out var b): _settings.MoveDiagonals = b; applied.Add(p.Name); break;
                case "moveAxisRotationDeg" when TryFloat(p.Value, out var f): _settings.MoveAxisRotationDeg = Math.Clamp(f, -180f, 180f); applied.Add(p.Name); break;
                case "moveCooldownMs" when TryInt(p.Value, out var n): _settings.MoveCooldownMs = Math.Clamp(n, 0, 60000); applied.Add(p.Name); break;
                case "moveKeyW" when TryInt(p.Value, out var n): _settings.MoveKeyW = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "moveKeyA" when TryInt(p.Value, out var n): _settings.MoveKeyA = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "moveKeyS" when TryInt(p.Value, out var n): _settings.MoveKeyS = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "moveKeyD" when TryInt(p.Value, out var n): _settings.MoveKeyD = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
                case "moveClickKey" when TryInt(p.Value, out var n): _settings.MoveClickKey = Math.Clamp(n, 1, 255); applied.Add(p.Name); break;
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

    /// <summary>Deserialize + sanitize a full <see cref="HoverPriceSettings"/> from posted JSON.
    /// Mirrors <see cref="TryParseGroundItems"/>. Returns false (settings untouched) on a parse failure.</summary>
    private static bool TryParseHoverPrice(JsonElement el, out HoverPriceSettings h)
    {
        h = new HoverPriceSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<HoverPriceSettings>(el.GetRawText(), Json);
            if (parsed == null) return false;
            parsed.HighlightMinEx = Math.Max(0, parsed.HighlightMinEx);
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

    /// <summary>Parse the combat-assist rotation the dashboard re-POSTs on edit: each entry is
    /// <c>{ key|vk, cooldownMs, range? }</c>. Sanitized + capped at 8; a malformed entry is skipped.
    /// An empty array is accepted (Decide no-ops until the user adds a skill).</summary>
    private static bool TryParseCombatSkills(JsonElement el, out List<CombatSkill> skills)
    {
        skills = new List<CombatSkill>();
        try
        {
            foreach (var s in el.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var key = 0;
                if (s.TryGetProperty("key", out var kv) && TryInt(kv, out var k)) key = k;
                else if (s.TryGetProperty("vk", out var vv) && TryInt(vv, out var vk)) key = vk;
                if (key is < 1 or > 255) continue;
                var cd = 400;
                if (s.TryGetProperty("cooldownMs", out var cv) && TryInt(cv, out var c)) cd = c;
                cd = Math.Clamp(cd, 0, 60000);
                var range = 0f;
                if (s.TryGetProperty("range", out var rv) && TryFloat(rv, out var r)) range = r;
                range = Math.Clamp(range, 0f, 200f);
                var minT = 1;
                if (s.TryGetProperty("minTargets", out var mv) && TryInt(mv, out var mt)) minT = Math.Clamp(mt, 1, 20);
                var rareOnly = s.TryGetProperty("rareOnly", out var ro) && TryBool(ro, out var rb) && rb;
                var hpBelow = 0f;
                if (s.TryGetProperty("hpBelowPct", out var hv) && TryFloat(hv, out var hb)) hpBelow = Math.Clamp(hb, 0f, 100f);
                var enabled = !(s.TryGetProperty("enabled", out var ev) && TryBool(ev, out var eb)) || eb;
                var repeat = 1; if (s.TryGetProperty("repeat", out var rpv) && TryInt(rpv, out var rp)) repeat = Math.Clamp(rp, 1, 10);
                var gap = 150; if (s.TryGetProperty("repeatGapMs", out var gv) && TryInt(gv, out var gp)) gap = Math.Clamp(gp, 30, 2000);
                var hold = 0; if (s.TryGetProperty("holdMs", out var hov) && TryInt(hov, out var ho)) hold = Math.Clamp(ho, 0, 10000);
                var dodge = s.TryGetProperty("dodgeAfter", out var dv) && TryBool(dv, out var db) && db;
                var nextDelay = 0; if (s.TryGetProperty("nextDelayMs", out var ndv) && TryInt(ndv, out var nd)) nextDelay = Math.Clamp(nd, 0, 10000);
                skills.Add(new CombatSkill { Key = key, CooldownMs = cd, Range = range, MinTargets = minT, RareOnly = rareOnly, HpBelowPct = hpBelow, Enabled = enabled,
                    Repeat = repeat, RepeatGapMs = gap, HoldMs = hold, DodgeAfter = dodge, NextDelayMs = nextDelay });
                if (skills.Count >= 8) break;
            }
            return true;
        }
        catch { return false; }
    }
}
