using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// User-tweakable overlay settings, persisted as JSON next to the executable
/// (<c>config/radar_settings.json</c>). Defaults reproduce the original hardcoded behavior exactly,
/// so a missing/partial file changes nothing. Calibration is saved live as hotkeys adjust it.
/// </summary>
public sealed class RadarSettings
{
    // ── Feature flags (reserved for later phases; no behavior wired yet). ──
    public bool HideJunk { get; set; } = false;
    public bool ShowPath { get; set; } = false;
    public bool UseCuratedLandmarks { get; set; } = true;
    public bool DrawAllLandmarkPaths { get; set; } = false;

    // ── Landmark clustering. A reusable tile (e.g. a "stairs up" wall piece) recurs in several
    //    disjoint spots — a multi-level dungeon has several stair-up/stair-down sections — so the
    //    scanner groups a tile path's cells into spatial clusters and emits one marker per cluster.
    //    This is the MAX GAP (in TILES; 1 tile ≈ 23 grid units) between cells still considered the
    //    same cluster: larger = merges nearby spots (fewer markers, less map spam), smaller = splits
    //    them (more markers). 0 disables bridging (only directly-touching tiles group). ──
    public int LandmarkClusterGap { get; set; } = 2;

    // ── Radar display toggles. ──
    public bool ShowMonsters { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    // The player position blip at map-center. Default on (prior behavior); some prefer it off.
    public bool ShowPlayerBlip { get; set; } = true;

    // ── Overlay render/present rate (Hz). The overlay redraws + UpdateLayeredWindow-blits at this
    //    rate; lower = less CPU/GPU tax on the game (the blit cost is proportional to resolution).
    //    0 = AUTO: match the refresh rate of the monitor the game is on (re-detected ~1/s) — recommended,
    //    so fast screen-anchored elements (loot-value chips) track smoothly. Set a fixed number to cap it.
    //    The heavier entity/terrain walk stays fixed at ~30 Hz regardless. ──
    public int FpsCap { get; set; } = 0;

    // ── Navigation-menu widget: which screen corner it is pinned to.
    //    One of "TopLeft", "TopRight", "BottomLeft", "BottomRight". ──
    public string NavMenuCorner { get; set; } = "TopLeft";

    // ── Persistent auto-nav: substrings matched (case-insensitive Contains) against a navigation
    //    target's MatchKey (tile path / entity metadata). On every zone change, every target whose
    //    MatchKey matches ANY pattern is auto-selected (up to the 8-color cap), so entering a new
    //    zone auto-draws a path to e.g. the expedition encounter. Seeded with one example so the
    //    feature is visible out of the box; clear the list to disable. ──
    // Dir-qualified so it matches the real marker ("Expedition2/Expedition2Encounter") and not the
    // transient ".../Objects/Expedition2EncounterCrack" effects. (Plain "ExpeditionEncounter" matched
    // nothing — the live path is "Expedition2Encounter" with a digit.)
    public List<string> AutoNavPatterns { get; set; } = new() { "Expedition2/Expedition2Encounter" };

    // ── Monster HP bars (world-space nameplates) by rarity.
    //    Defaults preserve prior behavior: Magic/Rare/Unique shown, Normal hidden. ──
    public bool HpBarNormal { get; set; } = false;
    public bool HpBarMagic { get; set; } = true;
    public bool HpBarRare { get; set; } = true;
    public bool HpBarUnique { get; set; } = true;

    // ── Projection calibration (PageUp/Down = scale, arrows = offset, Home = reset). ──
    public float ScaleMul { get; set; } = 1.0f;
    public float OffX { get; set; } = 0f;
    public float OffY { get; set; } = 0f;

    // Draw the overlay even when PoE2 isn't the foreground window (e.g. while tweaking the dashboard).
    // Auto-flask stays foreground-gated regardless (safety). Default off (overlay hides when unfocused).
    public bool AlwaysShowOverlay { get; set; } = false;

    // Still the in-game menus: no spinning sigils, drifting motes, pulses or entrance animations.
    public bool ReduceMotion { get; set; } = false;

    // NOTE: the atlas canvas→screen projection has NO stored settings — it's derived live from the game
    // window height (UIscale = winH/1600 × live zoom) in RadarApp.AtlasProjection, so it's resolution-
    // correct everywhere with no calibration. (The old F10/F11 homography calibration + its AtlasScale/
    // Off/Shear/Pers/CalibZoom settings were removed; F10 now inspects the tile under the cursor.)

    // Atlas highlight rules: only nodes whose content tags include one of these are drawn in-game (the
    // point is to surface content the game hides by default). Set live from the dashboard Atlas tab.
    // Matched case-insensitively against each node's resolved content tags (e.g. "Breach", "Powerful Map Boss").
    public List<string> AtlasHighlightTags { get; set; } = new();
    // Tags with the off-screen ARROW enabled: when a matching map is outside render distance, an edge
    // arrow points toward it (for hunting high-value maps you can't zoom out to). Independent of tracking.
    public List<string> AtlasArrowTags { get; set; } = new();
    // Tags with NAV-TO enabled: a shortest-hop route line is drawn from the accessible frontier to each
    // matching map. Independent of Highlight (ring) and Arrow — you can route to a map without ringing it,
    // or ring without routing. This is the set the auto-router targets.
    public List<string> AtlasNavTags { get; set; } = new();
    // Per-rule ring colour (tag → "#RRGGBB"), so each highlighted map draws in its filter's category
    // colour in-game (Citadel gold, Boss red, …). Set from the dashboard alongside AtlasHighlightTags.
    public Dictionary<string, string> AtlasHighlightColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Seeded-defaults guard: false until the atlas rules have been initialized once (either by seeding
    // the Citadel defaults when nodes are first read, or by any dashboard edit). Stops re-seeding.
    public bool AtlasRulesInitialized { get; set; }
    // Separate guard for the built-in "Map Targets" preset (#6). Distinct from AtlasRulesInitialized so the
    // preset seeds ONCE even on a config from before the preset existed (where RulesInitialized was already
    // true). The seed is ADDITIVE — it only adds the built-in target names/colours, never clears user rules.
    public bool AtlasTargetsSeeded { get; set; }
    // DEBUG: draw EVERY atlas node (overriding the highlight-only rule) — for offset/coverage diagnostics.
    // Off by default: normally only nodes matching AtlasHighlightTags (or manually selected) are drawn.
    public bool AtlasDrawAll { get; set; } = false;
    // Atlas routing: F10 over a tile sets it as the route destination; the overlay draws the shortest path
    // (through the node connection graph) from the player's current node to it. On by default.
    public bool AtlasShowRoute { get; set; } = true;
    // Auto-routing: draw a shortest-hop route from the player's CURRENT atlas node (or the accessible-now
    // frontier when the current node isn't known) to every tracked tile, with a hop-count chip per target.
    // This is the "auto-navigate to the key tiles I'm tracking" feature. On by default.
    public bool AtlasAutoRoute { get; set; } = true;
    // Suppress auto-routes longer than this many map hops (0 = no limit). Keeps the view readable when a
    // common content type (e.g. Breach) is tracked across the whole atlas.
    public int AtlasAutoRouteMaxHops { get; set; } = 0;
    // Draw a biome-coloured border around tracked map labels on the open Atlas (richer in-game info). On by default.
    public bool AtlasShowBiomeBorder { get; set; } = true;
    // Declutter filters (#3): suppress drawing rings/icons/routes on maps in these states. Completed maps
    // are hidden by default (you've run them); accessible-now maps stay visible. F10 route endpoints are
    // always drawn regardless. We already skip routing to completed maps.
    public bool AtlasHideCompleted { get; set; } = true;
    public bool AtlasHideAccessible { get; set; } = false;
    // On-node content icons (#5): draw the in-game content art (Breach/Boss/Essence/…) above tracked maps
    // and above FOGGED/off-screen maps the game isn't drawing icons for (never on already-visible nodes —
    // the game draws those itself). On by default. Size is the icon height in px before any scaling.
    public bool AtlasShowContentIcons { get; set; } = true;
    public float AtlasContentIconSize { get; set; } = 26f;
    // Route chevron spacing (#4): gap between the directional arrowheads drawn along a route, as a multiple
    // of the chevron size (higher = more spread out). Mirrors the GameHelper2 Atlas plugin's RouteArrowSpacing.
    public float AtlasRouteArrowSpacing { get; set; } = 8f;
    // Map colour groups (#7): named sets of map display names → one ring/label colour, so a whole category
    // (Citadels, Halls, Uniques, Expedition) recolours together. Seeded with sensible defaults once
    // (AtlasGroupsSeeded). A node in a group draws in the group colour when it has no per-rule colour.
    public List<AtlasMapGroup> AtlasGroups { get; set; } = new();
    public bool AtlasGroupsSeeded { get; set; }

    // One-time guard: false until the default "Abyss Lightless (Void)" monster display rule has been
    // seeded into display_rules.json. Set true after seeding so a user who deletes the rule keeps it gone.
    public bool AbyssRuleSeeded { get; set; }

    // One-time guard: false until the curated icon glyphs have been applied to the stock display rules
    // (Skull/Crown/Chest/MapPin/…). The migration only retouches rules still on their OLD default shape,
    // so user customizations are preserved; set true afterward so it runs at most once.
    public bool IconDefaultsApplied { get; set; }
    // v2 guard: re-runs the icon migration with separator-insensitive name matching (the v1 pass missed the
    // rules whose names contain "·" due to a code-point mismatch) and the monster Magic/Rare glyphs.
    public bool IconDefaultsApplied2 { get; set; }
    // One-time guard: removes the stale legacy "watched" Diamond rules that duplicated (and shadowed) the
    // mechanic rules, gates Ritual/Breach/Essence to Object/Other so they can't tag league monsters, and
    // reskins the remaining navigation-POI diamonds. Set true afterward so it runs at most once.
    public bool RuleCleanupV1 { get; set; }
    // One-time guard: gives the non-monster mechanic/special rules a default in-game LABEL where they had
    // none (Strongbox/Essence/Shrine/Transition/chest rarities), so the marker shows text, not just an icon.
    public bool MechanicLabelsV1 { get; set; }
    // One-time guard: broadens the ground-item category set from the old {Uniques,Runes,Essences,Currency}
    // to the full high-value set, now that non-uniques actually price + draw.
    public bool GroundDefaultsV2 { get; set; }
    // One-time guard: bumps the monster Magic/Rare/Unique rule sizes — the detailed Fang/Claw/Skull glyphs
    // need ~1.5× the size the old flat shapes used to be legible at radar scale.
    public bool IconSizesV1 { get; set; }
    // One-time guard: folds the built-in tracked-tile rules (DisplayRules.BuiltInTileRules — currently the
    // WaygateDevice waygate) into existing display_rules.json configs. Additive; set true after seeding so a
    // user who deletes the rule keeps it gone. Fresh configs get them via DisplayRules.BuildDefault.
    public bool BuiltInTileRulesSeeded { get; set; }

    // ── Auto-flask master enable (the F8 in-game kill-switch persists here so a disabled state survives
    //    a restart). Defaults ON to preserve the historical "auto-on each launch" behavior. NOTE: this is
    //    deliberately NOT writable via the HTTP API — automation is only ever armed from the local F8 key. ──
    public bool AutoFlaskEnabled { get; set; } = true;

    // ── Auto-flask thresholds + per-flask cooldowns (milliseconds). ──
    // What the (single) life-flask key triggers on: "Health" watches HP% only (default — unchanged
    // behavior), "EnergyShield" watches ES% only (for CI / ES-stacking builds), "Either" fires when
    // EITHER pool drops below its own threshold. ES is ignored when the build has no ES pool.
    public string LifeFlaskMode { get; set; } = "Health";
    public float LifeThresholdPct { get; set; } = 65f;
    public float EsThresholdPct { get; set; } = 50f;
    public float ManaThresholdPct { get; set; } = 30f;
    public int LifeCooldownMs { get; set; } = 2500;
    public int ManaCooldownMs { get; set; } = 2000;

    // ── Flask key codes (Win32 virtual-key). Defaults: '1' = life, '2' = mana. ──
    public int LifeKey { get; set; } = 0x31;
    public int ManaKey { get; set; } = 0x32;

    // ── Buff keeper (recast expired self-buffs). Its Enabled flag is the persisted ARM state — toggled by
    //    its hotkey / the INSERT menu only, never over HTTP. ──
    public BuffKeeperSettings BuffKeeper { get; set; } = new();

    // ── Chat macros, bookmarks and item-inspect hotkeys. ──
    public CommandSettings Commands { get; set; } = new();

    // ── Waystone mod checker (flags dangerous mods when hovering a waystone). ──
    public MapCheckSettings MapCheck { get; set; } = new();

    // ── Trade assistant: Client.txt-driven trade panel + earnings tracker. ──
    public TradeSettings Trade { get; set; } = new();

    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    // ── Per-item icon styling (shape / color / opacity / size) + metadata-matched "mechanic"
    //    overrides. Defaults reproduce the original hardcoded look exactly. ──
    public RadarStyles Styles { get; set; } = new();

    // ── Monster HP-bar geometry (the per-rarity ENABLE flags above stay the source of truth;
    //    this adds per-rarity sizing, border thickness, and border color). ──
    public HpBarSettings HpBars { get; set; } = new();

    // ── Walkable-terrain bitmap colors/transparency. Defaults reproduce the old hardcoded wash. ──
    public TerrainSettings Terrain { get; set; } = new();

    // ── Ground-item value overlay (unique drops): name + price over the loot icon, border if above value. ──
    public GroundItemSettings GroundItems { get; set; } = new();

    // ── Hover price: chip beside the game's item tooltip (inventory/stash/vendor); stacks show per-unit + total. ──
    public HoverPriceSettings HoverPrice { get; set; } = new();

    // ── Runeshape-monolith reward overlay: value-coloured map icon + N badge + nearby reward panel. ──
    public MonolithSettings Monoliths { get; set; } = new();

    // ── Currency Exchange (Kalguur market) order-book depth overlay: top-right panel with the best
    //    offered/wanted ratios + depth. Mirrors the GroundItems/Monoliths settings pattern. ──
    public CurrencyExchangeSettings CurrencyExchange { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Config file path: a "config" directory next to the executable.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "radar_settings.json");

    /// <summary>
    /// Load settings from disk. Returns defaults if the file is missing (and writes a default file),
    /// and is tolerant of partial/missing keys. Never throws on IO/parse errors — logs and falls back.
    /// </summary>
    public static RadarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new RadarSettings();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<RadarSettings>(json, Json) ?? new RadarSettings();
            // Existing configs are loaded verbatim (never re-seeded from defaults), so repair stale
            // patterns shipped by older builds in place, then persist the upgrade.
            if (loaded.Migrate())
            {
                loaded.Save();
                Console.WriteLine("Settings: migrated stale mechanic rules (Expedition/Strongbox category gating).");
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings load failed ({ex.Message}); using defaults.");
            return new RadarSettings();
        }
    }

    /// <summary>
    /// One-time, idempotent repair of mechanic rules from older builds (loaded verbatim, so they'd
    /// otherwise keep the bug forever). Both fixes address ungated rules that tagged a mechanic's
    /// spawned monsters, not just the object:
    /// <list type="bullet">
    /// <item>Expedition: bare "Expedition" / dead "ExpeditionEncounter" → precise, Other-gated
    ///   "Expedition2/Expedition2Encounter".</item>
    /// <item>Strongbox: add a Chest category gate (the box's Vaal guards carry "...Strongbox").</item>
    /// </list>
    /// Returns true if anything changed.
    /// </summary>
    public bool Migrate()
    {
        const string precise = "Expedition2/Expedition2Encounter";
        static bool IsStaleExp(string p) =>
            string.Equals(p, "Expedition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, "ExpeditionEncounter", StringComparison.OrdinalIgnoreCase);

        var changed = false;

        static bool IsBroadStrongbox(string p) => string.Equals(p, "Strongbox", StringComparison.OrdinalIgnoreCase);

        if (Styles?.Mechanics is { } mechanics)
            foreach (var m in mechanics)
            {
                if (m.Match is null) continue;
                // Expedition: drop the stale/over-broad keys → the precise key + an Other category gate
                // (so it can't hijack the Monster-category expedition mobs).
                if (m.Match.RemoveAll(IsStaleExp) > 0)
                {
                    if (!m.Match.Exists(p => string.Equals(p, precise, StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add(precise);
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Other");
                    changed = true;
                }
                // Strongbox: the default's bare "Strongbox" term over-matched twice — the box's spawned
                // Vaal guards (…Strongbox monsters) and ordinary area chests named "...Strongbox". Drop
                // it down to the "StrongBoxes" directory term and gate to Chest (the box is a /Chests/
                // entity). Triggers whenever the broad term is still present, regardless of category.
                else if (m.Match.Exists(IsBroadStrongbox))
                {
                    m.Match.RemoveAll(IsBroadStrongbox);
                    if (!m.Match.Exists(p => string.Equals(p, "StrongBoxes", StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add("StrongBoxes");
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Chest");
                    changed = true;
                }
            }

        // Auto-nav: the seeded "ExpeditionEncounter" matched nothing (digit in the real path).
        if (AutoNavPatterns is not null)
            for (var i = 0; i < AutoNavPatterns.Count; i++)
                if (IsStaleExp(AutoNavPatterns[i])) { AutoNavPatterns[i] = precise; changed = true; }

        return changed;
    }

    /// <summary>Persist current settings to disk. Never throws on IO error — logs and continues.</summary>
    public void Save()
    {
        if (!TrySave(out var error)) Console.Error.WriteLine($"Settings save failed: {error}");
    }

    public bool TrySave(out string error)
    {
        error = "";
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
