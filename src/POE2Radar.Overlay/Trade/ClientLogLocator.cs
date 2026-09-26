using System.Diagnostics;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Trade;

/// <summary>
/// Finds the game's <c>logs/Client.txt</c>. Order: explicit override → the running client's install dir
/// → common Steam / standalone install paths (incl. every Steam library in libraryfolders.vdf). The
/// environment probing is thin; the path logic is pure (injected readers) so it's testable on any OS.
/// </summary>
public static partial class ClientLogLocator
{
    public const string GameFolder = "Path of Exile 2";

    /// <summary>First existing candidate, or null.</summary>
    /// <param name="exePathHint">The client's module path if already known (e.g. <c>ProcessHandle.ModulePath</c>).</param>
    public static string? Locate(string? overridePath, int? processId = null, string? exePathHint = null)
    {
        try { return FirstExisting(Candidates(overridePath, processId, exePathHint), File.Exists); }
        catch (Exception) { return null; }
    }

    /// <summary>Every path <see cref="Locate"/> would try, in order (for a "searched here" status line).</summary>
    public static IReadOnlyList<string> Candidates(string? overridePath, int? processId = null, string? exePathHint = null)
    {
        var windows = OperatingSystem.IsWindows();
        var gameDirs = new List<string>();
        var hint = GameDirFromExePath(exePathHint, processId is int p && !windows ? ReadWinePrefix(p) : null);
        if (hint != null) gameDirs.Add(hint);
        if (processId is int pid) gameDirs.AddRange(windows ? WindowsProcessDirs(pid) : LinuxProcessDirs(pid));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return BuildCandidates(overridePath, gameDirs, windows ? WindowsSteamRoots() : LinuxSteamRoots(home),
            windows ? WindowsStandaloneDirs : [], ReadTextOrNull, windows);
    }

    public static readonly string[] WindowsStandaloneDirs =
    [
        @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2",
        @"C:\Program Files\Grinding Gear Games\Path of Exile 2",
    ];

    public static string[] LinuxSteamRoots(string home) =>
    [
        home + "/.local/share/Steam",
        home + "/.steam/steam",
        home + "/.steam/root",
        home + "/.var/app/com.valvesoftware.Steam/.local/share/Steam",
        home + "/snap/steam/common/.local/share/Steam",
    ];

    /// <summary>
    /// Pure candidate ordering. <paramref name="readText"/> returns a file's text or null (used for
    /// libraryfolders.vdf). Paths are joined with the target OS's separator, not the host's.
    /// </summary>
    public static IReadOnlyList<string> BuildCandidates(string? overridePath, IEnumerable<string> processGameDirs,
        IEnumerable<string> steamRoots, IEnumerable<string> standaloneDirs, Func<string, string?> readText, bool windows)
    {
        var list = new List<string>();
        var ov = overridePath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(ov))
        {
            list.Add(ov); // a file path; if it's the game (or logs) folder the two below catch it
            list.Add(Join(windows, ov, "logs", "Client.txt"));
            list.Add(Join(windows, ov, "Client.txt"));
        }
        foreach (var dir in processGameDirs) AddGameDir(list, dir, windows);
        foreach (var root in steamRoots)
        {
            AddGameDir(list, Join(windows, root, "steamapps", "common", GameFolder), windows);
            var vdf = readText(Join(windows, root, "steamapps", "libraryfolders.vdf"));
            if (vdf == null) continue;
            foreach (var lib in ParseLibraryFolders(vdf))
                AddGameDir(list, Join(windows, lib, "steamapps", "common", GameFolder), windows);
        }
        foreach (var dir in standaloneDirs) AddGameDir(list, dir, windows);
        var cmp = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return list.Distinct(cmp).ToList();
    }

    private static void AddGameDir(List<string> list, string dir, bool windows)
    {
        if (!string.IsNullOrWhiteSpace(dir)) list.Add(Join(windows, dir, "logs", "Client.txt"));
    }

    public static string? FirstExisting(IEnumerable<string> candidates, Func<string, bool> fileExists) =>
        candidates.FirstOrDefault(c => { try { return fileExists(c); } catch (Exception) { return false; } });

    [GeneratedRegex(@"""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRx();

    /// <summary>Steam library roots from <c>steamapps/libraryfolders.vdf</c> ("path" entries, VDF-unescaped).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdf) =>
        VdfPathRx().Matches(vdf)
            .Select(m => Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1"))
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>
    /// Game dir from a NUL-separated <c>/proc/&lt;pid&gt;/cmdline</c>: the first <c>PathOfExile*.exe</c>
    /// argument, mapped from a Wine path (<c>Z:\home\…</c> → <c>/home/…</c>) when needed.
    /// </summary>
    public static string? GameDirFromCmdline(string cmdline, string? winePrefix = null)
    {
        foreach (var arg in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = GameDirFromExePath(arg.Trim(), winePrefix, windows: false);
            if (dir != null) return dir;
        }
        return null;
    }

    /// <summary>Directory of a <c>PathOfExile*.exe</c> path (unix or Wine-mapped); null for anything else.</summary>
    /// <param name="windows">Target OS (default: host). On Windows the path is used as-is; elsewhere it's Wine-mapped.</param>
    public static string? GameDirFromExePath(string? exePath, string? winePrefix = null, bool? windows = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var slash = exePath.LastIndexOfAny(['/', '\\']);
        var name = exePath[(slash + 1)..];
        if (slash < 0 || !name.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var dir = exePath[..slash];
        if (windows ?? OperatingSystem.IsWindows()) return dir.Length == 2 && dir[1] == ':' ? dir + "\\" : dir;
        return dir.StartsWith('/') ? dir : MapWinePath(dir, winePrefix);
    }

    /// <summary>
    /// Wine path → unix. <c>Z:</c> is the host root; other drives go through the prefix's
    /// <c>dosdevices/&lt;x&gt;:</c> symlink (so <c>C:</c> lands in <c>drive_c</c>). Null when unmappable.
    /// </summary>
    public static string? MapWinePath(string path, string? winePrefix)
    {
        if (path.StartsWith('/')) return path;
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return null;
        var rest = path[2..].Replace('\\', '/').TrimStart('/');
        var drive = char.ToLowerInvariant(path[0]);
        if (drive == 'z') return "/" + rest;
        if (string.IsNullOrEmpty(winePrefix)) return null;
        return $"{winePrefix.TrimEnd('/')}/dosdevices/{drive}:" + (rest.Length > 0 ? "/" + rest : "");
    }

    /// <summary>Prefix from a NUL-separated <c>/proc/&lt;pid&gt;/environ</c>: WINEPREFIX, else Proton's STEAM_COMPAT_DATA_PATH/pfx.</summary>
    public static string? WinePrefixFromEnviron(string environ)
    {
        string? compat = null;
        foreach (var kv in environ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (kv.StartsWith("WINEPREFIX=", StringComparison.Ordinal) && kv.Length > 11) return kv[11..];
            if (kv.StartsWith("STEAM_COMPAT_DATA_PATH=", StringComparison.Ordinal) && kv.Length > 23)
                compat = kv[23..].TrimEnd('/') + "/pfx";
        }
        return compat;
    }

    private static string Join(bool windows, string root, params string[] parts)
    {
        var sep = windows ? '\\' : '/';
        var path = root.TrimEnd('/', '\\');
        foreach (var part in parts) path += sep + part;
        return path;
    }

    private static string? ReadTextOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception) { return null; }
    }

    private static string? ReadWinePrefix(int pid)
    {
        var env = ReadTextOrNull($"/proc/{pid}/environ");
        return env == null ? null : WinePrefixFromEnviron(env);
    }

    private static IEnumerable<string> LinuxProcessDirs(int pid)
    {
        var dirs = new List<string>();
        var cmdline = ReadTextOrNull($"/proc/{pid}/cmdline");
        if (cmdline != null && GameDirFromCmdline(cmdline, ReadWinePrefix(pid)) is { } fromCmd) dirs.Add(fromCmd);
        // Proton starts the client with its install dir as cwd.
        try
        {
            if (new DirectoryInfo($"/proc/{pid}/cwd").ResolveLinkTarget(false)?.FullName is { } cwd) dirs.Add(cwd);
        }
        catch (Exception) { }
        return dirs;
    }

    private static IEnumerable<string> WindowsProcessDirs(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var exe = proc.MainModule?.FileName;
            var dir = exe == null ? null : Path.GetDirectoryName(exe);
            return dir == null ? [] : [dir];
        }
        catch (Exception) { return []; } // exited, or access denied on a protected process
    }

    private static IEnumerable<string> WindowsSteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steam && steam.Length > 0) roots.Add(steam.Replace('/', '\\'));
            }
            catch (Exception) { }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        return roots;
    }
}
