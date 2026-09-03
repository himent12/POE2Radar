namespace POE2Radar.Core.Game;

public sealed partial class Poe2Atlas
{
    /// <summary>Read the live atlas node list. Atlas nodes are all children of one canvas container; we
    /// detect the node element-class + canvas once (BFS, vtable-grouped) and cache them, then each call
    /// just reads the canvas's children (cheap). Re-detects (throttled) if the cache goes stale or the
    /// atlas hasn't been opened yet. Returns empty when not in/near the Atlas.</summary>
    public List<AtlasNodeLive> ReadNodes(nint inGameState)
    {
        var nodes = new List<AtlasNodeLive>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return nodes;

        lock (_nodeLock) // called from both the tick thread (BuildAtlasMarks) and the API thread (AtlasJson)
        {
            // Fast path: cached canvas. Cheap gate first — when the Atlas is CLOSED the canvas isn't
            // hierarchically visible, so we skip reading ~1100 nodes (this runs on the tick loop).
            if (_nodeCanvas != 0 && _nodeVtable != 0)
            {
                if (HierarchicallyVisible(_nodeCanvas))
                {
                    _hiddenTicks = 0;
                    if (ReadCanvasNodes(_nodeCanvas, nodes)) return nodes;
                    // read failed → ReadCanvasNodes already Invalidated; fall through to re-detect.
                }
                else
                {
                    // Canvas hidden = atlas closed → normally a cheap return (no node read). BUT the game
                    // RECREATES the atlas panel on close/reopen, so the cached pointer can go stale and
                    // never recover (the old "restart the overlay to fix detection" bug). Guard against
                    // that: drop a cache that's no longer a live element immediately, and force a periodic
                    // re-detect as a self-heal. Otherwise cheap-return.
                    var liveSelf = Ptr(_nodeCanvas + Poe2.UiElement.Self) == _nodeCanvas;
                    if (liveSelf && ++_hiddenTicks % 150 != 0) return nodes;
                    Invalidate();
                }
            }

            // Cheap open-gate (the key cost saver). We only reach here with NO cached canvas — i.e. the
            // atlas has never been opened this session, or the cache self-healed. DetectNodeClass below
            // BFS-walks the entire (~50k-element) UI tree, and while the atlas is CLOSED it can never
            // succeed (the node elements aren't instantiated until first open), so without this gate it
            // would burn that whole-tree BFS on every retry — the entire time you're mapping. Instead,
            // gate on the atlas panel's visible bit (a persistent UiRoot child; ~4 reads). Closed → bail
            // cheaply. Fail-safe: any read failure reads as closed, so a drifted index degrades to
            // feature-off, never back to a per-tick BFS.
            if (!AtlasPanelOpen(uiRoot)) return nodes;

            // (Re)detect — throttled so even with the gate open we don't BFS 50k elements every tick.
            if (_nodeRetry++ % 30 != 0) return nodes;
            if (!DetectNodeClass(uiRoot)) return nodes;
            if (HierarchicallyVisible(_nodeCanvas)) ReadCanvasNodes(_nodeCanvas, nodes);
            return nodes;
        }
    }

    /// <summary>Read the cached canvas's children, keeping those of the node class. Returns false (and
    /// invalidates the cache) if the canvas no longer looks right, forcing a re-detect.</summary>
    private bool ReadCanvasNodes(nint canvas, List<AtlasNodeLive> outNodes)
    {
        var first = Ptr(canvas + Poe2.UiElement.Children);
        if (first == 0 || !_reader.TryReadStruct<nint>(canvas + Poe2.UiElement.ChildrenEnd, out var last)) { Invalidate(); return false; }
        var count = ((long)last - (long)first) / 8;
        if (count is <= 0 or > 20000) { Invalidate(); return false; }

        var matched = 0;
        var resolveBudget = 80;  // cap new content resolves per call → spread the first-read cost
        var allCached = true;    // false if any node was left unresolved this pass (budget spent)
        for (long i = 0; i < count; i++)
        {
            var el = Ptr(first + (nint)(i * 8));
            if (el == 0 || Ptr(el) != _nodeVtable) continue;     // vtable == node class
            matched++;
            _reader.TryReadStruct<uint>(el + Poe2.AtlasNode.MapNodeId, out var id);
            _reader.TryReadStruct<uint>(el + Poe2.AtlasNode.Content, out var content);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.State, out var state);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Biome, out var biome);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Flags, out var flags);
            _reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Completion, out var compl);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var x);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var y);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.SizeH, out var h);
            _reader.TryReadStruct<float>(el + 0x130, out var scale);
            _reader.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos, out var gridX);     // StdTuple2D<int> atlas grid coord
            _reader.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos + 4, out var gridY); // → the routing graph key
            _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var uiFlags);
            var visible = ((uiFlags >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
            // The node's content/icon TYPE lives on a nested sigil-icon child (content int 1..~50);
            // walk first-children a few levels to find it. Lets us classify + match nodes to in-game icons.
            var iconType = 0; var d = el;
            for (var lvl = 0; lvl < 5 && d != 0; lvl++)
            {
                if (_reader.TryReadStruct<uint>(d + Poe2.AtlasNode.Content, out var c) && c is > 0 and < 256) { iconType = (int)c; break; }
                d = Ptr(Ptr(d + Poe2.UiElement.Children)); // first child = *(*(el+Children))
            }
            // Accessible/completed status: the GameHelper-validated deeper model
            // *(node+DataStorage)+DataModel → status byte +0x2CF (bit0 accessible, bit1 completed). This is
            // the route SOURCE frontier ("maps you can run right now"). Cheap (2 derefs + 1 byte).
            bool accessible = false, completed = false;
            var storage = Ptr(el + Poe2.AtlasNode.DataStorage);
            if (storage != 0)
            {
                var model = Ptr(storage + Poe2.AtlasNode.DataModel);
                if (model != 0 && _reader.TryReadStruct<byte>(model + Poe2.AtlasNode.DataStatus, out var stb))
                { accessible = (stb & 1) != 0; completed = (stb & 2) != 0; }
            }
            // Resolved (cached): map name (all nodes) + rolled content tags (nodes with a +0x310 row).
            // Budget-limited per call so the first read doesn't hitch the tick; fills in over a few calls.
            if (!_tagCache.TryGetValue(el, out var resolved))
            {
                if (resolveBudget > 0) { resolved = ResolveTags(el); _tagCache[el] = resolved; resolveBudget--; }
                else { resolved = ("", "", NoTags, default); allCached = false; } // budget spent — retried next call (not cached)
            }
            var kind = Classify(resolved.code);   // map-archetype class (Citadel/Boss/Tower/Unique/Merchant/Normal) — first-class track target
            var meta = resolved.meta;             // offline maps.json classification (type/group/tags); default when unmapped
            outNodes.Add(new AtlasNodeLive(el, id, content, state, biome, flags, compl, x, y, w, h, scale, visible, iconType, gridX, gridY, resolved.map, resolved.code, resolved.content, accessible, completed, kind,
                meta.Type ?? "", meta.Group ?? "", meta.Tags ?? NoTags));
        }
        if (matched < 8) { Invalidate(); return false; }          // canvas no longer the node container
        AllTagsResolved = allCached;   // true once every node's tags are cached (seed defaults only then)
        EnsureGraph();                 // (re)read the connection-edge vector once per canvas (cached)
        return true;
    }

    /// <summary>The player's CURRENT atlas node grid coord (the "player icon" tile), via the marker element
    /// (<see cref="Poe2Offsets.AtlasGraph.CurrentMarkerNodePtr"/>): <c>*(marker+0x300)</c> → current node →
    /// its <see cref="Poe2Offsets.AtlasNode.GridPos"/>. Returns null when the marker isn't located or has
    /// gone stale (caller keeps its last-known start). Thread-safe; read each tick — it tracks the player as
    /// they run maps. The marker is found during <see cref="DetectNodeClass"/>.</summary>
    public (int X, int Y)? CurrentNodeGrid()
    {
        lock (_nodeLock)
        {
            var m = _currentMarker;
            if (m == 0 || Ptr(m + Poe2.UiElement.Self) != m) return null;   // not located / stale
            var node = Ptr(m + Poe2.AtlasGraph.CurrentMarkerNodePtr);
            if (node == 0) return null;
            if (!_reader.TryReadStruct<int>(node + Poe2.AtlasNode.GridPos, out var gx)) return null;
            if (!_reader.TryReadStruct<int>(node + Poe2.AtlasNode.GridPos + 4, out var gy)) return null;
            return (gx, gy);
        }
    }

    /// <summary>Read the canvas's connection-edge <see cref="StdVector"/> (<see cref="Poe2Offsets.AtlasGraph"/>)
    /// once per canvas and build the bidirectional adjacency by grid coord. Each 20-byte edge is
    /// <c>{ int unknown; StdTuple2D&lt;int&gt; source@+0x04; StdTuple2D&lt;int&gt; target@+0x0C }</c>. Bulk-reads
    /// the whole vector in one pass (cheap, ~300 edges). No-op when already built for this canvas. Caller
    /// holds <see cref="_nodeLock"/>.</summary>
    private void EnsureGraph()
    {
        if (_nodeCanvas == 0 || _graphCanvas == _nodeCanvas) return;
        _graph.Clear(); _graphCanvas = _nodeCanvas;
        var begin = Ptr(_nodeCanvas + Poe2.AtlasGraph.ConnectionsVec);
        if (begin == 0 || !_reader.TryReadStruct<nint>(_nodeCanvas + Poe2.AtlasGraph.ConnectionsVec + 8, out var end)) return;
        var bytes = (long)end - (long)begin;
        if (bytes <= 0 || bytes % Poe2.AtlasGraph.EdgeStride != 0) return;
        var count = (int)(bytes / Poe2.AtlasGraph.EdgeStride);
        if (count is <= 0 or > 200000) return;
        var buf = new byte[count * Poe2.AtlasGraph.EdgeStride];
        if (_reader.TryReadBytes(begin, buf) < buf.Length) return;
        for (var i = 0; i < count; i++)
        {
            var o = i * Poe2.AtlasGraph.EdgeStride;
            var sx = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeSourceOff);
            var sy = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeSourceOff + 4);
            var dx = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeTargetOff);
            var dy = BitConverter.ToInt32(buf, o + Poe2.AtlasGraph.EdgeTargetOff + 4);
            if (sx == dx && sy == dy) continue;
            AddEdge((sx, sy), (dx, dy));
            AddEdge((dx, dy), (sx, sy));
        }
    }

    public List<(int X, int Y)>? FindPath((int X, int Y) start, (int X, int Y) goal)
    {
        Dictionary<(int, int), List<(int, int)>> g;
        lock (_nodeLock)
        {
            if (!_graph.ContainsKey(start) || !_graph.ContainsKey(goal)) return null;
            // Snapshot so the search doesn't race a concurrent EnsureGraph rebuild.
            g = new Dictionary<(int, int), List<(int, int)>>(_graph);
        }
        if (start == goal) return new List<(int X, int Y)> { start };

        static float Dist((int X, int Y) a, (int X, int Y) b)
        { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var gScore = new Dictionary<(int, int), float> { [start] = 0f };
        var open = new PriorityQueue<(int, int), float>();   // lazy PQ: stale entries are filtered via gScore
        open.Enqueue(start, Dist(start, goal));

        while (open.Count > 0)
        {
            var cur = open.Dequeue();
            if (cur == goal)
            {
                var path = new List<(int X, int Y)> { cur };
                while (cameFrom.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur); }
                path.Reverse();
                return path;
            }
            if (!g.TryGetValue(cur, out var neighbours)) continue;
            var baseG = gScore[cur];
            foreach (var nb in neighbours)
            {
                var tentative = baseG + Dist(cur, nb);
                if (gScore.TryGetValue(nb, out var old) && tentative >= old) continue;
                cameFrom[nb] = cur;
                gScore[nb] = tentative;
                open.Enqueue(nb, tentative + Dist(nb, goal));
            }
        }
        return null;
    }

    /// <summary>Multi-source shortest-hop routing over the connection graph: one BFS seeded from every
    /// <paramref name="sources"/> node, then the fewest-hops path from the nearest source reconstructed for
    /// each goal. Returns goal→path (source…goal inclusive) for the REACHABLE goals only. This is the
    /// "route from where I am (or the accessible frontier) to each tracked tile" primitive — far cheaper
    /// than one A* per goal, and it naturally picks the closest entry point. Thread-safe (snapshots the
    /// graph under <see cref="_nodeLock"/>); safe to call from the world thread alongside ReadNodes.</summary>
    public Dictionary<(int X, int Y), List<(int X, int Y)>> RoutesFromSources(
        IReadOnlyCollection<(int, int)> sources, IReadOnlyCollection<(int, int)> goals)
    {
        var result = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        if (sources.Count == 0 || goals.Count == 0) return result;

        Dictionary<(int, int), List<(int, int)>> g;
        lock (_nodeLock)
        {
            if (_graph.Count == 0) return result;
            g = new Dictionary<(int, int), List<(int, int)>>(_graph);   // snapshot — don't race EnsureGraph
        }

        var srcSet = new HashSet<(int, int)>();
        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var visited = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();
        foreach (var s in sources)
            if (g.ContainsKey(s) && srcSet.Add(s) && visited.Add(s)) queue.Enqueue(s);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!g.TryGetValue(cur, out var nbrs)) continue;
            foreach (var nb in nbrs)
            {
                if (!visited.Add(nb)) continue;
                cameFrom[nb] = cur;
                queue.Enqueue(nb);
            }
        }

        foreach (var goal in goals)
        {
            if (!g.ContainsKey(goal)) continue;
            if (srcSet.Contains(goal)) { result[goal] = new List<(int X, int Y)> { goal }; continue; }
            if (!cameFrom.ContainsKey(goal)) continue;
            var path = new List<(int X, int Y)> { goal };
            var cur = goal;
            while (cameFrom.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur); }
            path.Reverse();
            result[goal] = path;
        }
        return result;
    }
}
