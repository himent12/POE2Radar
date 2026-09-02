using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay;

/// <summary>
/// Resolves the PoE2 GameState global-pointer slot via the "Game States" AOB pattern, validated
/// by confirming the full chain resolves to a real local player. Returns the slot address (the
/// thing the RIP-relative instruction points at); deref it each tick to get the live GameState.
/// </summary>
internal static class Bootstrap
{
    public static nint ResolveGameStateSlot(ProcessHandle process, MemoryReader reader)
    {
        if (AobPatterns.GameStateRefs.Length == 0)
        {
            Console.Error.WriteLine("No GameState AOB patterns committed.");
            return 0;
        }

        Console.WriteLine($"Scanning for GameState via 'Game States' AOB pattern...");
        Console.WriteLine($"  module 0x{process.MainModuleBase:X} size 0x{process.MainModuleSize:X} ({process.ProcessName})");
        var exec = AobScanner.ReadExecutableSections(process, reader);
        Console.WriteLine($"  executable sections: {exec.Count}  ({exec.Sum(s => (long)s.Bytes.Length) / (1024 * 1024)} MB)");
        var anySlots = false;
        foreach (var pattern in AobPatterns.GameStateRefs)
        {
            var slots = AobScanner.ScanForResolvedAddresses(process, reader, pattern).Distinct().ToList();
            Console.WriteLine($"  pattern hits: {slots.Count}");
            foreach (var slot in slots)
            {
                anySlots = true;
                var live = new Poe2Live(reader, slot);
                if (live.TryResolve(out _, out _, out var localPlayer))
                {
                    Console.WriteLine($"  GameState slot: 0x{slot:X16}  (LocalPlayer 0x{localPlayer:X16})");
                    return slot;
                }
            }
        }

        if (!anySlots)
        {
            Console.Error.WriteLine("AOB pattern did not match in the game's .text. Module mapping may be wrong.");
            return 0;
        }
        Console.Error.WriteLine("Pattern matched but no slot resolved to an in-game chain.");
        Console.Error.WriteLine("Make sure you're loaded into a zone (not at login / character select).");
        return 0;
    }
}
