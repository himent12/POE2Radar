namespace POE2Radar.Core.Game;

public sealed partial class Poe2Live
{
    private List<Landmark>? _landmarks;

    private nint _landmarksKey = -1;

    /// <summary>Resolve a tile's curated label: the injected user overlay if wired, else the baked list.</summary>
    private string? Curated(string areaCode, string tilePath)
        => CuratedLookup is { } f ? f(areaCode, tilePath) : CustomLandmarkData.TryMatch(areaCode, tilePath);

    /// <summary>Drop the cached per-area landmark scan so the next <see cref="Landmarks"/> call rebuilds
    /// it (e.g. after the user edits the custom landmark patterns from the dashboard).</summary>
    public void InvalidateLandmarks() => _landmarksKey = -1;

    private List<string>? _tilePaths;

    private nint _tilePathsKey = -1;

    /// <summary>
    /// All DISTINCT terrain-tile paths in the area (sorted), scanned once per area and cached. This is
    /// the full vocabulary of tile names — what the dashboard's add-rule picker browses so a tile rule
    /// can target any tile, not just the ones already surfaced as landmarks.
    /// </summary>
    public IReadOnlyList<string> TilePaths(nint areaInstance)
    {
        if (areaInstance == _tilePathsKey && _tilePaths is not null) return _tilePaths;
        _tilePathsKey = areaInstance;
        _tilePaths = ScanTilePaths(areaInstance);
        return _tilePaths;
    }

    private List<string> ScanTilePaths(nint areaInstance)
    {
        var result = new List<string>();
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Distinct by TgtFilePtr (one read per tile type — dozens, not per tile), collect the paths.
        var seenPtr = new HashSet<nint>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            var tgt = Ptr(first + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0 || !seenPtr.Add(tgt)) continue;
            var p = ReadStdWString(tgt + Poe2.TgtFileStruct.TgtPath);
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        result.AddRange(paths);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Static tile-based landmarks for the area (boss arenas, treasure, waypoints, mechanics…).
    /// Scans the terrain tile grid once per area (cached): each tile's TgtPath, grouped by path
    /// for "interesting" features, with the grid centroid of each group. This is the pre-explored
    /// "X is over here" layer — terrain-feature granularity, not a per-monster spawn table.
    /// </summary>
    public IReadOnlyList<Landmark> Landmarks(nint areaInstance)
    {
        if (areaInstance == _landmarksKey && _landmarks is not null) return _landmarks;
        _landmarksKey = areaInstance;
        _landmarks = ScanLandmarks(areaInstance);
        return _landmarks;
    }

    private List<Landmark> ScanLandmarks(nint areaInstance)
    {
        var result = new List<Landmark>();
        var areaCode = AreaCode(areaInstance);
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Collect each kept path's tile cells (in tile-index space) so we can CLUSTER them spatially
        // rather than average every instance into one centroid. A reusable tile (e.g. a "stairs up"
        // wall piece) recurs in several disjoint spots — multi-level dungeons have multiple stair-up /
        // stair-down sections connecting layers — and averaging them lands a marker in the dead space
        // between, pointing at nothing. Clustering yields one landmark per actual spot. Cache path by
        // TgtFilePtr so we read each distinct tile type's StdWString once (dozens), not per tile.
        var pathCache = new Dictionary<nint, string?>();
        var cellsByPath = new Dictionary<string, List<(int tx, int ty)>>();

        for (long i = 0; i < count; i++)
        {
            var tile = first + (nint)(i * Poe2.TileStructureSize);
            var tgtFile = Ptr(tile + Poe2.TileStructure.TgtFilePtr);
            if (tgtFile == 0) continue;
            if (!pathCache.TryGetValue(tgtFile, out var path))
            {
                var p = ReadStdWString(tgtFile + Poe2.TgtFileStruct.TgtPath);
                // Surface a tile as a landmark ONLY if the curated community list names it for this area
                // OR a user "Tile" display rule matches it (CustomLandmarkMatch). The old generic keyword
                // sweep was removed — it surfaced decorative terrain (e.g. every "...Vault_Door..." tile)
                // as noise; users now opt into any tile via Tile rules + the dashboard picker.
                var keep = Curated(areaCode, p) != null
                           || CustomLandmarkMatch?.Invoke(p) != null;
                path = keep ? p : null;
                pathCache[tgtFile] = path;
            }
            if (path is null) continue;
            (cellsByPath.TryGetValue(path, out var cells) ? cells : cellsByPath[path] = new())
                .Add(((int)(i % tilesX), (int)(i / tilesX)));
        }

        var cell = Poe2.Terrain.TileGridCells;
        foreach (var (path, cells) in cellsByPath)
        {
            var name = LandmarkName(path);
            // Curated label wins; else a non-empty user label; else null (derived name shows). Same
            // for every cluster of this path (they're the same feature type in different spots).
            var curated = Curated(areaCode, path) ?? NonEmpty(CustomLandmarkMatch?.Invoke(path));
            foreach (var cluster in ClusterTiles(cells, Math.Clamp(LandmarkClusterGap, 0, 64)))
            {
                double sx = 0, sy = 0;
                foreach (var (tx, ty) in cluster) { sx += tx; sy += ty; }
                var center = new System.Numerics.Vector2(
                    (float)(sx / cluster.Count * cell), (float)(sy / cluster.Count * cell));
                result.Add(new Landmark(name, path, center, cluster.Count, curated));
            }
        }
        return result;
    }

    /// <summary>
    /// Group same-path tile cells into spatially-disjoint clusters: two cells join when within a
    /// Chebyshev gap of <c>≤ gap</c> tiles (gap=2 bridges a one-tile hole inside a feature while
    /// keeping well-separated copies apart; larger merges more, 0 = only directly-touching cells).
    /// Plain BFS over a cell set — O(tiles) for the small kept-path counts, so a tile type that recurs
    /// across the map yields one cluster per location instead of a single meaningless average.
    /// </summary>
    private static List<List<(int tx, int ty)>> ClusterTiles(List<(int tx, int ty)> cells, int gap)
    {
        var set = new HashSet<(int, int)>(cells);
        var visited = new HashSet<(int, int)>();
        var clusters = new List<List<(int tx, int ty)>>();
        var queue = new Queue<(int, int)>();
        foreach (var start in cells)
        {
            if (!visited.Add(start)) continue;
            var cluster = new List<(int tx, int ty)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                cluster.Add((cx, cy));
                for (var dx = -gap; dx <= gap; dx++)
                    for (var dy = -gap; dy <= gap; dy++)
                    {
                        var nb = (cx + dx, cy + dy);
                        if (set.Contains(nb) && visited.Add(nb)) queue.Enqueue(nb);
                    }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    /// <summary>Null for null/empty, else the string — so an empty user label means "surface but use the
    /// path-derived name" rather than showing a blank curated label.</summary>
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string LandmarkName(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Read the packed walkable grid (one nibble per cell, 2 cells/byte) into a flat 0/1 array.</summary>
    public TerrainData? Terrain(nint areaInstance)
    {
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        var first = Ptr(terrain + Poe2.Terrain.GridWalkableData);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.GridWalkableData + 8, out var last) || last == 0) return null;
        if (!_reader.TryReadStruct<int>(terrain + Poe2.Terrain.BytesPerRow, out var bytesPerRow) || bytesPerRow <= 0 || bytesPerRow > 65536) return null;
        var totalBytes = (long)last - (long)first;
        if (first == 0 || totalBytes <= 0 || totalBytes > 64 * 1024 * 1024) return null;

        var rows = (int)(totalBytes / bytesPerRow);
        var width = bytesPerRow * 2;
        if (rows <= 0 || rows > 65536) return null;

        var raw = new byte[totalBytes];
        if (_reader.TryReadBytes(first, raw) != raw.Length) return null;

        var walk = new byte[width * rows];
        for (var y = 0; y < rows; y++)
        {
            var rowBase = (long)y * bytesPerRow;
            for (var x = 0; x < width; x++)
            {
                var b = raw[rowBase + (x >> 1)];
                var nibble = (x & 1) == 0 ? (b & 0x0F) : (b >> 4);
                walk[y * width + x] = (byte)(nibble != 0 ? 1 : 0);
            }
        }
        return new TerrainData(walk, width, rows);
    }
}
