using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace POE2Radar.Core.Game;

public sealed record GameBinding(string Action, int Key, int Modifiers)
{
    public bool Usable => IsSupported(Key, Modifiers);
    public string Label => (Modifiers.HasFlagBits(2) ? "Ctrl+" : "")
        + (Modifiers.HasFlagBits(1) ? "Shift+" : "") + (Modifiers.HasFlagBits(4) ? "Alt+" : "") + KeyName(Key);
    public static bool IsSupported(int key, int modifiers = 0) => modifiers is >= 0 and <= 7
        && key is (1 or 2 or 4 or 5 or 6 or 32 or >= 48 and <= 57 or >= 65 and <= 90
            or >= 96 and <= 111 or >= 186 and <= 192 or >= 219 and <= 222);
    public static string KeyName(int key) => key switch
    {
        0 => "Unbound", 1 => "Left mouse", 2 => "Right mouse", 4 => "Middle mouse",
        5 => "Mouse 4", 6 => "Mouse 5", 32 => "Space",
        >= 48 and <= 57 or >= 65 and <= 90 => ((char)key).ToString(),
        >= 96 and <= 105 => "Numpad " + (key - 96),
        106 => "Numpad *", 107 => "Numpad +", 109 => "Numpad -", 110 => "Numpad .", 111 => "Numpad /",
        186 => ";", 187 => "=", 188 => ",", 189 => "-", 190 => ".", 191 => "/", 192 => "`",
        219 => "[", 220 => "\\", 221 => "]", 222 => "'", _ => "VK " + key
    };
}

internal static class InputBits
{
    public static bool HasFlagBits(this int value, int flag) => (value & flag) != 0;
}

public sealed record GameInputSnapshot(bool Complete, string Mode, string Source, string Fingerprint,
    IReadOnlyList<GameBinding> Bindings, IReadOnlyList<string> Warnings)
{
    public GameBinding? Find(string action) => Bindings.FirstOrDefault(b => b.Action == action);
}

/// <summary>Reads settings only; never changes the game's configuration or assumes gem order is bar order.</summary>
public static class GameInputConfig
{
    private const string FileName = "poe2_production_Config.ini";
    public static GameInputSnapshot Parse(string text, string source = "Game configuration")
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                sections.TryAdd(section, new(StringComparer.OrdinalIgnoreCase));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && sections.TryGetValue(section, out var values))
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        var warnings = new List<string>();
        var ui = sections.GetValueOrDefault("UI");
        var modeKnown = bool.TryParse(ui?.GetValueOrDefault("use_wasd_to_move"), out var wasd);
        var mode = modeKnown ? (wasd ? "WASD" : "Mouse") : "Unknown";
        if (!modeKnown) warnings.Add("Movement mode is missing; select a valid PoE2 configuration before importing keys.");
        var actions = sections.GetValueOrDefault(wasd ? "WASD_ACTION_KEYS" : "ACTION_KEYS");
        var bindings = new List<GameBinding>();
        if (actions is null) warnings.Add("The active action-key section is missing.");
        else foreach (var (action, value) in actions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var modifiers = 0;
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var key)
                || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out modifiers))
                || key is < 0 or > 255 || modifiers is < 0 or > 7)
            { warnings.Add("Invalid binding for " + action + "."); continue; }
            bindings.Add(new(action, key, modifiers));
        }
        // Require every normal slot, including explicitly unbound entries, to reject truncated files.
        var complete = modeKnown && Enumerable.Range(1, 13).All(i => bindings.Any(b => b.Action == "use_bound_skill" + i));
        if (!complete) warnings.Add("Key scan incomplete. No default keys will be substituted.");
        var canonical = mode + "\n" + string.Join('\n', bindings.Select(b => $"{b.Action}={b.Key},{b.Modifiers}"));
        return new(complete, mode, source, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), bindings, warnings);
    }

    public static GameInputSnapshot Read()
    {
        var explicitPath = Environment.GetEnvironmentVariable("POE2_CONFIG_PATH");
        var candidates = string.IsNullOrWhiteSpace(explicitPath) ? DiscoverPaths() : new[] { explicitPath };
        var found = new List<GameInputSnapshot>();
        foreach (var path in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 1024 * 1024) continue;
                var snapshot = Parse(File.ReadAllText(path), path);
                if (found.All(f => f.Fingerprint != snapshot.Fingerprint)) found.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (found.Count == 1) return found[0];
        var reason = found.Count == 0
            ? "PoE2 configuration was not found. Set POE2_CONFIG_PATH to its full file path, or choose keys manually."
            : "Several PoE2 configurations have different bindings. Set POE2_CONFIG_PATH to the active file; keys will not be guessed.";
        return new(false, "Unknown", "", "", Array.Empty<GameBinding>(), new[] { reason });
    }

    public static IEnumerable<string> DiscoverPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var paths = new List<string> { Path.Combine(docs, "My Games", "Path of Exile 2", FileName),
            Path.Combine(home, "Documents", "My Games", "Path of Exile 2", FileName),
            Path.Combine(home, "OneDrive", "Documents", "My Games", "Path of Exile 2", FileName) };
        if (!OperatingSystem.IsLinux()) return paths;
        var prefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        var prefixes = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) prefixes.Add(prefix);
        var steamRoots = new List<string> { Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"),
            Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam") };
        // Steam libraries may be on other disks. Read only the library path entries.
        foreach (var steam in steamRoots.ToArray())
        {
            try
            {
                var libraries = Path.Combine(steam, "steamapps/libraryfolders.vdf");
                if (!File.Exists(libraries)) continue;
                foreach (var line in File.ReadLines(libraries))
                {
                    var parts = line.Split('"');
                    if (parts.Length >= 4 && parts[1] == "path" && Path.IsPathRooted(parts[3])) steamRoots.Add(parts[3]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var steam in steamRoots.Distinct())
        {
            var compat = Path.Combine(steam, "steamapps/compatdata");
            // Standalone installs use custom compatdata IDs. A bounded one-level search also covers these.
            try
            {
                if (Directory.Exists(compat)) prefixes.AddRange(Directory.EnumerateDirectories(compat).Take(512).Select(p => Path.Combine(p, "pfx")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var pfx in prefixes)
        {
            var users = Path.Combine(pfx, "drive_c/users");
            try
            {
                if (Directory.Exists(users)) paths.AddRange(Directory.EnumerateDirectories(users).Take(32)
                    .Select(p => Path.Combine(p, "Documents/My Games/Path of Exile 2", FileName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return paths;
    }
}
