using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ClientLogLocatorTests
{
    private const string Vdf = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"/home/me/.local/share/Steam"
                "label"		""
                "apps" { "228980" "1" }
            }
            "1"
            {
                "path"		"/mnt/games/SteamLibrary"
            }
        }
        """;

    private const string WinVdf = """
        "libraryfolders"
        {
            "0" { "path"		"C:\\Program Files (x86)\\Steam" }
            "1" { "path"		"D:\\SteamLibrary" }
        }
        """;

    [Fact]
    public void Parses_library_folder_paths_and_unescapes()
    {
        Assert.Equal(["/home/me/.local/share/Steam", "/mnt/games/SteamLibrary"], ClientLogLocator.ParseLibraryFolders(Vdf));
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], ClientLogLocator.ParseLibraryFolders(WinVdf));
        Assert.Empty(ClientLogLocator.ParseLibraryFolders("garbage"));
    }

    [Fact]
    public void Maps_wine_paths_to_unix()
    {
        Assert.Equal("/home/me/Games/PoE2", ClientLogLocator.MapWinePath(@"Z:\home\me\Games\PoE2", null));
        Assert.Equal("/pfx/dosdevices/c:/Program Files/GGG", ClientLogLocator.MapWinePath(@"C:\Program Files\GGG", "/pfx/"));
        Assert.Null(ClientLogLocator.MapWinePath(@"C:\Program Files\GGG", null));
        Assert.Equal("/already/unix", ClientLogLocator.MapWinePath("/already/unix", null));
        Assert.Null(ClientLogLocator.MapWinePath("relative\\path", null));
    }

    [Fact]
    public void Finds_game_dir_in_proc_cmdline()
    {
        var cmd = "C:\\windows\\system32\\steam.exe\0Z:\\home\\me\\.local\\share\\Steam\\steamapps\\common\\Path of Exile 2\\PathOfExileSteam.exe\0--nopatch\0";
        Assert.Equal("/home/me/.local/share/Steam/steamapps/common/Path of Exile 2", ClientLogLocator.GameDirFromCmdline(cmd));
        Assert.Equal("/games/poe2", ClientLogLocator.GameDirFromCmdline("/games/poe2/PathOfExile_x64.exe\0"));
        Assert.Equal("/pfx/dosdevices/c:/GGG/Path of Exile 2",
            ClientLogLocator.GameDirFromCmdline("C:\\GGG\\Path of Exile 2\\PathOfExile.exe\0", "/pfx"));
        Assert.Null(ClientLogLocator.GameDirFromCmdline("/usr/bin/wineserver\0-p\0"));
    }

    [Fact]
    public void Exe_path_on_windows_is_used_verbatim()
    {
        Assert.Equal(@"D:\Games\Path of Exile 2",
            ClientLogLocator.GameDirFromExePath(@"D:\Games\Path of Exile 2\PathOfExile.exe", windows: true));
        Assert.Null(ClientLogLocator.GameDirFromExePath(@"D:\Games\notepad.exe", windows: true));
    }

    [Fact]
    public void Reads_wine_prefix_from_environ()
    {
        Assert.Equal("/p", ClientLogLocator.WinePrefixFromEnviron("A=1\0WINEPREFIX=/p\0"));
        Assert.Equal("/compat/2694490/pfx", ClientLogLocator.WinePrefixFromEnviron("STEAM_COMPAT_DATA_PATH=/compat/2694490/\0"));
        Assert.Null(ClientLogLocator.WinePrefixFromEnviron("HOME=/home/me\0"));
    }

    [Fact]
    public void Linux_candidates_order_override_process_steam_then_libraries()
    {
        var roots = ClientLogLocator.LinuxSteamRoots("/home/me");
        var list = ClientLogLocator.BuildCandidates("/custom/Client.txt", ["/proc/game"], roots, [],
            p => p == "/home/me/.local/share/Steam/steamapps/libraryfolders.vdf" ? Vdf : null, windows: false);
        Assert.Equal("/custom/Client.txt", list[0]);
        Assert.Equal("/proc/game/logs/Client.txt", list[3]);
        Assert.Equal("/home/me/.local/share/Steam/steamapps/common/Path of Exile 2/logs/Client.txt", list[4]);
        Assert.Equal("/mnt/games/SteamLibrary/steamapps/common/Path of Exile 2/logs/Client.txt", list[5]);
        Assert.Contains("/home/me/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Path of Exile 2/logs/Client.txt", list);
        Assert.Equal(list.Count, list.Distinct().Count()); // the vdf lists the root library again
    }

    [Fact]
    public void Windows_candidates_include_libraries_and_standalone()
    {
        var list = ClientLogLocator.BuildCandidates("", [], [@"C:\Program Files (x86)\Steam"], ClientLogLocator.WindowsStandaloneDirs,
            p => p == @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf" ? WinVdf : null, windows: true);
        Assert.Equal(
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\logs\Client.txt",
            @"D:\SteamLibrary\steamapps\common\Path of Exile 2\logs\Client.txt",
            @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2\logs\Client.txt",
            @"C:\Program Files\Grinding Gear Games\Path of Exile 2\logs\Client.txt",
        ], list);
    }

    [Fact]
    public void Override_may_be_the_game_folder()
    {
        var list = ClientLogLocator.BuildCandidates("/games/poe2/", [], [], [], _ => null, windows: false);
        Assert.Equal("/games/poe2/logs/Client.txt", ClientLogLocator.FirstExisting(list, p => p == "/games/poe2/logs/Client.txt"));
    }

    [Fact]
    public void First_existing_skips_missing_and_throwing_probes()
    {
        Assert.Equal("b", ClientLogLocator.FirstExisting(["a", "x", "b"], p => p == "x" ? throw new IOException() : p == "b"));
        Assert.Null(ClientLogLocator.FirstExisting(["a"], _ => false));
    }

    [Fact]
    public void Real_locate_uses_existing_override_file()
    {
        var file = Path.GetTempFileName();
        try { Assert.Equal(file, ClientLogLocator.Locate(file)); }
        finally { File.Delete(file); }
    }
}
