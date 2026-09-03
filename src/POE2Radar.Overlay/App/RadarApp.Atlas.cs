using System.Linq;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    private readonly Poe2Atlas _atlas;

    private sealed record AtlasAutoSpec(IReadOnlyList<AtlasPoint> Points, string? Color, int Hops);

    private sealed record AtlasRender(bool Open, IReadOnlyList<AtlasMark> Marks, AtlasPoint? Start, AtlasPoint? End, IReadOnlyList<AtlasPoint>? Route, AtlasPoint? Current, IReadOnlyList<AtlasAutoSpec> AutoRoutes)
    {
        public static readonly AtlasRender Closed = new(false, Array.Empty<AtlasMark>(), null, null, null, null, Array.Empty<AtlasAutoSpec>());
    }

    private volatile AtlasRender _atlasRender = AtlasRender.Closed;

    private readonly List<AtlasMark> _atlasMarkFrame = new();

    private readonly object _atlasLock = new();

    private readonly HashSet<nint> _atlasSel = new();

    // F10 route workflow (manual, no memory-marker dependency): 1st F10 sets START tile, 2nd sets END tile
    // (and routes between them through the connection graph), 3rd resets. Stored by GRID coord so they
    // survive pan/zoom and the tiles going off-screen. Written by F10 (render thread), read by UpdateAtlas
    // (world thread) — guarded by _atlasLock (nullable int-tuples aren't torn-read-safe).
    private (int X, int Y)? _atlasStartGrid;

    private (int X, int Y)? _atlasGoalGrid;

    private DateTime _atlasGoodAt = DateTime.MinValue;

    private long _lastAtlasSig;

    private bool _builtAtlasOnce;

    // Live atlas zoom (= canvas/node scale @ +0x130; 0.85 max-out … larger zoomed in). relPos is read
    // live (pan baked in) and the projection scales by this zoom, so rings track pan AND zoom.
    private volatile float _atlasZoom = 0.85f;

    /// <summary>F10: pick the atlas tile under the cursor and advance the route workflow (START → END → reset).
    /// Inverts the same projection the renderer draws with (relPos = screen / scale) to map the cursor into
    /// canvas space, then picks the tile whose box CONTAINS it (fallback: nearest centre). Stores the pick by
    /// GRID coord so the route survives pan/zoom and tiles going off-screen. (No on-screen tile-details
    /// tooltip — that interfered with the point-to-point selection; the pick is just echoed to the console.)</summary>
    private void AtlasRoutePick()
    {
        if (_inGameStateForApi == 0 || !GameHost.GetCursorPos(out var spt)) { Console.WriteLine("\n[atlas route] not in game."); return; }
        var pt = ScreenToClientPoint(spt);
        // Invert the shared projection: for screen = relPos × scale (offset/shear/persp = 0), relPos = screen/scale.
        var proj = AtlasProjection();
        double scaleX = Math.Abs(proj[0]) > 1e-6 ? proj[0] : 1, scaleY = Math.Abs(proj[4]) > 1e-6 ? proj[4] : 1;
        double curX = pt.X / scaleX, curY = pt.Y / scaleY; // cursor in canvas/relPos units

        Poe2Atlas.AtlasNodeLive? bestIn = null, bestAny = null; double bdIn = 1e18, bdAny = 1e18;
        foreach (var n in _atlas.ReadNodes(_inGameStateForApi))
        {
            // Consider EVERY node (not just the local-Visible ones): the game leaves the visible bit OFF for
            // undiscovered / fog-of-war tiles even though it draws them at a valid relPos, so filtering it made
            // F10 skip fogged tiles and snap to the nearest visible neighbour. Routing must reach those tiles.
            if (!float.IsFinite(n.X) || !float.IsFinite(n.Y)) continue;
            double dx = curX - n.X, dy = curY - n.Y, d = dx * dx + dy * dy;
            if (d < bdAny) { bdAny = d; bestAny = n; }     // nearest centre (fallback)
            double hw = (n.W > 1 ? n.W : 40) * 0.5, hh = (n.H > 1 ? n.H : 40) * 0.5; // tile half-extents (canvas units)
            if (Math.Abs(dx) <= hw && Math.Abs(dy) <= hh && d < bdIn) { bdIn = d; bestIn = n; } // cursor inside the tile box
        }
        if ((bestIn ?? bestAny) is not { } b) { Console.WriteLine("\n[atlas route] no tile under cursor (is the Atlas open?)."); return; }

        // Dump the hovered tile's full identity so the user can set web-UI filters even when the display
        // name is unusual: the REAL map name (WorldAreas +0x08), the raw internal code (never localized,
        // always a safe match key), the rolled content tags, biome and grid coord.
        var content = b.Tags.Count > 0 ? string.Join(", ", b.Tags) : "(none)";
        Console.WriteLine($"\n[atlas tile] \"{b.MapName}\"  code={b.MapCode}  kind={b.Kind}  grid={b.Grid}  biome={b.Biome}");
        // Status cross-validation (improvement 1): deeper-model accessible/completed vs the element flag bits.
        Console.WriteLine($"            accessible={b.Accessible} completed={b.Completed}  (elem flags=0x{b.Flags:X2}: unlocked={b.Unlocked} visited={b.Visited})");
        Console.WriteLine($"             content: {content}");
        Console.WriteLine($"             web-UI filters -> Map: \"{b.MapName}\"" + (b.Tags.Count > 0 ? $"   Content: {content}" : ""));

        // 1st press → set START · 2nd press → set END (route computed each tick) · 3rd → reset. The grids
        // are read by the world thread (UpdateAtlas/BuildAtlasRoute), so mutate them under _atlasLock —
        // a nullable int-tuple isn't a torn-read-safe field.
        string stage;
        lock (_atlasLock)
        {
            if (_atlasStartGrid is null) { _atlasStartGrid = b.Grid; _atlasGoalGrid = null; stage = $"START = {b.Grid} '{b.MapName}'  (F10 another tile to set END)"; }
            else if (_atlasGoalGrid is null) { _atlasGoalGrid = b.Grid; stage = $"END = {b.Grid} '{b.MapName}'  (routing from {_atlasStartGrid}; F10 again to reset)"; }
            else { _atlasStartGrid = null; _atlasGoalGrid = null; stage = "route RESET (F10 a tile to set a new START)"; }
        }
        Console.WriteLine($"\n[atlas route] {stage}");
    }

    /// <summary>The atlas projection, derived LIVE from the game window height and live atlas zoom:
    /// screen = relPos × (UIscale×zoom), UIscale = winH/1600. Pure uniform scale, NO offset — relPos
    /// already has pan baked in and the canvas origin sits at screen (0,0) (the long-proven 1080p default
    /// was scale≈0.572 / offset 0). This is what lines up at any resolution with no hand-calibration.
    /// Returned in the 8-coeff homography layout (shear + perspective + offset = 0).</summary>
    private double[] AtlasProjection()
    {
        float uiScale = _window.Height > 0 ? _window.Height / 1600f : 1080f / 1600f;
        float scale = uiScale * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
        return new double[] { scale, 0, 0, 0, scale, 0, 0, 0 };
    }

    /// <summary>API (/api/atlas): a JSON-ready snapshot of the atlas map-data we can read — the full
    /// map-archetype catalog and the set of map types present in the current atlas region. Inspection /
    /// validation only (no spatial graph yet — see resources/atlas-research-notes.md). The reader scans
    /// + caches, so the first call after entering the atlas may take a moment; called on the API thread.</summary>
    private object AtlasJson()
    {
        // Anchor the scan to the live game-heap slab (the catalog shares the arena with AreaInstance).
        var d = _atlas.Read(_lastAreaInstance);
        // Live node graph (atlas nodes are UiElements) — summary + the locally-visible highlight set.
        var nodes = _inGameStateForApi != 0 ? _atlas.ReadNodes(_inGameStateForApi) : new List<Poe2Atlas.AtlasNodeLive>();
        var vis = nodes.Where(n => n.Visible).ToList();
        return new
        {
            located = d.Located,
            note = d.Note,
            catalogAddr = $"0x{d.CatalogAddr:X}",
            catalogCount = d.CatalogCount,
            regionCount = d.Region.Count,
            catalog = d.Catalog.Select(m => new { id = m.Id, code = m.Code, name = m.Name, kind = m.Kind, parsedObj = $"0x{m.ParsedObj:X}" }),
            region = d.Region.Select(r => new { code = r.Code, name = r.Name, kind = r.Kind }),
            nodes = new
            {
                total = nodes.Count,
                visible = vis.Count,
                hasContent = nodes.Count(n => n.HasContent),
                unvisited = nodes.Count(n => !n.Visited),
                unlocked = nodes.Count(n => n.Unlocked),
                biomes = nodes.GroupBy(n => (int)n.Biome).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            },
            // Every distinct content tag currently on the atlas (+ count), for the dashboard's filter /
            // highlight-rule pickers. These are the readable content/mechanic names (Powerful Map Boss,
            // Breach, Delirium, …) resolved from each node's EndgameMapAtlas row.
            allTags = nodes.SelectMany(n => n.Tags).GroupBy(t => t).OrderByDescending(g => g.Count())
                .Select(g => new { tag = g.Key, count = g.Count(), desc = AtlasMapData.Shared.ContentDesc(g.Key), icon = AtlasMapData.Shared.ContentIcon(g.Key) }),
            // Distinct MAP NAMES (Sun Temple, Precursor Tower, Vaal City, …) — the separate "Map" filter
            // group, so towers/temples/specific maps are highlightable independently of rolled content.
            allMaps = nodes.Where(n => !string.IsNullOrEmpty(n.MapName)).GroupBy(n => n.MapName)
                .OrderBy(g => g.Key).Select(g => new { tag = g.Key, count = g.Count() }),
            // Map-archetype KINDS present (Citadel / Boss / Tower / Unique / Merchant) — first-class track
            // targets (improvement 3): tracking "Tower" rings/routes EVERY tower without listing each name.
            allKinds = nodes.Where(n => !string.IsNullOrEmpty(n.Kind) && n.Kind != "Normal").GroupBy(n => n.Kind)
                .OrderByDescending(g => g.Count()).Select(g => new { tag = g.Key, count = g.Count() }),
            // maps.json classification tokens (#7): the map TYPE (e.g. "unique") + cross-cutting TAGS
            // (lineage/arbiter). Selectable as one-click route/track targets like allKinds. De-duped union.
            allDataTags = nodes.SelectMany(n =>
                    (string.IsNullOrEmpty(n.MapType) || n.MapType == "normal" ? Enumerable.Empty<string>() : new[] { n.MapType })
                    .Concat(n.MapDataTags ?? Array.Empty<string>()))
                .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => new { tag = g.Key, count = g.Count() }),
            // The currently active rules (persisted): tracked tags (rings) + arrow tags (off-screen
            // direction). Match against BOTH content tags and map names.
            highlightTags = _settings.AtlasHighlightTags,
            navTags = _settings.AtlasNavTags,
            arrowTags = _settings.AtlasArrowTags,
            // The individual live nodes for the dashboard's grid. On-screen first, then content/unvisited.
            nodeList = nodes
                .OrderByDescending(n => n.Visible).ThenByDescending(n => n.HasContent).ThenByDescending(n => !n.Visited)
                .Take(2000)
                .Select(n => new
                {
                    el = ((long)n.Element).ToString(), // unique stable key (element address) for selection
                    id = n.Id, biome = (int)n.Biome, type = n.IconType, hasContent = n.HasContent,
                    unlocked = n.Unlocked, visited = n.Visited, visible = n.Visible,
                    accessible = n.Accessible, completed = n.Completed, kind = n.Kind,
                    x = (int)n.X, y = (int)n.Y, map = n.MapName, tags = n.Tags,
                }),
        };
    }

    // Built-in "Map Targets" preset (#6) — high-value maps to ring/route/arrow on first open, matched by
    // exact internal MapId (reliable via the maps.json layer). Ported from the GameHelper2 Atlas plugin's
    // BuiltInTargets. On=true → seeded as an active default; off targets are discoverable in the dashboard.
    private static readonly (string Code, string Color, bool On)[] BuiltInAtlasTargets =
    {
        ("MapUberBoss_StoneCitadel",   "#e0b341", true),   // Citadel gold
        ("MapUberBoss_IronCitadel",    "#e0b341", true),
        ("MapUberBoss_CopperCitadel",  "#e0b341", true),
        ("MapMothersoul_Male",         "#e0b341", true),   // Halls
        ("MapMothersoul_Female",       "#e0b341", true),
        ("MapDerelictMansion",         "#058f3b", true),   // green specials
        ("MapCavernCity",              "#058f3b", true),
        ("MapVaalVault",               "#058f3b", true),
        ("MapUberBoss_JadeCitadel",    "#058f3b", true),
        ("MapUniqueUntaintedParadise", "#ff9933", false),  // orange uniques (off by default)
        ("MapUniqueCastaway",          "#ff9933", false),
    };

    // Default colour groups (#7), adopted from the plugin's Map Styles. Seeded once (AtlasGroupsSeeded).
    private static readonly (string Name, string Color, string[] Maps)[] DefaultAtlasGroups =
    {
        ("Citadels", "#e0b341", new[] { "The Copper Citadel", "The Iron Citadel", "The Stone Citadel" }),
        ("Halls",    "#e0b341", new[] { "The Matriarch Halls", "The Patriarch Halls" }),
        ("Uniques",  "#ff9933", new[] { "Untainted Paradise", "Castaway", "The Fractured Lake",
            "The Ezomyte Megaliths", "Moment of Zen", "The Viridian Wildwood" }),
        ("Expedition", "#fff0d9", new[] { "Sprawling Jungle", "Secluded Temple", "Obscure Island",
            "Mournful Cliffside", "Moor of Fallen Skies" }),
    };

    /// <summary>One-time seed (#6 + #7) of the built-in target rules + colour groups, gated on first full
    /// node read (AllTagsResolved) so names/ids are available. Idempotent via the two settings guards; any
    /// later dashboard edit keeps its own state (AtlasRulesInitialized locks out re-seeding the rules).</summary>
    private void SeedAtlasDefaults(IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes)
    {
        if (!_atlas.AllTagsResolved) return;
        var changed = false;

        if (!_settings.AtlasTargetsSeeded)
        {
            // Resolve each built-in MapId to its live display name so the rules are dashboard-editable.
            var byCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
                if (!string.IsNullOrEmpty(n.MapCode) && !string.IsNullOrEmpty(n.MapName))
                    byCode.TryAdd(n.MapCode, n.MapName);
            // ADDITIVE: add each built-in target name to the rule lists if not already present, never
            // clearing the user's own rules. Seeds once (the AtlasTargetsSeeded guard), independent of the
            // legacy AtlasRulesInitialized so it also reaches configs from before this preset existed.
            _settings.AtlasHighlightTags ??= new List<string>();
            _settings.AtlasNavTags ??= new List<string>();
            _settings.AtlasArrowTags ??= new List<string>();
            void AddOnce(List<string> list, string name) { if (!list.Contains(name)) list.Add(name); }
            foreach (var (code, color, on) in BuiltInAtlasTargets)
            {
                if (!on || !byCode.TryGetValue(code, out var name)) continue;
                AddOnce(_settings.AtlasHighlightTags, name); // ring
                AddOnce(_settings.AtlasNavTags, name);       // + auto-route
                AddOnce(_settings.AtlasArrowTags, name);     // + off-screen arrow
                _settings.AtlasHighlightColors[name] = color;
            }
            _settings.AtlasTargetsSeeded = true;
            _settings.AtlasRulesInitialized = true;
            changed = true;
        }

        if (!_settings.AtlasGroupsSeeded)
        {
            _settings.AtlasGroups ??= new List<AtlasMapGroup>();
            if (_settings.AtlasGroups.Count == 0)
                foreach (var (name, color, maps) in DefaultAtlasGroups)
                    _settings.AtlasGroups.Add(new AtlasMapGroup { Name = name, Color = color, Maps = new List<string>(maps) });
            _settings.AtlasGroupsSeeded = true;
            changed = true;
        }

        if (changed) _settings.Save();
    }

    /// <summary>Read the live atlas nodes and rebuild the highlight marks + F10 route, publishing them as a
    /// single immutable <see cref="AtlasRender"/> the render thread reads lock-free. Runs on the world thread.
    /// Cheap when the atlas is closed (ReadNodes returns empty via its visibility gate). Rides over transient
    /// empty reads so the route doesn't flicker; freezes the marks when the view is static (no arrow jitter).</summary>
    private void UpdateAtlas(nint inGameState)
    {
        var nodes = _atlas.ReadNodes(inGameState);
        if (nodes.Count == 0)
        {
            // Empty read: ride over TRANSIENT misses (a node read hiccupping ~1×/sec was the ~0.1s route
            // flicker) so the route doesn't blink out. Treat the atlas as CLOSED only when the panel's visible
            // bit reads closed AND we've had no good read for a short grace — that absorbs both the node-read
            // miss and a racy visible-bit read, while still clearing promptly on a real close.
            var stillOpen = _atlas.IsAtlasOpen(inGameState) || (DateTime.UtcNow - _atlasGoodAt).TotalSeconds < 0.4;
            if (_atlasRender.Open && stillOpen) return;      // keep last marks/route — no flicker
            _builtAtlasOnce = false; _lastAtlasSig = 0;      // force a rebuild on reopen
            if (!ReferenceEquals(_atlasRender, AtlasRender.Closed)) _atlasRender = AtlasRender.Closed;
            return;                                          // (manual START/END grids persist)
        }
        _atlasGoodAt = DateTime.UtcNow;
        // Live zoom = the nodes' shared canvas scale (+0x130). Use the median (robust to a stray 0/odd node).
        // Drives both the ring projection and the route projection (relPos × winH/1600 × zoom).
        var scales = nodes.Where(n => n.Scale > 0.01f).Select(n => n.Scale).OrderBy(s => s).ToList();
        if (scales.Count > 0) _atlasZoom = scales[scales.Count / 2];

        // Snapshot the cross-thread inputs ONCE under the lock: the F10 START/END grids (written by the
        // render thread) and the dashboard node selection (written by the API thread).
        (int X, int Y)? startGrid, goalGrid; HashSet<nint> sel;
        lock (_atlasLock) { startGrid = _atlasStartGrid; goalGrid = _atlasGoalGrid; sel = new HashSet<nint>(_atlasSel); }
        // The player's CURRENT atlas node (the route source for auto-navigation). Changes only on zone
        // change, but it MUST feed the freeze signature so the auto-routes re-solve as the player advances.
        var curGrid = _atlas.CurrentNodeGrid();

        // ARROW JITTER FIX — freeze the marks when the atlas view is static. PoE2 doesn't keep CULLED
        // (off-screen) UI elements' relPos cleanly updated, so off-screen nodes' positions are noisy — which
        // is why the off-screen ARROWS jitter while on-screen rings (properly laid out) stay still. Arrows
        // are only a direction hint, so we don't need to re-read them every tick: build a signature from the
        // live zoom + the centroid of FIRMLY on-screen nodes (stable when idle) + the inputs that affect the
        // marks (route endpoints, rule/selection counts). If it's unchanged, KEEP the previous marks/route
        // frozen → no jitter. Rebuild only when the view pans/zooms or an input changes. (Stay live until tag
        // resolution finishes so all default highlights get seeded first.)
        float pscale = (_window.Height > 0 ? _window.Height / 1600f : 0.675f) * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
        double cxSum = 0, cySum = 0; int onCount = 0; float vw = _window.Width, vh = _window.Height; const float vm = 80f;
        foreach (var n in nodes)
        {
            float sx = n.X * pscale, sy = n.Y * pscale;
            if (sx > vm && sx < vw - vm && sy > vm && sy < vh - vm) { cxSum += n.X; cySum += n.Y; onCount++; }
        }
        long viewSig = onCount == 0 ? 0
            : (long)Math.Round(cxSum / onCount) * 73856093L
            ^ (long)Math.Round(cySum / onCount) * 19349663L
            ^ (long)Math.Round(_atlasZoom * 2000f) * 83492791L;
        long inputSig = (long)(startGrid?.GetHashCode() ?? 0)
            ^ ((long)(goalGrid?.GetHashCode() ?? 0) << 1)
            ^ ((long)(_settings.AtlasHighlightTags?.Count ?? 0) << 20)
            ^ ((long)(_settings.AtlasArrowTags?.Count ?? 0) << 28)
            ^ ((long)sel.Count << 36)
            ^ (_settings.AtlasDrawAll ? 1L << 44 : 0L);
        long sig = viewSig * 2654435761L ^ inputSig;
        sig = sig * 1000003L ^ (curGrid?.GetHashCode() ?? 0);                       // re-solve routes as the player moves
        sig = sig * 1000003L ^ (_settings.AtlasAutoRoute ? 1L : 0L) ^ ((long)_settings.AtlasAutoRouteMaxHops << 1);
        sig = sig * 1000003L ^ (long)(_settings.AtlasNavTags?.Count ?? 0);          // re-solve when the nav set changes
        // Rebuild when the #3 hide filters, #5 icon toggle, or #7 group set change.
        sig = sig * 1000003L ^ (_settings.AtlasHideCompleted ? 2L : 0L) ^ (_settings.AtlasHideAccessible ? 4L : 0L)
            ^ (_settings.AtlasShowContentIcons ? 8L : 0L) ^ ((long)(_settings.AtlasGroups?.Count ?? 0) << 4);
        if (_builtAtlasOnce && _atlas.AllTagsResolved && sig == _lastAtlasSig)
            return;   // view + inputs unchanged → marks/route stay frozen (off-screen arrows don't jitter)
        _lastAtlasSig = sig; _builtAtlasOnce = true;

        // One-time defaults (#6 + #7): seed the built-in "Map Targets" preset (Citadels/bosses/key uniques,
        // matched by exact internal MapId via the maps.json layer and resolved to the live display name so
        // the rules stay editable in the dashboard) + the colour groups. Waits for AllTagsResolved (tag
        // resolution is budget-limited per tick) so the full node set — incl. names/ids — is available.
        SeedAtlasDefaults(nodes);

        // A node matches a rule set if its map name or one of its content tags is in the set; returns the
        // matched tag (drives label + colour). Track set ⇒ draw a ring; Arrow set ⇒ off-screen edge arrow.
        var hlTrack = new HashSet<string>(_settings.AtlasHighlightTags ?? new(), StringComparer.OrdinalIgnoreCase);
        var hlNav = new HashSet<string>(_settings.AtlasNavTags ?? new(), StringComparer.OrdinalIgnoreCase);
        var hlArrow = new HashSet<string>(_settings.AtlasArrowTags ?? new(), StringComparer.OrdinalIgnoreCase);
        // A node matches a rule set if its map name, one of its content tags, OR its map-archetype KIND
        // (Citadel/Boss/Tower/Unique/Merchant — improvement 3) is in the set. Returns the matched token.
        static string? Match(HashSet<string> set, in Poe2Atlas.AtlasNodeLive nd)
        {
            if (set.Count == 0) return null;
            if (!string.IsNullOrEmpty(nd.MapName) && set.Contains(nd.MapName)) return nd.MapName;
            if (nd.Tags is { Count: > 0 }) foreach (var t in nd.Tags) if (set.Contains(t)) return t;
            if (!string.IsNullOrEmpty(nd.Kind) && nd.Kind != "Normal" && set.Contains(nd.Kind)) return nd.Kind;
            // maps.json classification (#7): type (e.g. "unique") + cross-cutting tags (lineage/arbiter),
            // so one-click "route to all uniques / lineage / arbiter" works without listing each map.
            if (!string.IsNullOrEmpty(nd.MapType) && set.Contains(nd.MapType)) return nd.MapType;
            if (nd.MapDataTags is { Count: > 0 }) foreach (var t in nd.MapDataTags) if (set.Contains(t)) return t;
            return null;
        }
        // #7 colour groups: map display name → group colour (the first group containing it). A matched node
        // with no per-rule colour falls back to its group colour. Built once per rebuild.
        Dictionary<string, string>? groupColor = null;
        if (_settings.AtlasGroups is { Count: > 0 } groups)
        {
            groupColor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in groups)
                if (grp?.Maps is { Count: > 0 } && !string.IsNullOrEmpty(grp.Color))
                    foreach (var m in grp.Maps)
                        if (!string.IsNullOrWhiteSpace(m)) groupColor.TryAdd(m, grp.Color);
        }
        var marks = new List<AtlasMark>(128);
        // Routing inputs gathered alongside the marks: grid→node ELEMENT (so the render thread re-reads each
        // route point's live relPos per frame), the tracked tiles to route to, and each tile's ring colour.
        var gridToPoint = new Dictionary<(int, int), AtlasPoint>(nodes.Count);
        var trackedGrids = new List<(int, int)>();
        var gridColor = new Dictionary<(int, int), string?>();
        foreach (var n in nodes) gridToPoint[n.Grid] = new AtlasPoint(n.Element, n.X, n.Y);
        foreach (var n in nodes)
        {
            var selected = sel.Contains(n.Element);
            var mTrack = Match(hlTrack, n);
            var mNav = Match(hlNav, n);
            var mArrow = Match(hlArrow, n);
            var isTracked = selected || mTrack != null;   // Highlight (ring)
            var isNav = mNav != null;                     // Nav-to (route line)
            var isArrow = mArrow != null;                 // Arrow (off-screen pointer)

            // #3 declutter: hide done maps (and optionally accessible-now ones). F10 route endpoints are
            // drawn separately (BuildAtlasRoute), so they survive. Completed maps are already nav-excluded.
            if (_settings.AtlasHideCompleted && n.Completed) continue;
            if (_settings.AtlasHideAccessible && n.Accessible && !n.Completed) continue;

            // #5 on-node content icons: resolve this node's content tags to icon asset basenames. Drawn on
            // tracked nodes and, crucially, on FOGGED nodes the game hides icons on (surfacing what's hidden).
            IReadOnlyList<string>? contentIcons = null;
            if (_settings.AtlasShowContentIcons && n.Tags is { Count: > 0 })
            {
                List<string>? ic = null;
                foreach (var t in n.Tags)
                    if (AtlasMapData.Shared.ContentIcon(t) is { Length: > 0 } bn && (ic ??= new List<string>()).Contains(bn) == false)
                        ic!.Add(bn);
                contentIcons = ic;
            }
            // An untracked node still earns a mark when it's a FOGGED content node with a drawable icon (the
            // renderer draws icons only for !Visible nodes), so we reveal content on un-revealed maps. Otherwise
            // only highlighted/nav/arrow maps draw. AtlasDrawAll debug overrides this to draw every node.
            bool foggedIconNode = contentIcons is { Count: > 0 } && !n.Visible;
            if (!_settings.AtlasDrawAll && !isTracked && !isNav && !isArrow && !foggedIconNode) continue;
            var matched = mTrack ?? mNav ?? mArrow;
            var label = matched ?? (isTracked || isNav || isArrow
                ? (n.Tags is { Count: > 0 } ? n.Tags[0] : (string.IsNullOrEmpty(n.MapName) ? null : n.MapName))
                : null);   // icon-only fogged marks carry no label (icons alone)
            string? color = matched != null && _settings.AtlasHighlightColors.TryGetValue(matched, out var c) ? c
                : (groupColor != null && !string.IsNullOrEmpty(n.MapName) && groupColor.TryGetValue(n.MapName, out var gc) ? gc : null);
            // Route to NAV tiles that aren't already done — independent of the ring/arrow toggles.
            if (isNav && !n.Completed) { trackedGrids.Add(n.Grid); gridColor[n.Grid] = color; }
            marks.Add(new AtlasMark(n.X, n.Y, isTracked, n.HasContent, n.Visited, n.Unlocked, n.Biome, n.IconType, label, color, isArrow, isNav, n.Element, contentIcons, n.Visible));
        }

        // ── Auto-routing (improvement 1): "you are here" + a route to each tracked tile ──────────────
        // Sources = the player's CURRENT atlas node when known, else the accessible-now frontier (every
        // map you can run right now). One multi-source BFS gives the fewest-hops route to each tracked tile.
        AtlasPoint? currentPt = (curGrid is { } cg && gridToPoint.TryGetValue(cg, out var ce)) ? ce : null;
        var autoRoutes = new List<AtlasAutoSpec>();
        if (_settings.AtlasAutoRoute && trackedGrids.Count > 0)
        {
            // Sources = the ACCESSIBLE-NOW frontier (every map you can run right now) — fixed regardless of
            // the mouse. We deliberately do NOT seed from the current-node marker: on some patches that
            // element tracks the HOVERED tile, which made every route re-origin from the cursor ("navs to
            // whatever I mouse over"). Fall back to the marker only when no accessible node is known (rare).
            var sources = new List<(int, int)>();
            foreach (var n in nodes) if (n.Accessible) sources.Add(n.Grid);
            if (sources.Count == 0 && curGrid is { } c0 && _atlas.GraphHas(c0)) sources.Add(c0);
            if (sources.Count > 0)
            {
                foreach (var kv in _atlas.RoutesFromSources(sources, trackedGrids).OrderBy(r => r.Value.Count))
                {
                    int hops = kv.Value.Count - 1;
                    if (hops <= 0) continue;                                       // already on the tile
                    if (_settings.AtlasAutoRouteMaxHops > 0 && hops > _settings.AtlasAutoRouteMaxHops) continue;
                    var pts = new List<AtlasPoint>(kv.Value.Count);
                    foreach (var gp in kv.Value) if (gridToPoint.TryGetValue(gp, out var ep)) pts.Add(ep);
                    if (pts.Count < 2) continue;
                    gridColor.TryGetValue(kv.Key, out var col);
                    autoRoutes.Add(new AtlasAutoSpec(pts, col, hops));
                    if (autoRoutes.Count >= 30) break;                            // keep the view readable
                }
            }
        }

        var (start, end, route) = BuildAtlasRoute(nodes, startGrid, goalGrid);
        _atlasRender = new AtlasRender(true, marks, start, end, route, currentPt, autoRoutes);   // publish atomically
    }

    /// <summary>Resolve the F10 START/END grid coords to canvas-space (relPos) points for the markers, and —
    /// when both are set — A* through the connection graph for the route polyline. All keyed by grid coord,
    /// so the markers + route survive pan/zoom and tiles going off-screen (every canvas child is in
    /// <paramref name="nodes"/>, so its relPos is available even when off-screen). Returns (startPt, endPt,
    /// route) for the caller to fold into the published <see cref="AtlasRender"/>. Logs once when a freshly-set
    /// END produces (or fails to produce) a path, so we can see whether the graph connected the two.</summary>
    private (AtlasPoint? Start, AtlasPoint? End, List<AtlasPoint> Route) BuildAtlasRoute(
        IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes, (int X, int Y)? startGrid, (int X, int Y)? goalGrid)
    {
        var route = new List<AtlasPoint>();
        AtlasPoint? startPt = null, endPt = null;
        if (nodes.Count == 0) return (null, null, route);

        var gridToPoint = new Dictionary<(int, int), AtlasPoint>(nodes.Count);
        foreach (var n in nodes) gridToPoint[n.Grid] = new AtlasPoint(n.Element, n.X, n.Y);

        if (startGrid is { } s && gridToPoint.TryGetValue(s, out var sp)) startPt = sp;
        if (goalGrid is { } g && gridToPoint.TryGetValue(g, out var gp)) endPt = gp;

        if (startGrid is { } start && goalGrid is { } goal)
        {
            var path = _atlas.FindPath(start, goal);
            if (path != null) foreach (var p in path) if (gridToPoint.TryGetValue(p, out var rp)) route.Add(rp);
            // Log once per (start,goal) pair so we can see graph connectivity (or the lack of it).
            if (_loggedRoute != (start, goal))
            {
                _loggedRoute = (start, goal);
                Console.WriteLine($"[atlas route] {start}→{goal}: {(path == null ? $"NO graph path (graph has {_atlas.GraphNodeCount} nodes; start in graph={_atlas.GraphHas(start)}, goal in graph={_atlas.GraphHas(goal)})" : $"{path.Count} hops")}");
            }
        }
        else _loggedRoute = null;
        return (startPt, endPt, route);
    }

    /// <summary>API: set the dashboard-selected atlas nodes (by element address) to highlight in-game.
    /// Draw-only — never sends input to the game. Safe to call from the API thread.</summary>
    public void SetAtlasSelection(IReadOnlyList<long> els)
    {
        lock (_atlasLock) { _atlasSel.Clear(); foreach (var e in els) _atlasSel.Add((nint)e); }
    }

    /// <summary>API: set the active atlas highlight rules (tag + ring colour). Only nodes whose content
    /// tags or map name match one of these are drawn in-game, in the rule's colour. Persisted; applied on
    /// the next world tick. Draw-only.</summary>
    public void SetAtlasHighlight(IReadOnlyList<(string tag, string color, bool track, bool nav, bool arrow)> rules)
    {
        var tags = new List<string>(); var navs = new List<string>(); var arrows = new List<string>();
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, color, track, nav, arrow) in rules)
        {
            if (string.IsNullOrWhiteSpace(tag) || !seen.Add(tag)) continue;
            if (track) tags.Add(tag);
            if (nav) navs.Add(tag);
            if (arrow) arrows.Add(tag);
            if (!string.IsNullOrWhiteSpace(color)) colors[tag] = color;
        }
        _settings.AtlasHighlightTags = tags;
        _settings.AtlasNavTags = navs;
        _settings.AtlasArrowTags = arrows;
        _settings.AtlasHighlightColors = colors;
        _settings.AtlasRulesInitialized = true;   // any explicit edit locks out the Citadel default-seed
        _settings.Save();
    }
}
