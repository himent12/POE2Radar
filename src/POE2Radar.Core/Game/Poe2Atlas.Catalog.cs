namespace POE2Radar.Core.Game;

public sealed partial class Poe2Atlas
{
    /// <summary>Archetype class derived from the code — drives "valuable map" filtering. (Per-node rolled
    /// content like a boss modifier isn't reachable; this is the map TYPE's inherent class.)</summary>
    public static string Classify(string code)
    {
        if (code.Contains("Citadel", StringComparison.Ordinal)) return "Citadel";
        if (code.Contains("UberBoss", StringComparison.Ordinal)) return "Boss";
        if (code.Contains("HildaCampsite", StringComparison.Ordinal) || code.Contains("Wildwood", StringComparison.Ordinal)) return "Unique";
        if (code.Contains("Merchant", StringComparison.Ordinal)) return "Merchant";
        if (code.Contains("Unique", StringComparison.Ordinal)) return "Unique";
        if (code.Contains("Tower", StringComparison.Ordinal)) return "Tower";
        return "Normal";
    }

    /// <summary>An address is a catalog entry iff +0x08 and +0x10 are canonical heap pointers and the
    /// +0x10 target begins "Map" in UTF-16.</summary>
    private bool IsCatalogEntry(nint e)
    {
        var obj = Ptr(e + 0x08);
        var idStr = Ptr(e + 0x10);
        if (obj == 0 || idStr == 0) return false;
        Span<byte> b = stackalloc byte[6];
        return _reader.TryReadBytes(idStr, b) == 6 && b.SequenceEqual(MapPrefix);
    }

    /// <summary>Scan [lo,hi) for a run of ≥8 consecutive catalog entries; on a hit, walk to the run's
    /// bounds and cache base + count. Cheap-prunes candidates on the in-buffer pointer shape before
    /// the (syscall) idStr deref.</summary>
    private bool ScanForCatalog(nint lo, nint hi)
    {
        var chunk = new byte[1 << 20];
        var overlap = Stride * 9;
        foreach (var (regionBase, regionSize) in _reader.Process.EnumerateReadableRegions(privateOnly: false))
        {
            if ((long)regionBase + regionSize <= (long)lo || (long)regionBase >= (long)hi) continue;
            long off = 0;
            while (off < regionSize)
            {
                var toRead = (int)Math.Min(chunk.Length, regionSize - off);
                var read = _reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
                if (read <= 0) break;
                for (var i = 0; i + Stride * 8 <= read; i += 8)
                {
                    // Cheap prune (no syscall — all from the buffer): a catalog entry starts with a small
                    // int32 id, then two canonical heap pointers. Random data rarely matches all three.
                    var id = BitConverter.ToInt32(chunk, i);
                    if (id is < 0 or > 4096) continue;
                    if (!IsCanon((nint)BitConverter.ToInt64(chunk, i + 0x08))) continue;
                    if (!IsCanon((nint)BitConverter.ToInt64(chunk, i + 0x10))) continue;
                    var baseAddr = regionBase + (nint)(off + i);
                    if (!IsCatalogEntry(baseAddr)) continue;
                    // Confirm a run (≥6 more) before committing.
                    var ok = true;
                    for (var k = 1; k <= 6 && ok; k++) ok = IsCatalogEntry(baseAddr + (nint)(k * Stride));
                    if (!ok) continue;
                    // Walk to the true start + count.
                    var start = baseAddr;
                    while (IsCatalogEntry(start - Stride)) start -= Stride;
                    var count = 0;
                    for (var e = start; count < 20000 && IsCatalogEntry(e); e += Stride) count++;
                    _catalogCount = count; CatalogBase = start;
                    return true;
                }
                if (read != toRead) break;
                if (toRead < chunk.Length) break;
                off += chunk.Length - overlap;
            }
        }
        return false;
    }

    private List<MapType> WalkCatalog(nint start, int count)
    {
        var list = new List<MapType>(count);
        for (var i = 0; i < count; i++)
        {
            var e = start + (nint)(i * Stride);
            if (!IsCatalogEntry(e)) break;
            _reader.TryReadStruct<int>(e, out var id);
            var obj = Ptr(e + 0x08);
            var idStr = Ptr(e + 0x10);
            var code = _reader.ReadStringUtf16(idStr, 64);
            list.Add(new MapType(id, code, Prettify(code), Classify(code), (long)obj, (long)idStr));
        }
        return list;
    }

    /// <summary>Read the current-region map array: a 0x18-stride run of {record, archetype∈catalog,
    /// sharedConst}. Returns the longest such run. Bounded to a window around the catalog (the array +
    /// records live near it), so this stays fast enough to run on every read.</summary>
    private List<RegionMap> ReadRegion(Dictionary<nint, MapType> byParsed, nint catalogBase)
    {
        var winLo = (long)catalogBase - 0x1000_0000L; // ±256 MB around the catalog
        var winHi = (long)catalogBase + 0x1000_0000L;
        var best = new List<RegionMap>();
        var chunk = new byte[1 << 20];
        var overlap = Stride * 32;
        foreach (var (regionBase, regionSize) in _reader.Process.EnumerateReadableRegions(privateOnly: false))
        {
            if ((long)regionBase + regionSize <= winLo || (long)regionBase >= winHi) continue;
            long off = 0;
            while (off < regionSize)
            {
                var toRead = (int)Math.Min(chunk.Length, regionSize - off);
                var read = _reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
                if (read <= 0) break;
                for (var i = 0; i + Stride <= read; i += 8)
                {
                    var rec = (nint)BitConverter.ToInt64(chunk, i);
                    var arch = (nint)BitConverter.ToInt64(chunk, i + 0x08);
                    var shared = (nint)BitConverter.ToInt64(chunk, i + 0x10);
                    if (!IsCanon(rec) || !IsCanon(shared) || !byParsed.ContainsKey(arch)) continue;
                    // Found a candidate entry — measure the run from here (entries share `shared`).
                    var run = new List<RegionMap>();
                    for (var e = regionBase + (nint)(off + i); run.Count < 20000; e += Stride)
                    {
                        var r = Ptr(e); var a = Ptr(e + 0x08); var s = Ptr(e + 0x10);
                        if (!IsCanon(r) || s != shared || !byParsed.TryGetValue(a, out var mt)) break;
                        run.Add(new RegionMap(mt.Code, mt.Name, mt.Kind, (long)r));
                    }
                    if (run.Count > best.Count) best = run;
                    // Skip past this run to avoid re-measuring its interior.
                    if (run.Count > 1) i += (run.Count - 1) * Stride;
                }
                if (read != toRead) break;
                if (toRead < chunk.Length) break;
                off += chunk.Length - overlap;
            }
        }
        return best.Count >= 8 ? best : new List<RegionMap>(); // require a real run, not a coincidence
    }

    /// <summary>Derive a readable display name from the internal code: strip the "Map" prefix and common
    /// qualifiers, then space out CamelCase / underscores. "MapRustbowl"→"Rustbowl";
    /// "MapUberBoss_StoneCitadel"→"Stone Citadel"; "MapUniqueMerchant01_Oasis"→"Merchant Oasis". This is
    /// clearly DERIVED (the real localized display name isn't reliably adjacent in memory across builds).</summary>
    public static string Prettify(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var s = code;
        if (s.StartsWith("Map", StringComparison.Ordinal)) s = s[3..];
        s = s.Replace("UberBoss_", "").Replace("PrecursorTower", "Tower ").Replace("Unique", "");
        // Drop a leading "MerchantNN_" style numeric qualifier inside merchant codes.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\D)\d{1,2}(?=_|$)", "");
        s = s.Replace("_", " ");
        // Space out CamelCase boundaries.
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Read + parse a node's content-badge display names (no lock — caller holds <see cref="_nodeLock"/>,
    /// e.g. ResolveTags via ReadCanvasNodes). Returns the display portion of each "[Code|Display]" badge.</summary>
    private List<string> BadgeContentsNoLock(nint el)
    {
        var result = new List<string>();
        var n0 = Ptr(Ptr(el + Poe2.UiElement.Children));           // node[0]
        if (n0 == 0) return result;
        var n00 = Ptr(Ptr(n0 + Poe2.UiElement.Children));          // node[0][0] = content-badge container
        if (n00 == 0) return result;
        var begin = Ptr(n00 + Poe2.UiElement.Children);
        if (begin == 0 || !_reader.TryReadStruct<nint>(n00 + Poe2.UiElement.ChildrenEnd, out var end)) return result;
        var count = ((long)end - (long)begin) / 8;
        if (count is <= 0 or > 64) return result;
        for (long i = 0; i < count; i++)
        {
            var child = Ptr(begin + (nint)(i * 8));
            if (child == 0) continue;
            var sp = Ptr(child + BadgeContentStr);                  // badge child → content-name ptr
            if (sp == 0) continue;
            var name = ParseBadgeName(_reader.ReadStringUtf16(sp, 96));
            if (name.Length > 0 && !result.Contains(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>"[DeadlyMapBoss|Deadly Map Boss]" → "Deadly Map Boss" (display part after '|'); a bare
    /// "[Code]" → "Code". Returns "" for anything that doesn't look like a real name.</summary>
    private static string ParseBadgeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        int lb = s.IndexOf('['), rb = s.LastIndexOf(']');
        if (lb >= 0 && rb > lb) s = s.Substring(lb + 1, rb - lb - 1);
        int pipe = s.IndexOf('|');
        if (pipe >= 0) s = s[(pipe + 1)..];
        s = s.Trim();
        return LooksLikeName(s) ? s : "";
    }

    /// <summary>DIAGNOSTIC: describe a node's child-tree + scan each content-badge child's offset window
    /// (0x280..0x300) for UTF-16 strings, so we can LOCATE where per-node content (incl. the boss-tier
    /// "Deadly Map Boss" badge) lives on our build — the GameHelper Atlas-main path (node[0][0] children,
    /// +0x290) returned nothing here, so this finds the real layout instead of guessing. Printed by F10.</summary>
    public string DescribeNodeContent(nint el)
    {
        if (el == 0) return "(null element)";
        var sb = new System.Text.StringBuilder();
        lock (_nodeLock)
        {
            int Count(nint e)
            {
                var b = Ptr(e + Poe2.UiElement.Children);
                if (b == 0 || !_reader.TryReadStruct<nint>(e + Poe2.UiElement.ChildrenEnd, out var en)) return -1;
                var c = ((long)en - (long)b) / 8;
                return c is >= 0 and <= 100000 ? (int)c : -1;
            }
            nint Child(nint e, int i)
            {
                var b = Ptr(e + Poe2.UiElement.Children);
                return b == 0 ? 0 : Ptr(b + (nint)(i * 8));
            }
            void ScanStrings(nint e, string lbl)
            {
                for (var off = 0x280; off <= 0x310; off += 8)
                {
                    var p = Ptr(e + off);
                    if (p == 0) continue;
                    var s = _reader.ReadStringUtf16(p, 48);
                    if (LooksLikeName(s)) sb.Append($" {lbl}+0x{off:X}='{s}'");
                }
            }

            var n0 = Child(el, 0);
            var n00 = n0 != 0 ? Child(n0, 0) : 0;
            sb.Append($"tree: node.ch={Count(el)} n0.ch={(n0 != 0 ? Count(n0) : -1)} n00.ch={(n00 != 0 ? Count(n00) : -1)}");
            // Scan the node itself + the two child levels we navigate.
            ScanStrings(el, "node");
            if (n0 != 0) ScanStrings(n0, "n0");
            if (n00 != 0)
            {
                var cnt = Count(n00);
                for (var i = 0; i < cnt && i < 8; i++)
                {
                    var ch = Child(n00, i);
                    if (ch == 0) continue;
                    sb.Append($"\n   n00[{i}]:");
                    ScanStrings(ch, "");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Resolve a node's content TAGS (display names) via its EndgameMapAtlas row (+0x310):
    /// the headline content (row+0x38 → content row +0x30 name, e.g. "Powerful Map Boss") plus the league
    /// mechanics harvested from the stats sub-struct (row+0x50): stat ids "map_atlas_node_has_&lt;x&gt;"
    /// → "X" (Breach, Delirium, …). Validated live 2026-06-07; re-confirm offsets via Research --atlas-resolve.</summary>
    private (string code, string map, string[] content, AtlasMapData.MapMeta meta) ResolveTags(nint el)
    {
        // Map NAME: node +0x300 → EndgameMaps row; its +0x00 → the WorldAreas row, which holds
        // {+0x00 → Id "MapXxx",  +0x08 → the LOCALIZED display name}. We now read +0x08 (the game's real
        // name, e.g. "Savannah"/"Digsite"/"Precursor Tower") instead of Prettify()-guessing the code —
        // Prettify mismatched the in-game name for some maps, which broke web-UI filters. Validated live
        // 2026-06-16 (Research --atlas-mapname). Prettify(code) stays only as a fallback. The raw code is
        // returned too (stable, never localized) so the F10 inspector / dashboard can show it.
        string code = "", name = "";
        var mapRow = Ptr(el + 0x300);
        if (mapRow != 0)
        {
            var w = Ptr(mapRow);
            var direct = w != 0 ? _reader.ReadStringUtf16(w, 64) : "";
            if (direct.StartsWith("Map", StringComparison.Ordinal))
            {
                code = direct;                                  // legacy layout: +0x300 row → code string directly
            }
            else if (w != 0)
            {
                var idP = Ptr(w);                               // WorldAreas +0x00 → Id "MapXxx"
                code = idP != 0 ? _reader.ReadStringUtf16(idP, 64) : "";
                var nmP = Ptr(w + Poe2.AtlasMapRow.WorldAreaName); // WorldAreas +0x08 → localized name
                name = nmP != 0 ? _reader.ReadStringUtf16(nmP, 64) : "";
            }
        }
        // Offline classification layer (atlas_maps.json): type/group/tags keyed by the internal MapId. Adds
        // unique/lineage/arbiter signal Classify() can't derive, and a curated display name as a fallback.
        var meta = AtlasMapData.Shared.Get(code) ?? default;

        var map = (LooksLikeName(name) && name.Length <= 48) ? name.Trim()
                : (!string.IsNullOrEmpty(meta.Name) ? meta.Name
                : (code.StartsWith("Map", StringComparison.Ordinal) ? Prettify(code) : ""));

        var tags = new List<string>(4);

        // Rolled content lives on the EndgameMapAtlas row at +0x310 (null ⇒ no headline/mechanic content,
        // but the node may still carry CONTENT BADGES — read below regardless).
        var row = Ptr(el + 0x310);
        if (row != 0)
        {
            // Headline content: row+0x38 → content row; +0x30 is a pointer to the (NUL-terminated UTF-16)
            // display name, e.g. "Powerful Map Boss" / "Trialmaster's Trainee".
            var contentRow = Ptr(row + 0x38);
            if (contentRow != 0)
            {
                var np = Ptr(contentRow + 0x30);
                var nm = np != 0 ? _reader.ReadStringUtf16(np, 64) : "";
                if (LooksLikeName(nm)) tags.Add(nm.Trim());
            }

            // League mechanics: row+0x50 → stats sub-struct; harvest "map_atlas_node_has_<mechanic>" stat ids.
            var stats = Ptr(row + 0x50);
            if (stats != 0)
            {
                Span<byte> sb = stackalloc byte[0x100];
                var n = _reader.TryReadBytes(stats, sb);
                for (var o = 0; o + 8 <= n; o += 8)
                {
                    var p = (nint)System.BitConverter.ToInt64(sb[o..]);
                    if (!IsCanon(p)) continue;
                    const string pre = "map_atlas_node_has_";
                    // The stat id (ASCII) lives at the pointer, or one deref in.
                    var s = ReadAscii(p, 64);
                    if (!s.StartsWith(pre, StringComparison.Ordinal)) { var pp = Ptr(p); s = pp == 0 ? "" : ReadAscii(pp, 64); }
                    if (s.StartsWith(pre, StringComparison.Ordinal))
                    {
                        var mech = TitleCase(s[pre.Length..].Replace('_', ' '));
                        if (mech.Length > 0 && !tags.Contains(mech)) tags.Add(mech);
                    }
                }
            }
        }

        // CONTENT BADGES (node[0][0] children, +0x300 → "[Code|Display]"): the boss-TIER badge "Deadly Map
        // Boss" and any other per-node content the headline collapses. Always read (a node can have a badge
        // with a null +0x310 row), de-duped against the headline/mechanic tags above.
        foreach (var bc in BadgeContentsNoLock(el))
            if (!tags.Contains(bc)) tags.Add(bc);

        return (code, map, tags.Count == 0 ? NoTags : tags.ToArray(), meta);
    }

    /// <summary>Read a NUL/garbage-terminated ASCII run at <paramref name="addr"/> (stat ids are ASCII).</summary>
    private string ReadAscii(nint addr, int max)
    {
        Span<byte> b = stackalloc byte[max];
        var n = _reader.TryReadBytes(addr, b);
        var sb = new System.Text.StringBuilder(n);
        for (var i = 0; i < n; i++) { var c = b[i]; if (c is >= 0x20 and < 0x7f) sb.Append((char)c); else break; }
        return sb.ToString();
    }

    /// <summary>BFS the UI tree; the atlas-node class is the vtable whose instances spread across many
    /// distinct biome values (0..12) — generic elements are all biome 0. Cache that vtable + the nodes'
    /// common parent (the canvas container).</summary>
    private bool DetectNodeClass(nint uiRoot)
    {
        var root = Ptr(uiRoot + Poe2.UiElement.Parent) is var tr && tr != 0 ? tr : uiRoot;
        var queue = new Queue<nint>(); queue.Enqueue(root);
        var visited = new HashSet<nint>();
        var byVtable = new Dictionary<nint, List<nint>>();
        while (queue.Count > 0 && visited.Count < 200000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el) || Ptr(el + Poe2.UiElement.Self) != el) continue;
            var vt = Ptr(el);
            if (vt != 0) (byVtable.TryGetValue(vt, out var l) ? l : byVtable[vt] = new()).Add(el);
            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
            {
                var n = ((long)last - (long)first) / 8;
                if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) queue.Enqueue(Ptr(first + (nint)(k * 8)));
            }
        }
        // Score each candidate vtable on THREE signals so a stray biome-ish UI class can't win (a
        // biome-spread-only pick mis-detected a 18×18 list element → the overlay read 0 nodes): the real
        // atlas-node class is ~40×40, has biome spread ≥3, AND the most instances.
        nint bestVt = 0; var bestCount = 0; var bestBiomes = 0;
        nint fbVt = 0; var fbBiomes = 0;   // fallback: max biome-spread, in case sizes drift
        foreach (var (vt, list) in byVtable)
        {
            if (list.Count < 50) continue;
            var biomes = new HashSet<int>(); var widths = new Dictionary<int, int>();
            foreach (var el in list.Take(400))
            {
                if (_reader.TryReadStruct<byte>(el + Poe2.AtlasNode.Biome, out var b) && b is >= 1 and <= 12) biomes.Add(b);
                if (_reader.TryReadStruct<float>(el + Poe2.UiElement.SizeW, out var w)) { var iw = (int)w; widths[iw] = widths.GetValueOrDefault(iw) + 1; }
            }
            if (biomes.Count > fbBiomes) { fbBiomes = biomes.Count; fbVt = vt; }
            var modalW = widths.Count == 0 ? 0 : widths.OrderByDescending(k => k.Value).First().Key;
            if (modalW is >= 28 and <= 56 && biomes.Count >= 3 && list.Count > bestCount) { bestCount = list.Count; bestVt = vt; bestBiomes = biomes.Count; }
        }
        if (bestVt == 0) { bestVt = fbVt; bestBiomes = fbBiomes; }   // no ~40×40 class — fall back
        if (bestVt == 0 || bestBiomes < 3) return false;
        _nodeVtable = bestVt;
        // The node-class elements also appear OUTSIDE the atlas (terrain props / minimap), so the
        // first one's parent isn't necessarily the node canvas. The real atlas canvas is the parent
        // that holds the MOST node-class children — pick that (443 nodes vs a few terrain props).
        var parentCount = new Dictionary<nint, int>();
        foreach (var el in byVtable[bestVt])
        {
            var p = Ptr(el + Poe2.UiElement.Parent);
            if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1;
        }
        if (parentCount.Count == 0) return false;
        _nodeCanvas = parentCount.OrderByDescending(k => k.Value).First().Key;

        // Current-location marker: the lone NON-node element whose +0x300 points at a node-class element
        // (*(marker+0x300) = the node the player is currently in). Structural, so no vtable to drift. The BFS
        // above already visited every element (grouped in byVtable); scan them once.
        _currentMarker = 0;
        var nodeSet = new HashSet<nint>(byVtable[bestVt]);
        foreach (var el in byVtable.Values.SelectMany(v => v))
        {
            if (nodeSet.Contains(el)) continue;
            var p = Ptr(el + Poe2.AtlasGraph.CurrentMarkerNodePtr);
            if (p != 0 && nodeSet.Contains(p)) { _currentMarker = el; break; }
        }

        return _nodeCanvas != 0;
    }
}
