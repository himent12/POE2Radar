using System.Reflection;
using POE2Radar.Overlay.Draw;
using SkiaSharp;

namespace POE2Radar.Overlay;

/// <summary>
/// Decodes the embedded Atlas content-icon PNGs once into premultiplied-BGRA bitmaps, cached for
/// the renderer's lifetime.
/// </summary>
public sealed class AtlasIconCache : IDisposable
{
    private readonly Dictionary<string, DrawBitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public AtlasIconCache() => LoadEmbedded();

    public int Count => _bitmaps.Count;

    private void LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            const string marker = ".AtlasIcons.";
            foreach (var res in asm.GetManifestResourceNames())
            {
                var idx = res.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0 || !res.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var basename = res[(idx + marker.Length)..^4];
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                using var codec = SKCodec.Create(s);
                if (codec is null) continue;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                if (codec.GetPixels(info, bmp.GetPixels()) != SKCodecResult.Success)
                {
                    bmp.Dispose();
                    continue;
                }
                _bitmaps[basename] = new DrawBitmap(bmp);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AtlasIconCache load failed: {ex.Message}");
        }
    }

    public DrawBitmap? Get(DrawTarget _, string? basename)
    {
        if (string.IsNullOrEmpty(basename)) return null;
        return _bitmaps.TryGetValue(basename, out var bmp) ? bmp : null;
    }

    public void Dispose()
    {
        foreach (var b in _bitmaps.Values) b?.Dispose();
        _bitmaps.Clear();
    }
}
