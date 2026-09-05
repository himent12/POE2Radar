using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>Read-only smoke test of the production readers after a client patch.</summary>
internal static class PatchCheck
{
    public static int Run(ProcessHandle process, MemoryReader reader)
    {
        var slots = AobPatterns.GameStateRefs
            .SelectMany(pattern => AobScanner.ScanForResolvedAddresses(process, reader, pattern))
            .Distinct().ToArray();
        Console.WriteLine($"GameState pattern: {slots.Length} distinct slots");
        if (slots.Length != 1) return 1;
        var live = new Poe2Live(reader, slots[0]);
        if (!live.TryResolve(out var state, out var area, out var player))
        {
            Console.Error.WriteLine("FAIL chain: load a character into a zone.");
            return 1;
        }
        Console.WriteLine($"Slot 0x{slots[0]:X}; state 0x{state:X}; area 0x{area:X}; player 0x{player:X}");
        var failed = false;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
            failed |= !ok;
        }
        var code = live.AreaCode(area);
        Check("area code", code.Length > 0 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), code);
        var level = live.AreaLevel(area);
        Check("area level", level is > 0 and <= 100, level.ToString());
        Console.WriteLine($"Area hash: 0x{live.AreaHash(area):X8}");
        Console.WriteLine($"Player: {live.PlayerName(player)}; league: {live.LeagueName(area)}");
        var vitals = live.PlayerVitals(player);
        Check("vitals", vitals is not null, vitals.ToString() ?? "unavailable");
        var terrain = live.Terrain(area);
        Check("terrain", terrain is not null && terrain.Walkable.Any(b => b != 0),
            terrain is null ? "unavailable" : $"{terrain.Width}×{terrain.Height}");
        var position = live.PlayerGrid(player);
        Check("player grid", position is { } pos && float.IsFinite(pos.X) && float.IsFinite(pos.Y)
            && terrain is not null && pos.X >= 0 && pos.Y >= 0 && pos.X < terrain.Width && pos.Y < terrain.Height,
            position.ToString() ?? "unavailable");
        var entities = live.Entities(area);
        Check("entities", entities.Count > 0, entities.Count.ToString());
        Console.WriteLine($"Landmarks: {live.Landmarks(area).Count}");
        var map = live.ReadMap(state, area);
        Check("map discovery", float.IsFinite(map.Zoom) && map.Zoom is > 0.05f and < 8f, map.ToString());
        return failed ? 1 : 0;
    }
}
