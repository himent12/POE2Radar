using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Wall-distance weighting for the binary walkable grid. Every walkable cell gets a value that A*'s cost
/// model (<c>(6 − value) × step</c>) turns into a penalty for hugging walls:
/// <list type="bullet">
/// <item><see cref="Open"/> (5) → ×1: two or more cells from any wall.</item>
/// <item><see cref="Near"/> (4) → ×2: one cell from a wall.</item>
/// <item><see cref="Hug"/>  (3) → ×3: touching a wall (8-neighbour).</item>
/// </list>
/// Routes therefore run down the middle of corridors and swing wide around corners instead of scraping
/// them — the character has a collision radius wider than one cell, and a path that clips a corner is a
/// path the character gets stuck on. Built once per terrain (cached by reference in <see cref="PathPlanner"/>).
/// </summary>
public sealed class ClearanceGrid : ICellReader
{
    public const byte Open = 5, Near = 4, Hug = 3;

    private readonly byte[] _cells;
    public int Width { get; }
    public int Height { get; }
    public byte[] Cells => _cells;

    private ClearanceGrid(byte[] cells, int w, int h) { _cells = cells; Width = w; Height = h; }

    public int Read(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        return _cells[y * Width + x];
    }

    public static ClearanceGrid Build(Poe2Live.TerrainData t) => Build(t.Walkable, t.Width, t.Height);

    public static ClearanceGrid Build(byte[] walkable, int w, int h)
    {
        var n = w * h;
        var cells = new byte[n];
        // Pass 1: walkable cells touching a wall (or the map edge) → Hug; others provisionally Open.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (walkable[i] == 0) continue;
                cells[i] = Touches(walkable, w, h, x, y) ? Hug : Open;
            }
        }
        // Pass 2: Open cells touching a Hug cell → Near.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (cells[i] != Open) continue;
                for (var dy = -1; dy <= 1 && cells[i] == Open; dy++)
                {
                    var yy = y + dy;
                    if ((uint)yy >= (uint)h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if ((uint)xx >= (uint)w) continue;
                        if (cells[yy * w + xx] == Hug) { cells[i] = Near; break; }
                    }
                }
            }
        }
        return new ClearanceGrid(cells, w, h);
    }

    private static bool Touches(byte[] walkable, int w, int h, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var yy = y + dy;
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var xx = x + dx;
                if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return true; // map edge counts as wall
                if (walkable[yy * w + xx] == 0) return true;
            }
        }
        return false;
    }
}
