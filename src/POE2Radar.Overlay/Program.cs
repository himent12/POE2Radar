using POE2Radar.Core;
using POE2Radar.Overlay;

Console.WriteLine("POE2Radar — map/radar overlay");
Console.WriteLine("=============================");

ProcessHandle? process;
try
{
    process = ProcessHandle.AttachToPoE();
}
catch (UnauthorizedAccessException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("PoE2 is running, but this process is not allowed to read it.");
    Console.Error.WriteLine("On Arch, yama.ptrace_scope=1 blocks attaching to Proton.");
    Console.Error.WriteLine("Fix (this boot):  sudo sysctl kernel.yama.ptrace_scope=0");
    Console.Error.WriteLine("Then run ./run.sh again.  (run.sh will do the sysctl for you.)");
    return 3;
}
if (process is null)
{
    Console.Error.WriteLine("PoE2 not running (no matching process found).");
    Console.Error.WriteLine("Start Path of Exile 2, load into a zone, then run ./run.sh again.");
    return 1;
}
using (process)
{
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    var reader = new MemoryReader(process);

    var slot = Bootstrap.ResolveGameStateSlot(process, reader);
    if (slot == 0)
        return 2;

    Console.WriteLine();
    Console.WriteLine("Radar running. Open the in-game map to see terrain + entities.");
    Console.WriteLine("Atlas: open it in-game; rings are auto-positioned. F10 over a tile = inspect its map/content/biome.");
    Console.WriteLine("Ctrl+C to exit.");

    using var app = new RadarApp(process, reader, slot);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestShutdown(); };
    app.Run();
}

Console.WriteLine("Done.");
return 0;
