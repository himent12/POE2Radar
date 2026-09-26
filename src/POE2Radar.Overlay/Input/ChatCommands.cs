using System.Text;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Input;

/// <summary>A user chat macro: <see cref="Text"/> (one chat line per text line) typed when <see cref="Hotkey"/> is pressed.</summary>
public sealed class ChatCommand
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Live values the placeholders expand to; null = unknown right now.</summary>
public sealed record PlaceholderContext(string? Character = null, string? LastWhisper = null, string? League = null, string? Area = null);

/// <summary>
/// Pure placeholder expansion for chat macros. Braced tokens (<c>{char}</c> <c>{last}</c> <c>{league}</c>
/// <c>{area}</c>, case-insensitive) always become the bare value. The PoE-Overlay-style <c>@char</c>/<c>@last</c>
/// (exact lowercase, whole word — so real whisper targets like <c>@Charlie</c> are untouched) follow PoE's
/// whisper syntax: at the START of a line they stay a whisper (<c>@last hi</c> → <c>@Name hi</c>), anywhere
/// else they are the bare name (<c>/invite @last</c> → <c>/invite Name</c>).
/// </summary>
public static partial class ChatCommands
{
    [GeneratedRegex(@"(?<![\w@])@(char|last)(?![\w])")]
    private static partial Regex AtToken();

    [GeneratedRegex(@"\{(char|last|league|area)\}", RegexOptions.IgnoreCase)]
    private static partial Regex BraceToken();

    /// <summary>
    /// Expand <paramref name="text"/> into chat lines (blank lines dropped). Fails — nothing should be sent —
    /// when a placeholder it uses is unknown, or when nothing is left to send.
    /// </summary>
    public static bool TryExpand(string? text, PlaceholderContext ctx, out IReadOnlyList<string> lines, out string? error)
    {
        lines = [];
        error = null;
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            string? missing = null;
            string Value(string token)
            {
                var v = token.ToLowerInvariant() switch
                {
                    "char" => ctx.Character,
                    "last" => ctx.LastWhisper,
                    "league" => ctx.League,
                    "area" => ctx.Area,
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(v)) { missing ??= token.ToLowerInvariant(); return ""; }
                return v.Trim();
            }
            line = BraceToken().Replace(line, m => Value(m.Groups[1].Value));
            line = AtToken().Replace(line, m =>
            {
                var v = Value(m.Groups[1].Value);
                return m.Index == 0 && v.Length > 0 ? "@" + v : v;
            });
            if (missing != null)
            {
                error = missing switch
                {
                    "char" => "character name unknown",
                    "last" => "no whisper partner yet",
                    "league" => "league unknown",
                    _ => "area unknown",
                };
                return false;
            }
            line = StripControl(line);
            if (line.Length > 0) result.Add(line);
        }
        if (result.Count == 0) { error = "nothing to send"; return false; }
        lines = result;
        return true;
    }

    private static string StripControl(string s)
    {
        if (!s.Any(char.IsControl)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }
}
