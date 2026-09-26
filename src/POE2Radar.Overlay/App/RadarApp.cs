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

/// <summary>
/// Drives the PoE2 radar: per-tick resolve chain → read player/entities/terrain/map → render.
/// Read-only. Render rate is configurable (RadarSettings.FpsCap, default 60 Hz; player blip tracks
/// live); the heavier entity/terrain walk runs at ~30 Hz. Projection scale/offset are tweakable live
/// via hotkeys for calibration.
/// </summary>
public sealed partial class RadarApp : IDisposable
{
    private const int WorldHz = 30;

    private readonly ProcessHandle _process;

    // Three INDEPENDENT reader stacks over the one shared ProcessHandle (ReadProcessMemory is itself
    // concurrency-safe; the per-instance buffers + caches in MemoryReader/Poe2Live are NOT). Each thread
    // owns its own so nothing mutable is shared: _live = world thread (entity/terrain/landmark walk),
    // _liveRender = render thread (player/vitals/camera/map + HP-bar live reads), _liveApi = HTTP thread
    // (tile-path scans). _atlas is internally locked, so it's shared across all three.
    private readonly MemoryReader _reader;

    private readonly Poe2Live _live;

    private readonly MemoryReader _readerRender;

    private readonly Poe2Live _liveRender;

    private readonly MemoryReader _readerApi;

    private readonly Poe2Live _liveApi;

    private readonly OverlayWindow _window;

    private readonly OverlayRenderer _renderer;

    private readonly ApiServer _api;

    private readonly RadarSettings _settings;

    private readonly HiddenEntities _hidden;

    private readonly WatchedEntities _watched;

    private readonly LandmarkPatterns _landmarkPatterns;

    private readonly DisplayRules _displayRules;

    // Cached delegates for the per-frame RenderContext, so we don't allocate a method-group delegate +
    // closure every render frame. Bound once after _displayRules is constructed.
    private Func<Poe2Live.EntityDot, DisplayRule?>? _resolveEntity;

    private Func<string, DisplayRule?>? _resolveTileDraw;

    private readonly LandmarkStore _landmarkStore;

    private int _landmarkGen;

    private int _displayRulesGen;

    private int _landmarkStoreGen;

    private int _appliedClusterGap;

    private string _appliedLeague = "";

    private nint _areaInstanceForApi;

    private nint _inGameStateForApi;

    private volatile RadarState _state = RadarState.Empty;

    // ── Atlas overlay: live node highlights (takes precedence over the radar when the atlas is open). ──
    // The render-consumed outputs (open flag + marks + route) are published as ONE immutable record the
    // world thread swaps atomically and the render thread reads lock-free — same lock-free-snapshot idiom
    // as _world / _state below.
    // A route/marker point: the node's UiElement (so the render thread re-reads live RelativePos per frame →
    // smooth pan) PLUS the world-walk's baked position as a fallback when that read is rejected (stale/freed
    // element scrolled off-screen, or garbage) — without the fallback those bad reads streaked lines.
    private readonly record struct AtlasPoint(nint El, float Bx, float By);

    // ── Runeforge ("Runeshape Combinations") priced-reward labels: same lock-free published-record idiom.
    //    Built on the world thread (panel read + price lookup), read by the render thread. ──
    private sealed record RuneRender(bool Open, IReadOnlyList<RuneLabel> Labels)
    {
        public static readonly RuneRender Closed = new(false, Array.Empty<RuneLabel>());
    }

    // ── Currency Exchange (Kalguur market) order-book depth: same lock-free published-record idiom as
    //    Runeforge. Built on the world thread (panel read + ladder aggregation), read by the render thread.
    //    ExchangeRow is defined in RenderContext.cs so the ctx can carry it directly. ──
    private sealed record ExchangeRender(bool Open, IReadOnlyList<ExchangeRow> Offered, IReadOnlyList<ExchangeRow> Wanted,
        string Summary, int HaveQty, string FillNote, float PanelX, float PanelY, bool Collapsed)
    {
        public static readonly ExchangeRender Closed = new(false, Array.Empty<ExchangeRow>(), Array.Empty<ExchangeRow>(), "", 0, "", 0f, 0f, false);
    }

    // ── Ritual tribute-shop priced-reward labels: same lock-free published-record idiom. Built on the world
    //    thread (read the 5 reward tiles' item entities + price each), drawn by the render thread on each tile.
    private sealed record RitualRender(bool Open, IReadOnlyList<RitualLabel> Labels)
    {
        public static readonly RitualRender Closed = new(false, Array.Empty<RitualLabel>());
    }

    // ── Loot-tag value chips: the WORLD thread scans the visible UI tree for tag text, matches each to a
    //    priced item by name, and publishes a spec per match (the tag's UiElement address + value). The
    //    RENDER thread re-reads each element's LIVE screen rect (game-computed → smooth) and draws on it.
    //    Same lock-free published-record idiom as the runeforge/HP-bar pipelines. ──
    private readonly record struct LootTagSpec(nint El, string TagText, string Value, bool Highlight);

    private sealed record LootTagRender(IReadOnlyList<LootTagSpec> Specs)
    {
        public static readonly LootTagRender Empty = new(Array.Empty<LootTagSpec>());
    }

    private DateTime _nextLootScanUtc = DateTime.MinValue;

    private const int LootScanThrottleMs = 200;

    // ── Hover price: the item under the cursor in an item UI (inventory/stash/vendor). Scanned at world
    //    rate (cursor-in-rect walk → +0x4F8 item → price + the item SLOT rect), published as a spec the
    //    render thread draws beside the item. Text is the estimate/state; Sub contains newline-separated
    //    identity, stack breakdown, confidence, source/freshness and comparison shortcut. ──
    private readonly record struct HoverPriceSpec(float BoxX, float BoxY, float BoxW, float BoxH,
        string Text, string Sub, bool Highlight, bool Danger = false);

    private sealed record HoverPriceRender(HoverPriceSpec Spec, uint AreaHash, DateTime UpdatedUtc);

    private DateTime _nextHoverScanUtc = DateTime.MinValue;

    private const int HoverScanThrottleMs = 120;

    private sealed record MonolithRender(uint AreaHash, IReadOnlyList<MonolithMarker> Markers)
    {
        public static readonly MonolithRender Empty = new(0, Array.Empty<MonolithMarker>());
    }

    private DateTime _nextInspectAt = DateTime.MinValue;

    private volatile UpdateChecker.Result? _update;

    /// <summary>Directory holding the user config files (shared with <see cref="RadarSettings"/>).</summary>
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");

    // ── World-thread working fields (written ONLY by the world tick; never read by the render thread —
    //    the render thread reads the published _world snapshot instead). ──
    private Thread? _worldThread;

    private List<Poe2Live.EntityDot> _entities = new();

    // Monster HP-bar pipeline: the SPEC (style + which mobs get a bar + their component addresses) is
    // rebuilt at WORLD rate; _hpFrame (live position + HP) is rebuilt every RENDER frame from cheap per-mob
    // reads (via the spec's captured addresses) so bars track moving monsters smoothly without re-walking.
    private readonly record struct HpBarSpec(nint Entity, nint Render, nint Life, float Width, uint Fill, float BorderWidth, uint Border);

    // Ground-item label SPEC (world rate): the priced facts + the item's Render component address. Its
    // live world position is re-read every RENDER frame into _itemFrame so the label tracks smoothly
    // (dropped items bob, so a 30 Hz-sampled position aliases/jitters when projected at render rate —
    // same reason HP bars re-read per frame).
    private readonly record struct ItemLabelSpec(nint Render, string Name, string Value, bool Highlight, bool ShowName);

    private IReadOnlyList<Poe2Live.Landmark> _landmarks = Array.Empty<Poe2Live.Landmark>();

    private Poe2Live.TerrainData? _terrain;

    private int _charLevel;

    private nint _lastAreaInstance;

    // ── Published lock-free snapshot: the world tick swaps this whole immutable record; the render thread
    //    reads it once per frame. Same idiom as _state / _atlasRender. ──
    private sealed record WorldSnapshot(
        bool InGame, uint AreaHash, int AreaLevel, string AreaCode, int CharLevel,
        IReadOnlyList<Poe2Live.EntityDot> Entities,
        IReadOnlyList<Poe2Live.Landmark> Landmarks,
        Poe2Live.TerrainData? Terrain,
        IReadOnlyList<HpBarSpec> HpSpecs,
        IReadOnlyList<ItemLabelSpec> ItemLabels,
        IReadOnlyList<SelectedPath> SelectedPaths,
        IReadOnlyList<LegendEntry> Legend,
        IReadOnlyList<string> SelectedSnapshot)
    {
        public DateTime ObservedAt { get; init; } = DateTime.UtcNow;
        public static readonly WorldSnapshot Empty = new(
            false, 0, 0, "", 0, Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), null,
            Array.Empty<HpBarSpec>(), Array.Empty<ItemLabelSpec>(), Array.Empty<SelectedPath>(),
            Array.Empty<LegendEntry>(), Array.Empty<string>());
    }

    private volatile WorldSnapshot _world = WorldSnapshot.Empty;

    private NumVec2 _worldPlayer;

    private volatile float _worldMs, _renderMs;

    private volatile float _fps;

    private int _autoHz = 144;

    private bool _autoHzLogged;

    private DateTime _nextHzCheckUtc = DateTime.MinValue;

    private uint _areaHash;

    private nint _gameHwnd;

    private volatile bool _shutdown;

    private volatile string _charName = "";

    private nint _charNameFor;

    private float[]? _cameraMatrix;

    private bool _overlayHadContent;

    private readonly List<uint> _zoneOrder = new();

    private const int MaxRememberedZones = 64;

    public void RequestShutdown() => _shutdown = true;

    /// <summary>Is PoE2 the focused window? Every draw/input gate goes through this (thread-safe; on Hyprland it
    /// asks the compositor — see <see cref="GameHost.IsGameForeground"/>).</summary>
    private bool GameFocused() => _gameHwnd != 0 && GameHost.IsGameForeground(_gameHwnd, _process.ProcessId);

    private DateTime _nextWindowRefreshUtc;

    public RadarApp(ProcessHandle process, MemoryReader reader, nint gameStateSlot)
    {
        _process = process;
        _reader = reader;
        _settings = RadarSettings.Load();
        _autoFlask = _settings.AutoFlaskEnabled;   // restore the persisted F8 state (default ON)
        _buffKeeperArmed = _settings.BuffKeeper.Enabled;   // restore the persisted buff-keeper arm (default OFF)
        _isDownMemo = IsDownMemo;
        _chat = new ChatSender(new GameChatInput(), CanSendChat);
        (_trade, _tradeHistory, _tradeLog) = CreateTrade();
        _tradeLog.Start();
        Console.WriteLine($"Settings: {RadarSettings.FilePath}");
        Console.WriteLine($"Entity names: {EntityNameResolver.Shared.Count} mappings; zones: {ZoneGuide.Shared.Count}");
        _live = new Poe2Live(reader, gameStateSlot);
        // Independent reader stacks for the render + API threads (see the field declarations): each owns
        // its own MemoryReader/Poe2Live so the world walk, the render-frame reads, and the API tile scan
        // never share the non-thread-safe per-instance buffers/caches. All read the one shared handle.
        _readerRender = new MemoryReader(process);
        _liveRender = new Poe2Live(_readerRender, gameStateSlot);
        _readerApi = new MemoryReader(process);
        _liveApi = new Poe2Live(_readerApi, gameStateSlot);
        _atlas = new Poe2Atlas(reader);
        _runeforge = new Poe2Runeforge(reader);   // world-thread reader stack
        _exchange = new Poe2CurrencyExchange(reader); // world-thread reader stack (same as _runeforge)
        _window = OverlayWindow.Create();
        _renderer = new OverlayRenderer(_window);
        // Clicking a legend row toggles that landmark in the path selection. Purely local UI — the
        // click lands on our own overlay window (never forwarded to the game). See UpdateClickThrough.
        _window.OnClientClick = OnOverlayClick;
        _hidden = new HiddenEntities(Path.Combine(ConfigDir, "hidden_entities.json"));
        _watched = new WatchedEntities(Path.Combine(ConfigDir, "watched_entities.json"));
        _landmarkPatterns = new LandmarkPatterns(Path.Combine(ConfigDir, "landmark_patterns.json"));
        _live.CustomLandmarkMatch = TileLandmarkMatch; // surface tiles via landmark patterns + Tile rules
        _landmarkGen = _landmarkPatterns.Generation;
        _live.LandmarkClusterGap = _settings.LandmarkClusterGap;
        _appliedClusterGap = _settings.LandmarkClusterGap;
        // Unified display ruleset — single source of truth for the entity dot decision. On first run
        // (no display_rules.json) seed it from the legacy category styles + mechanics + watched rules
        // so behavior is identical; thereafter it's the authoritative, editable, ordered ruleset.
        _displayRules = new DisplayRules(Path.Combine(ConfigDir, "display_rules.json"));
        _resolveEntity = _displayRules.Resolve;
        _resolveTileDraw = p => _displayRules.ResolveTile(p, requireMatch: false);
        if (_displayRules.Count == 0)
        {
            _displayRules.Replace(DisplayRules.BuildDefault(
                _settings.Styles, _settings.ShowMonsters, _watched.All));
            Console.WriteLine($"Display rules: seeded {_displayRules.Count} from legacy config (first run).");
        }
        // One-time: fold any user landmark-tile patterns into Tile display rules (the unified system),
        // then clear the old config so it's retired and won't double-apply or re-migrate.
        if (_landmarkPatterns.All.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var seen = new HashSet<string>(
                rules.Where(r => r.Categories.Contains("Tile")).SelectMany(r => r.Match), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var lp in _landmarkPatterns.All)
            {
                if (!seen.Add(lp.Pattern)) continue;
                rules.Add(new DisplayRule
                {
                    Enabled = lp.Enabled, Name = string.IsNullOrWhiteSpace(lp.Label) ? lp.Pattern : lp.Label,
                    Categories = new() { "Tile" }, Match = new() { lp.Pattern },
                    Shape = "Diamond", Color = "#F259F2", Opacity = 1f, Size = 5f, Navigable = true,
                    Label = string.IsNullOrWhiteSpace(lp.Label) ? null : lp.Label,
                });
                added++;
            }
            if (added > 0) _displayRules.Replace(rules);
            foreach (var lp in _landmarkPatterns.All.ToList()) _landmarkPatterns.Remove(lp.Pattern);
            Console.WriteLine($"Migrated {added} landmark-tile pattern(s) into Tile display rules.");
        }
        // One-time: fold the old AutoNavPatterns list onto matching rules' Auto-path flag (a rule auto-
        // paths when one of its match terms overlaps a pattern), then retire the list. Preserves the
        // "auto-path to the expedition encounter on zone entry" default.
        if (_settings.AutoNavPatterns.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var pats = _settings.AutoNavPatterns;
            var changed = false;
            foreach (var r in rules)
            {
                if (r.Navigable) continue;
                if (r.Match.Any(m => pats.Any(p =>
                        m.Contains(p, StringComparison.OrdinalIgnoreCase) || p.Contains(m, StringComparison.OrdinalIgnoreCase))))
                { r.Navigable = true; changed = true; }
            }
            if (changed) _displayRules.Replace(rules);
            _settings.AutoNavPatterns = new(); _settings.Save();
            Console.WriteLine("Migrated auto-path patterns onto display rules' Auto-path flag.");
        }
        // One-time: seed a default rule that flags Abyss "Lightless" (Amanamu void) monsters — the
        // dangerous void-cloud mobs. Same idea as the community AmanamuVoidAlert plugin's PRIMARY detector:
        // match the monster's affix-mod ids (we read ObjectMagicProperties+Mods). Placed before the generic
        // "Monster · <rarity>" rules so it wins. Guarded by a seed flag so deleting it from the dashboard
        // sticks. NOTE: this matches on the affix-mod ids we read (+0x168); if the abyss faction mod proves
        // to live in the ModNames vector (+0x150) we don't currently read, this won't fire and we extend the
        // read — verify live via the dashboard Entities view on an Abyss monster.
        if (!_settings.AbyssRuleSeeded)
        {
            const string abyssRuleName = "Abyss Lightless (Void)";
            var rules = _displayRules.All.ToList();
            if (!rules.Any(r => string.Equals(r.Name, abyssRuleName, StringComparison.Ordinal)))
            {
                var idx = rules.FindIndex(r => r.Name.StartsWith("Monster ·", StringComparison.Ordinal));
                var abyssRule = new DisplayRule
                {
                    Name = abyssRuleName,
                    Categories = new() { "Monster" },
                    Mods = new() { "AbyssLightless", "LightlessWell", "Lightless" }, // affix-mod id terms (ANY-of)
                    Shape = "Exclamation", Color = "#B450FF", Opacity = 1f, Size = 6f, Label = "VOID",
                };
                if (idx >= 0) rules.Insert(idx, abyssRule); else rules.Add(abyssRule);
                _displayRules.Replace(rules);
                Console.WriteLine("Display rules: seeded default Abyss Lightless (Void) monster rule.");
            }
            _settings.AbyssRuleSeeded = true; _settings.Save();
        }
        // One-time: fold the built-in tracked-tile rules (WaygateDevice waygate, …) into existing configs.
        // Fresh configs already get these via DisplayRules.BuildDefault; this covers configs that predate them.
        // Additive + name-guarded so it never duplicates a rule and a user's deletion of one sticks.
        if (!_settings.BuiltInTileRulesSeeded)
        {
            var rules = _displayRules.All.ToList();
            var added = 0;
            foreach (var tile in DisplayRules.BuiltInTileRules())
            {
                if (rules.Any(r => string.Equals(r.Name, tile.Name, StringComparison.Ordinal))) continue;
                rules.Add(tile); added++;
            }
            if (added > 0)
            {
                _displayRules.Replace(rules);
                Console.WriteLine($"Display rules: seeded {added} built-in tracked-tile rule(s).");
            }
            _settings.BuiltInTileRulesSeeded = true; _settings.Save();
        }
        // One-time (v2): apply the curated icon glyphs to the STOCK display rules. Names are matched
        // SEPARATOR-INSENSITIVELY (normalized to lowercase alphanumerics) because the stock names contain a
        // "·" whose code point didn't match a literal key in the v1 pass — silently skipping Monster·Unique
        // and the chests. Each entry only retouches a rule still on its OLD default shape, so user
        // customizations are preserved; idempotent (already-applied rules no longer match their old shape).
        if (!_settings.IconDefaultsApplied2)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var iconMap = new Dictionary<string, (string from, string to)>(StringComparer.Ordinal)
            {
                ["monsterunique"]      = ("Star", "Skull"),
                ["monstermagic"]       = ("Diamond", "Fang"),
                ["monsterrare"]        = ("Triangle", "Claw"),
                ["player"]             = ("Circle", "Person"),
                ["npc"]                = ("Plus", "Chat"),
                ["chestrare"]          = ("Square", "Chest"),
                ["chestunique"]        = ("Square", "Crown"),
                ["transition"]         = ("Diamond", "Stairs"),
                ["pointofinterest"]    = ("Circle", "MapPin"),
                ["breach"]             = ("Diamond", "Portal"),
                ["essence"]            = ("Triangle", "Flask"),
                ["expedition"]         = ("Plus", "Flag"),
                ["strongbox"]          = ("Square", "Chest"),
                ["abysslightlessvoid"] = ("Diamond", "Exclamation"),
            };
            var rules = _displayRules.All.ToList();
            var changed = 0;
            foreach (var r in rules)
                if (iconMap.TryGetValue(Norm(r.Name), out var m) && string.Equals(r.Shape, m.from, StringComparison.OrdinalIgnoreCase))
                { r.Shape = m.to; changed++; }
            if (changed > 0) _displayRules.Replace(rules);
            _settings.IconDefaultsApplied = true; _settings.IconDefaultsApplied2 = true; _settings.Save();
            Console.WriteLine($"Display rules: applied curated icon glyphs to {changed} stock rule(s).");
        }
        // One-time cleanup: the legacy "watched" defaults were seeded as Diamond, (any)-category rules placed
        // BEFORE the mechanic rules, so they shadowed them (everything drew as a diamond) — and the bare
        // "Ritual"/"Breach"/"Essence" mechanic matches with no category gate tagged the leagues' MONSTERS.
        if (!_settings.RuleCleanupV1)
        {
            static bool AnyCat(DisplayRule r) => r.Categories is null or { Count: 0 };
            var rules = _displayRules.All.ToList();
            var before = rules.Count;
            // a) Drop the stale Diamond duplicates that shadow a mechanic rule (matched by their target path).
            var dupMatches = new[] { "LeagueRitual", "Expedition2/Expedition2Encounter", "StrongBoxes", "Metadata/Shrines/" };
            rules.RemoveAll(r => string.Equals(r.Shape, "Diamond", StringComparison.OrdinalIgnoreCase)
                && AnyCat(r) && r.Match is { Count: > 0 } && r.Match.Any(m => dupMatches.Contains(m)));
            // b) Gate the broad mechanic rules to the marker (Object/Other) so they never tag monsters.
            foreach (var r in rules)
                if (AnyCat(r) && (string.Equals(r.Name, "Ritual", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.Name, "Breach", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.Name, "Essence", StringComparison.OrdinalIgnoreCase)))
                    r.Categories = new List<string> { "Object", "Other" };
            // c) Reskin the remaining navigation-POI diamonds to sensible glyphs (only if still Diamond).
            var poiIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Waypoint"] = "MapPin", ["Checkpoint"] = "Flag", ["Entrance"] = "Stairs", ["Stash"] = "Chest",
                ["Portal"] = "Portal", ["Town Portal"] = "Portal", ["Quest Chest"] = "Chest", ["Quest Object"] = "Exclamation",
                ["Quest Marker"] = "Exclamation", ["Reforging Bench"] = "Coin", ["Crafting Bench"] = "Coin", ["Abyss Crack"] = "Exclamation",
            };
            foreach (var r in rules)
                if (string.Equals(r.Shape, "Diamond", StringComparison.OrdinalIgnoreCase) && poiIcons.TryGetValue(r.Name, out var ic))
                    r.Shape = ic;
            _displayRules.Replace(rules);
            _settings.RuleCleanupV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: cleanup removed {before - rules.Count} stale duplicate(s), gated mechanic rules, reskinned POIs.");
        }
        // One-time: give the non-monster mechanic/special rules a default in-game LABEL where they had none,
        // so their marker shows text (Expedition/Ritual/Breach already had labels from the legacy watched set;
        // Strongbox/Essence/Shrine/Transition/chests did not). Only fills an empty label (never overwrites).
        if (!_settings.MechanicLabelsV1)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var labelMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["strongbox"] = "Strongbox", ["essence"] = "Essence", ["shrine"] = "Shrine",
                ["transition"] = "Transition", ["chestunique"] = "Unique Chest", ["chestrare"] = "Rare Chest",
            };
            var rules = _displayRules.All.ToList();
            var n = 0;
            foreach (var r in rules)
                if (string.IsNullOrEmpty(r.Label) && labelMap.TryGetValue(Norm(r.Name), out var lbl)) { r.Label = lbl; n++; }
            if (n > 0) _displayRules.Replace(rules);
            _settings.MechanicLabelsV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: added default labels to {n} non-monster rule(s).");
        }
        // One-time: broaden the ground-item categories from the old 4 to the full high-value set (now that
        // non-uniques actually price + draw). Only replaces the EXACT old default, so a custom set is kept.
        if (!_settings.GroundDefaultsV2)
        {
            var cur = _settings.GroundItems.Categories ?? new();
            var old = new HashSet<string>(new[] { "Uniques", "Runes", "Essences", "Currency" }, StringComparer.OrdinalIgnoreCase);
            if (cur.Count == old.Count && cur.All(old.Contains))
            {
                _settings.GroundItems.Categories = new GroundItemSettings().Categories; // the new broad default
                Console.WriteLine("Ground items: broadened category set to the full high-value default.");
            }
            _settings.GroundDefaultsV2 = true; _settings.Save();
        }
        // One-time: bump monster Magic/Rare/Unique rule sizes — the Fang/Claw/Skull glyphs are far less
        // legible than the old flat shapes at the same radar size. Only retouches a rule still on its OLD
        // default size (within a small epsilon), so a size you've customized is preserved.
        if (!_settings.IconSizesV1)
        {
            static string Norm(string s)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                return sb.ToString();
            }
            var sizeMap = new Dictionary<string, (float from, float to)>(StringComparer.Ordinal)
            {
                ["monstermagic"]  = (3.4f, 5.5f),
                ["monsterrare"]   = (5.5f, 7.5f),
                ["monsterunique"] = (6.5f, 8.0f),
            };
            var rules = _displayRules.All.ToList();
            var n = 0;
            foreach (var r in rules)
                if (sizeMap.TryGetValue(Norm(r.Name), out var m) && Math.Abs(r.Size - m.from) < 0.05f)
                { r.Size = m.to; n++; }
            if (n > 0) _displayRules.Replace(rules);
            _settings.IconSizesV1 = true; _settings.Save();
            Console.WriteLine($"Display rules: bumped {n} monster icon size(s) for glyph legibility.");
        }
        _displayRulesGen = _displayRules.Generation;
        // User-editable overlay on the baked curated landmark table (the "Landmarks" tab). Inject its
        // lookup so the landmark scan honors user edits on top of the shipped community data.
        _landmarkStore = new LandmarkStore(Path.Combine(ConfigDir, "landmarks.json"));
        _live.CuratedLookup = _landmarkStore.Lookup;
        _landmarkStoreGen = _landmarkStore.Generation;
        _modCatalog = new ModCatalog(Path.Combine(ConfigDir, "known_mods.json"));
        _priceBook = new Pricing.PriceBook(Path.Combine(ConfigDir, "price_cache.json"), _settings.GroundItems.League);
        _priceBook.RefreshIfDue(); // kick a background fetch on startup if the cache is stale
        Console.WriteLine($"Hidden entities: {_hidden.Count} pattern(s); display rules: {_displayRules.Count}; known mods: {_modCatalog.Count}");
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection,
                             _hidden, _displayRules, _landmarkStore, CurrentTilePaths, () => _modCatalog.All, PricesJson, AtlasJson, SetAtlasSelection,
                             SetAtlasHighlight, VersionJson, _settings.ApiPort)
        {
            BuffsProvider = BuffsJson,
            TradeProvider = TradeJson,
            TradeCommand = TradeCommandJson,
        };
        try { _api.Start(); Console.WriteLine($"API on http://localhost:{_settings.ApiPort} (dashboard at /)"); }
        catch (Exception ex) { Console.Error.WriteLine($"API server disabled: {ex.Message}"); }
        Console.WriteLine("Hotkeys: F6=route to nearest  F7=clear routes  F8=auto-flask  F9=quit  F12=open dashboard  Insert=menu");
        Console.WriteLine("         F10 (Atlas open) = inspect hovered tile (dumps map name + code + content"
                          + " to console for web-UI filters) and set route START->END (3rd press resets)");
        // Best-effort version check against GitHub (non-blocking; never fails startup).
        _ = Task.Run(async () =>
        {
            var u = await UpdateChecker.CheckAsync();
            _update = u;
            if (u.UpdateAvailable)
                Console.WriteLine($"\n*** UPDATE AVAILABLE: {u.Latest} — you have v{u.Current}. Download: {u.Url} ***\n");
            else
                Console.WriteLine($"POE2Radar v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
        });
    }

    private object VersionJson()
    {
        var u = _update;
        return new
        {
            current = u?.Current ?? UpdateChecker.Current,
            latest = u?.Latest,
            updateAvailable = u?.UpdateAvailable ?? false,
            url = u?.Url ?? UpdateChecker.ReleasesPage,
        };
    }

    public void Run()
    {
        _gameHwnd = GameHost.FindWindowForProcess(_process.ProcessId);
        // The heavy world-rate walk runs on its OWN thread (Phase 3); the render loop below only does
        // fast per-frame reads + draw, so a slow world pass (big pack, zone load) never hitches frames.
        _worldThread = new Thread(WorldLoop) { IsBackground = true, Name = "POE2Radar.World" };
        _worldThread.Start();
        GameHost.BeginHighResTimer();   // 1 ms timer resolution on Windows so the frame pacer can hit FpsCap
        try
        {
            var frameSw = System.Diagnostics.Stopwatch.StartNew();
            var fpsSw = System.Diagnostics.Stopwatch.StartNew();
            var fpsFrames = 0;
            while (!_shutdown)
            {
                frameSw.Restart();
                // Re-find the game window every few seconds, not just once: Wine can recreate its toplevel (e.g. on a
                // fullscreen/borderless switch), which would otherwise leave the overlay tracking a dead window.
                if (_gameHwnd == 0 || DateTime.UtcNow >= _nextWindowRefreshUtc)
                {
                    _nextWindowRefreshUtc = DateTime.UtcNow.AddSeconds(3);
                    var found = GameHost.FindWindowForProcess(_process.ProcessId);
                    if (found != 0) _gameHwnd = found;
                }
                if (_gameHwnd != 0) _window.TrackGameWindow(_gameHwnd);
                if (!_window.PumpMessages()) break;
                Tick();

                // Effective render FPS over a rolling ~500 ms window (actual loop iterations/sec, after
                // pacing) — exposed via /state so we can verify we're truly hitting FpsCap, not just asking.
                if (++fpsFrames >= 1 && fpsSw.ElapsedMilliseconds >= 500)
                {
                    _fps = (float)(fpsFrames * 1000.0 / fpsSw.Elapsed.TotalMilliseconds);
                    fpsFrames = 0; fpsSw.Restart();
                }
                // Pace to the configured cap against ELAPSED time (incl. the Tick render cost), not a fixed
                // sleep on top of it — otherwise effective fps = 1000/(budget+renderMs), always below the cap.
                // FpsCap <= 0 means "auto-match the monitor the game is on" (re-detected ~1/s; logged on change).
                // Read live so dashboard edits apply immediately.
                int hz;
                if (_settings.FpsCap > 0) hz = Math.Clamp(_settings.FpsCap, 15, 360);
                else
                {
                    var nowHz = DateTime.UtcNow;
                    if (nowHz >= _nextHzCheckUtc)
                    {
                        _nextHzCheckUtc = nowHz.AddSeconds(1);
                        var det = GameHost.DetectMonitorHz(_gameHwnd);
                        if (det > 0)
                        {
                            var clamped = Math.Clamp(det, 30, 360);
                            if (clamped != _autoHz || !_autoHzLogged)
                            {
                                _autoHz = clamped; _autoHzLogged = true;
                                Console.WriteLine($"Auto FPS cap: {_autoHz} Hz (game monitor refresh).");
                            }
                        }
                    }
                    hz = _autoHz;
                }
                var budgetMs = 1000.0 / hz;
                var remaining = budgetMs - frameSw.Elapsed.TotalMilliseconds;
                // Coarse-sleep most of the remainder (1 ms accurate now), then spin the last ~1.5 ms for a
                // tight, low-jitter frame interval — what high-refresh tracking needs.
                if (remaining > 2.0) Thread.Sleep((int)(remaining - 1.5));
                while (frameSw.Elapsed.TotalMilliseconds < budgetMs) Thread.SpinWait(64);
            }
        }
        finally { GameHost.EndHighResTimer(); }
    }

    private (( int, int) s, (int, int) g)? _loggedRoute;

    public void Dispose()
    {
        _shutdown = true;
        _tradeLog.Dispose();
        _chat.Dispose();
        GameHost.RestoreInputState();
        _worldThread?.Join(1000);   // let the background world loop observe _shutdown and exit
        _modCatalog.Flush(); // persist any mods seen since the last debounced write
        _replanner.Dispose();
        _api.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
