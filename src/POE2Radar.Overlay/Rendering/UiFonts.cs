using SkiaSharp;

namespace POE2Radar.Overlay.Draw;

/// <summary>
/// The overlay's embedded typefaces (SIL OFL 1.1 — licences ship next to the exe under Assets/Fonts): Manrope for
/// UI text, Cinzel (a Trajan-style display serif close to PoE's own headings) for titles and big numbers, and
/// Noto Sans Runic for the Elder Futhark ornaments. Embedding them makes the menus look identical on every machine
/// instead of depending on which system fonts happen to be installed. Loaded once; null (→ system fallback) only
/// if a resource is missing.
/// </summary>
internal static class UiFonts
{
    private static readonly Lazy<SKTypeface?> SansRegular = new(() => Load("Manrope-Regular.ttf"));
    private static readonly Lazy<SKTypeface?> SansMedium = new(() => Load("Manrope-Medium.ttf"));
    private static readonly Lazy<SKTypeface?> SansSemiBold = new(() => Load("Manrope-SemiBold.ttf"));
    private static readonly Lazy<SKTypeface?> SansBold = new(() => Load("Manrope-Bold.ttf"));
    private static readonly Lazy<SKTypeface?> CinzelRegular = new(() => Load("Cinzel-Regular.ttf"));
    private static readonly Lazy<SKTypeface?> CinzelSemiBold = new(() => Load("Cinzel-SemiBold.ttf"));
    private static readonly Lazy<SKTypeface?> CinzelBold = new(() => Load("Cinzel-Bold.ttf"));
    private static readonly Lazy<SKTypeface?> CinzelBlack = new(() => Load("Cinzel-Black.ttf"));
    private static readonly Lazy<SKTypeface?> RunicRegular = new(() => Load("NotoSansRunic-Regular.ttf"));

    public enum Weight { Regular, Medium, SemiBold, Bold, Black }

    public static SKTypeface? Sans(Weight w) => w switch
    {
        Weight.Medium => SansMedium.Value,
        Weight.SemiBold => SansSemiBold.Value,
        Weight.Bold or Weight.Black => SansBold.Value,
        _ => SansRegular.Value,
    };

    public static SKTypeface? Display(Weight w) => w switch
    {
        Weight.Regular => CinzelRegular.Value,
        Weight.Bold => CinzelBold.Value,
        Weight.Black => CinzelBlack.Value,
        _ => CinzelSemiBold.Value,
    };

    public static SKTypeface? Runic => RunicRegular.Value;

    private static SKTypeface? Load(string file)
    {
        var asm = typeof(UiFonts).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file, StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        return stream is null ? null : SKTypeface.FromStream(stream);
    }
}
