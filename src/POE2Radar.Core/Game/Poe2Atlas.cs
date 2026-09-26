namespace POE2Radar.Core.Game;

/// <summary>
/// Read-only reader for the PoE2 Atlas. Exposes (a) the map-archetype CATALOG + current-region map set
/// (for the dashboard's Atlas tab), and (b) the LIVE NODE GRAPH — atlas nodes are UiElements (one
/// vtable subclass) carrying per-node id/biome/content/flags/completion + a live screen-space position
/// (see <see cref="ReadNodes"/>). Node offsets validated live 2026-06-07 (resources/additional offsets.txt
/// + atlas-research-notes.md).
///
/// <para><b>Catalog</b> — an array of 0x18-byte entries <c>{int32 id; IntPtr parsedObj (stride 0x300);
/// IntPtr idStr → UTF-16 "MapXxx"}</c>. The display name follows the code inline in the dat row.
/// 139 entries seen live (all biomes, towers, uniques, Citadel/uber-boss maps).</para>
///
/// <para><b>Region set</b> — a 0x18-byte array <c>{IntPtr record; IntPtr archetype (a catalog parsedObj);
/// IntPtr sharedConst}</c>: one entry per map type present in the current atlas view.</para>
///
/// <para>Addresses are session-specific, so both structures are LOCATED by signature scan over the
/// game-data arena and cached (re-validated cheaply on each read; re-scanned only if the cache goes
/// stale). The catalog signature — a run of entries whose idStr reads "Map…" — is unambiguous.</para>
/// </summary>
public sealed partial class Poe2Atlas
{
    private readonly MemoryReader _reader;

    private readonly object _lock = new();

    private volatile int _catalogBaseLo, _catalogBaseHi;

    private int _catalogCount;

    private volatile bool _scanning;

    private nint _scanLo, _scanHi;

    private nint CatalogBase
    {
        get => (nint)(((long)(uint)_catalogBaseHi << 32) | (uint)_catalogBaseLo);
        set { _catalogBaseLo = (int)(long)value; _catalogBaseHi = (int)((long)value >> 32); }
    }

    private const int Stride = 0x18;

    private static readonly byte[] MapPrefix = { 0x4D, 0x00, 0x61, 0x00, 0x70, 0x00 };

    public Poe2Atlas(MemoryReader reader) => _reader = reader;

    /// <summary>One map archetype: its internal code ("MapSteppe"), display name ("Steppe"), an id
    /// (tracks roughly with tier/level), and the address of its parsed runtime object.</summary>
    public readonly record struct MapType(int Id, string Code, string Name, string Kind, long ParsedObj, long IdStr);

    /// <summary>One map type present in the current atlas region (resolved to its archetype code/name).</summary>
    public readonly record struct RegionMap(string Code, string Name, string Kind, long Record);

    /// <summary>The full read result. <see cref="Located"/> is false when the catalog can't be found
    /// (not in/near the atlas, or the layout drifted) — the dashboard shows that state rather than guessing.</summary>
    public sealed record AtlasData(
        bool Located, long CatalogAddr, int CatalogCount,
        IReadOnlyList<MapType> Catalog, IReadOnlyList<RegionMap> Region, string Note);

    /// <summary>Locate (cached) + read the catalog and current-region map set. Thread-safe; safe to call
    /// from the API thread concurrently with the tick loop (independent reads on the same handle).
    /// <para><paramref name="anchor"/> is any live in-arena address (e.g. the current AreaInstance): the
    /// catalog lives in the same heap slab, so we scan the 1 TB-aligned slab containing the anchor —
    /// robust to ASLR across sessions. Pass 0 only as a last resort (scans every readable region).</para></summary>
    public AtlasData Read(nint anchor = 0)
    {
        var baseAddr = CatalogBase;
        // Cache hit: re-validate cheaply, then walk (fast). No scan.
        if (baseAddr != 0 && IsCatalogEntry(baseAddr) && IsCatalogEntry(baseAddr + Stride))
        {
            var catalog = WalkCatalog(baseAddr, _catalogCount);
            if (catalog.Count == 0) { CatalogBase = 0; return NotLocated("Catalog cache stale; refresh to re-scan."); }
            var byParsed = new Dictionary<nint, MapType>(catalog.Count);
            foreach (var m in catalog) byParsed[(nint)m.ParsedObj] = m;
            var region = ReadRegion(byParsed, baseAddr);
            var note = region.Count == 0 ? "Current-region map set not located (catalog still valid)." : "";
            return new AtlasData(true, (long)baseAddr, catalog.Count, catalog, region, note);
        }

        // Not located yet — kick off (or report) a one-time BACKGROUND scan. The slab scan is seconds-
        // long, so it must not block the API thread; the dashboard polls/refreshes until it's ready.
        if (_scanning) return NotLocated("Scanning game memory for the atlas catalog… (one-time, ~1–2 min) — refresh shortly.");
        if (anchor == 0) return NotLocated("Not in game / no anchor yet — open the Atlas, then refresh.");

        var lo = (nint)((long)anchor & ~0xFF_FFFF_FFFFL);          // 1 TB-aligned slab containing the anchor
        var hi = (nint)((long)lo + 0x100_0000_0000L);
        lock (_lock)
        {
            if (_scanning || CatalogBase != 0) return Read(anchor); // another thread won the race
            _scanning = true;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _scanLo = lo; _scanHi = hi;
                // The catalog (loaded at startup) is typically BELOW the live AreaInstance — scan there
                // first (roughly halves the work), then above only if needed.
                if (!ScanForCatalog(lo, anchor)) ScanForCatalog(anchor, hi);
            }
            finally { _scanning = false; }
        });
        return NotLocated("Scanning game memory for the atlas catalog… (one-time, ~1–2 min) — refresh shortly.");
    }

    private static AtlasData NotLocated(string note)
        => new(false, 0, 0, Array.Empty<MapType>(), Array.Empty<RegionMap>(), note);

    private static bool IsCanon(nint p) => (ulong)p >= 0x10000 && (ulong)p <= 0x7FFFFFFFFFFF;

    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        return IsCanon(p) ? p : 0;
    }

    /// <summary>One live atlas node. <see cref="X"/>/<see cref="Y"/> are the element's RelativePos
    /// (canvas/screen-space units that the game updates live as you pan; project ×scale + origin to draw).</summary>
    public readonly record struct AtlasNodeLive(
        nint Element, uint Id, uint Content, byte State, byte Biome, byte Flags, byte Completion,
        float X, float Y, float W, float H, float Scale, bool Visible, int IconType,
        int GridX, int GridY, string MapName, string MapCode, IReadOnlyList<string> Tags,
        bool Accessible, bool Completed, string Kind,
        string MapType, string MapGroup, IReadOnlyList<string> MapDataTags)
    {
        /// <summary>The node's atlas grid coordinate (<see cref="Poe2Offsets.AtlasNode.GridPos"/>) — the
        /// key into the connection graph for routing (unique per node; stable while the atlas is open).</summary>
        public (int X, int Y) Grid => (GridX, GridY);
        public bool Unlocked => (Flags & 0x01) != 0;
        public bool Visited => (Flags & 0x02) != 0;
        public bool HasContent => Content != 0;   // +0x310 (atlas-row ptr) non-null ⇒ has rolled content
        // Accessible/Completed are decoded from the deeper node-data status byte (the GameHelper-validated
        // source — *(node+0x10)+0x20 +0x2CF, bit0 accessible / bit1 completed). Accessible ("you can run
        // this now") is the route SOURCE frontier; the element-flag Unlocked/Visited bits are kept separate.
    }

    private readonly object _nodeLock = new();

    private nint _nodeVtable;

    private nint _nodeCanvas;

    private int _nodeRetry;

    private int _hiddenTicks;

    // Per-element resolved content tags (display names). Content is read via the +0x310 EndgameMapAtlas
    // row (validated live 2026-06-07): the headline content @ row+0x38 → contentRow+0x30 name, plus the
    // league mechanics from the stats list @ row+0x50 (stat ids "map_atlas_node_has_<mechanic>"). Cached
    // (content is stable while the atlas is open) and resolved at a bounded rate to avoid a tick hitch.
    private readonly Dictionary<nint, (string code, string map, string[] content, AtlasMapData.MapMeta meta)> _tagCache = new();

    private static readonly string[] NoTags = Array.Empty<string>();

    // Atlas CONNECTION GRAPH (grid coord → neighbour grid coords), read from the canvas's edge vector
    // (Poe2.AtlasGraph.ConnectionsVec). Static while the atlas is open, so it's read once per canvas and
    // cached (rebuilt on Invalidate / canvas change). This is what enables node-to-node routing.
    private readonly Dictionary<(int, int), List<(int, int)>> _graph = new();

    private nint _graphCanvas;

    // The current-location ("player icon") marker: the lone non-node element whose +0x300 → a node. Located
    // during DetectNodeClass (vtable-independent — see Poe2.AtlasGraph.CurrentMarkerNodePtr). *(marker+0x300)
    // is the node the player is currently in (the route's true start).
    private nint _currentMarker;

    /// <summary>Cheap "is the Atlas screen open?" check (the persistent panel's visible bit, ~4 reads) —
    /// the same gate <see cref="ReadNodes"/> uses internally, exposed so callers can tell a TRANSIENT empty
    /// read (atlas open, a node read just hiccupped) from the atlas genuinely being closed. Lets the overlay
    /// hold its last marks through a read miss instead of blanking (the off-screen-arrow flicker).</summary>
    public bool IsAtlasOpen(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        return uiRoot != 0 && AtlasPanelOpen(uiRoot);
    }

    /// <summary>True once every visible node's tags have been resolved + cached (tag resolution is
    /// budget-limited per read, so it takes a few reads after opening the atlas). Lets callers seed
    /// defaults only when the full map/content set is available.</summary>
    public bool AllTagsResolved { get; private set; }

    private void Invalidate() { _nodeCanvas = 0; _nodeVtable = 0; _hiddenTicks = 0; _tagCache.Clear(); _graph.Clear(); _graphCanvas = 0; _currentMarker = 0; }

    private void AddEdge((int, int) a, (int, int) b)
    {
        if (!_graph.TryGetValue(a, out var list)) { list = new List<(int, int)>(4); _graph[a] = list; }
        if (!list.Contains(b)) list.Add(b);
    }

    /// <summary>A* over the atlas connection graph from <paramref name="start"/> to <paramref name="goal"/>
    /// (both grid coords). Returns the ordered grid-coord path (start … goal inclusive), or null when either
    /// endpoint is absent or the two aren't connected. Cost + heuristic are Euclidean grid distance, so the
    /// result is the fewest-hops / shortest route through the unlocked node mesh. Thread-safe (snapshots the
    /// graph under <see cref="_nodeLock"/>); safe to call from the tick thread alongside ReadNodes.</summary>
    /// <summary>Number of nodes in the cached connection graph (0 ⇒ not built / atlas closed). Diagnostic.</summary>
    public int GraphNodeCount { get { lock (_nodeLock) return _graph.Count; } }

    /// <summary>True if the given grid coord is a vertex in the connection graph (has ≥1 edge). Diagnostic —
    /// a node with no edges can't be a route endpoint.</summary>
    public bool GraphHas((int, int) grid) { lock (_nodeLock) return _graph.ContainsKey(grid); }

    // Per-node content BADGES: node[0][0] children, each child's +0x300 → "[Code|Display]" UTF-16 string.
    // The GameHelper Atlas-main reference reads this at child+0x290; on OUR build it's child+0x300 (validated
    // live 2026-06-20 via the F10 discovery dump: n00[0]+0x300='[DeadlyMapBoss|Deadly Map Boss]'). This badge
    // list carries the boss TIER ("Deadly Map Boss") that the +0x310 headline row collapses to the generic
    // "Powerful Map Boss" — so it's what makes Deadly/Twinned/etc. trackable + navigable.
    private const int BadgeContentStr = 0x300;

    /// <summary>Public, lock-guarded variant of <see cref="BadgeContentsNoLock"/> for external callers (F10).</summary>
    public List<string> ReadContentBadges(nint el)
    {
        if (el == 0) return new List<string>();
        lock (_nodeLock) return BadgeContentsNoLock(el);
    }

    private static bool LooksLikeName(string s) => s.Length is >= 3 and <= 64 && s[0] is >= ' ' and < (char)0x7f;

    /// <summary>Title-case each space-separated word ("breach" → "Breach", "boss unique" → "Boss Unique").</summary>
    private static string TitleCase(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }

    /// <summary>Cheap "is the Atlas screen open?" gate used to avoid the whole-tree node-class BFS while
    /// the atlas is closed. The atlas panel is a persistent UiRoot child at a fixed index
    /// (<see cref="Poe2.AtlasPanel.UiRootChildIndex"/>) whose visible bit toggles with the panel — so this
    /// is ~4 reads. Validates the indexed element is a real UiElement (Self==self) first; returns false on
    /// any read failure (fail-safe: a drifted index degrades to feature-off, not a per-tick BFS).</summary>
    private bool AtlasPanelOpen(nint uiRoot)
    {
        if (uiRoot == 0) return false;
        var first = Ptr(uiRoot + Poe2.UiElement.Children);
        if (first == 0) return false;
        var panel = Ptr(first + (nint)(Poe2.AtlasPanel.UiRootChildIndex * 8));
        if (panel == 0 || Ptr(panel + Poe2.UiElement.Self) != panel) return false;   // not a UiElement
        if (!_reader.TryReadStruct<uint>(panel + Poe2.UiElement.Flags, out var fl)) return false;
        return ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
    }

    /// <summary>True iff the element and all ancestors (via Parent +0xB8) have the local visible bit set
    /// — i.e. actually shown. Cheap (~6 reads); used to detect "the Atlas screen is open".</summary>
    private bool HierarchicallyVisible(nint el)
    {
        var cur = el; var guard = 0;
        while (cur != 0 && guard++ < 16)
        {
            if (!_reader.TryReadStruct<uint>(cur + Poe2.UiElement.Flags, out var fl)) return false;
            if (((fl >> Poe2.UiElement.FlagVisibleBit) & 1) == 0) return false;
            var par = Ptr(cur + Poe2.UiElement.Parent);
            if (par == cur) break;
            cur = par;
        }
        return true;
    }
}
