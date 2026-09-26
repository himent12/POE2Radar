using POE2Radar.Overlay.Draw;
using SkiaSharp;

namespace POE2Radar.Overlay;

/// <summary>
/// Line icons for the menus and panels, authored on a 24×24 grid (stroked, round caps — Lucide-style shapes
/// drawn from scratch). Parsed once with Skia's SVG path parser; draw with <see cref="DrawTarget.DrawIconPath"/>.
/// </summary>
internal static class UiIcons
{
    private static readonly Dictionary<string, string> Data = new(StringComparer.Ordinal)
    {
        ["overview"] = "M4 4h6v7H4z M14 4h6v4h-6z M14 12h6v8h-6z M4 15h6v5H4z",
        ["flask"] = "M9 3h6 M10 3v6.5L4.8 18.4A1.8 1.8 0 0 0 6.4 21h11.2a1.8 1.8 0 0 0 1.6-2.6L14 9.5V3 M7.5 15h9",
        ["macros"] = "M4 12a8 8 0 0 1 13.7-5.6L20 8.6 M20 4v4.6h-4.6 M20 12a8 8 0 0 1-13.7 5.6L4 15.4 M4 20v-4.6h4.6",
        ["trade"] = "M4 8h14 M15 4.5 18.5 8 15 11.5 M20 16H6 M9 12.5 5.5 16 9 19.5",
        ["radar"] = "M12 3a9 9 0 1 0 9 9 M12 7.5a4.5 4.5 0 1 0 4.5 4.5 M12 12l6.4-6.4 M12 12h.01",
        ["search"] = "M10.5 4a6.5 6.5 0 1 1 0 13a6.5 6.5 0 0 1 0-13z M15.3 15.3 20 20",
        ["close"] = "M6.5 6.5l11 11 M17.5 6.5l-11 11",
        ["bolt"] = "M13 3 5 13.5h6L10.5 21 19 10.5h-6z",
        ["chat"] = "M5 5h14a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H10l-4.5 3.5V16H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z",
        ["coin"] = "M12 4a8 8 0 1 1 0 16a8 8 0 0 1 0-16z M12 8v8 M9.5 10.2c0-1.1 1.1-1.7 2.5-1.7s2.5.6 2.5 1.7-1.1 1.5-2.5 1.8-2.5.7-2.5 1.8 1.1 1.7 2.5 1.7 2.5-.6 2.5-1.7",
        ["heart"] = "M12 19.5s-7.5-4.4-7.5-10A4.3 4.3 0 0 1 12 7a4.3 4.3 0 0 1 7.5 2.5c0 5.6-7.5 10-7.5 10z",
        ["pulse"] = "M3 12h4l2.5-6 5 12 2.5-6H21",
        ["external"] = "M14 4h6v6 M20 4l-9 9 M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
        ["refresh"] = "M20 11a8 8 0 1 0-2.3 5.7 M20 5v6h-6",
        ["plus"] = "M12 5v14 M5 12h14",
        ["eye"] = "M2.5 12s3.5-6.5 9.5-6.5S21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z M12 9.5a2.5 2.5 0 1 1 0 5a2.5 2.5 0 0 1 0-5z",
        ["map"] = "M9 4 3.5 6v14L9 18l6 2 5.5-2V4L15 6z M9 4v14 M15 6v14",
        ["gauge"] = "M4.5 17a8.5 8.5 0 1 1 15 0 M12 13l4-4",
        ["keyboard"] = "M4 7h16a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1z M7 10.5h.01 M11 10.5h.01 M15 10.5h.01 M8 14h8",
    };

    private static readonly Dictionary<string, DrawPath?> Cache = new(StringComparer.Ordinal);

    public static DrawPath? Get(string name)
    {
        // Static and shared by every renderer: headless renderers (tests, previews) may call this side by side.
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var p)) return p;
            p = Data.TryGetValue(name, out var d) && SKPath.ParseSvgPathData(d) is { } path ? new DrawPath(path) : null;
            Cache[name] = p;
            return p;
        }
    }
}
