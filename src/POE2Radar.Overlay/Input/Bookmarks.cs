using System.Diagnostics;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Input;

/// <summary>A website bound to an optional hotkey. <see cref="Url"/> may use <c>{league}</c> <c>{item}</c> <c>{char}</c>.</summary>
public sealed class Bookmark
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hotkey { get; set; } = "";
}

/// <summary>Values for bookmark URL placeholders; null = unknown (a URL that needs it is refused).</summary>
public sealed record UrlContext(string? League = null, string? Item = null, string? Character = null);

public static partial class Bookmarks
{
    [GeneratedRegex(@"\{(league|item|char)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    /// <summary>
    /// Substitute URL-encoded placeholder values, then validate the result as an absolute http(s) URL.
    /// Encoding happens BEFORE validation so a value can never smuggle in a scheme, host or query.
    /// </summary>
    public static bool TryExpand(string? url, UrlContext ctx, out string expanded, out string? error)
    {
        expanded = "";
        error = null;
        string? missing = null;
        var text = Token().Replace(url?.Trim() ?? "", m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var v = key switch { "league" => ctx.League, "item" => ctx.Item, _ => ctx.Character };
            if (string.IsNullOrWhiteSpace(v)) { missing ??= key; return ""; }
            return Uri.EscapeDataString(v.Trim());
        });
        if (missing != null)
        {
            error = missing switch { "league" => "league unknown", "item" => "no hovered item", _ => "character name unknown" };
            return false;
        }
        if (!TryValidate(text, out expanded, out error)) return false;
        return true;
    }

    /// <summary>Only absolute http/https URLs — never file:, javascript:, custom protocol handlers or paths.</summary>
    public static bool TryValidate(string? url, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            error = "only http(s) URLs are allowed";
            return false;
        }
        normalized = uri.AbsoluteUri;
        return true;
    }
}

/// <summary>Opens a validated http(s) URL in the user's default browser. Nothing is sent to the game.</summary>
public static class BrowserLauncher
{
    public static bool Open(string url) => Open(url, out _);

    public static bool Open(string url, out string? error)
    {
        if (!Bookmarks.TryValidate(url, out var safe, out error)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsLinux())
        {
            try
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(safe);
                using var p = Process.Start(psi);
                return true;
            }
            catch (Exception ex2) { error = $"open failed: {ex.Message}; xdg-open: {ex2.Message}"; return false; }
        }
        catch (Exception ex) { error = "open failed: " + ex.Message; return false; }
    }
}
