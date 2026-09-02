using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using POE2Radar.Core.Native;

namespace POE2Radar.Core;

/// <summary>
/// Owns the OS-level handle returned by OpenProcess plus the resolved main-module base + size.
/// Lives for the duration of one attach session; release via Dispose.
/// </summary>
public sealed class ProcessHandle : IDisposable
{
    public int ProcessId { get; }
    public string ProcessName { get; }
    public string ModulePath { get; }
    public nint MainModuleBase { get; }
    public uint MainModuleSize { get; }
    internal nint Handle { get; private set; }

    private ProcessHandle(int processId, string processName, string modulePath, nint mainModuleBase, uint mainModuleSize, nint handle)
    {
        ProcessId = processId;
        ProcessName = processName;
        ModulePath = modulePath;
        MainModuleBase = mainModuleBase;
        MainModuleSize = mainModuleSize;
        Handle = handle;
    }

    /// <summary>
    /// Find a running PoE client by process name, open it for read access, and resolve the EXE module base.
    /// Returns null if no matching process is running.
    /// </summary>
    /// <param name="candidateNames">Process names to search for (without ".exe"). Defaults cover standalone + Steam clients.</param>
    public static ProcessHandle? AttachToPoE(IReadOnlyList<string>? candidateNames = null)
    {
        // PoE2 client process names (window title "Path of Exile 2"). Source: GameHelper2
        // GameProcessName.cs — see resources/community-offsets.md.
        candidateNames ??= PoeProcessNames.Stems;

        if (OperatingSystem.IsLinux())
            return AttachToPoELinux(candidateNames);

        foreach (var name in candidateNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length == 0) continue;
                var proc = procs[0]; // ambiguous-multiple-instances is a "deal with later" problem
                return AttachToProcess(proc.Id, name);
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Open the given PID for read access, locate the main EXE module, and return a handle wrapper.
    /// Throws on OpenProcess / ptrace failure (typically access denied — re-run as admin, or on
    /// Linux relax yama.ptrace_scope / set cap_sys_ptrace).
    /// </summary>
    public static ProcessHandle AttachToProcess(int processId, string? expectedProcessName = null)
    {
        if (OperatingSystem.IsLinux())
            return AttachToProcessLinux(processId, expectedProcessName);

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)processId);

        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({processId}) failed");

        try
        {
            var (modulePath, baseAddr, size) = ResolveMainModule(handle, expectedProcessName);
            var name = expectedProcessName ?? Path.GetFileNameWithoutExtension(modulePath);
            var result = new ProcessHandle(processId, name, modulePath, baseAddr, size, handle);
            handle = 0; // ownership transferred to result
            return result;
        }
        finally
        {
            if (handle != 0) NativeMethods.CloseHandle(handle);
        }
    }

    private static ProcessHandle? AttachToPoELinux(IReadOnlyList<string> candidateNames)
    {
        foreach (var pid in FindLinuxPoePids(candidateNames))
        {
            try { return AttachToProcessLinux(pid, null); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Attach PID {pid} failed: {ex.Message}");
            }
        }
        return null;
    }

    private static IEnumerable<int> FindLinuxPoePids(IReadOnlyList<string> candidateNames)
    {
        var found = new List<(int Pid, long ExeBytes)>();
        string[] procDirs;
        try { procDirs = Directory.GetDirectories("/proc"); }
        catch { yield break; }

        foreach (var dir in procDirs)
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsDigit(name[0])) continue;
            if (!int.TryParse(name, out var pid) || pid <= 0) continue;

            string cmdline;
            try { cmdline = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' '); }
            catch { continue; }

            var hit = false;
            foreach (var stem in candidateNames)
            {
                if (cmdline.Contains(stem, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
            }
            if (!hit)
            {
                // Proton often truncates comm to 15 chars ("PathOfExileSte") and cmdline may be the
                // wine-preloader path; maps are the source of truth.
                try
                {
                    var maps = File.ReadAllText(Path.Combine(dir, "maps"));
                    if (maps.Contains("PathOfExile", StringComparison.OrdinalIgnoreCase)
                        && maps.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                        hit = true;
                }
                catch { /* ignore */ }
            }
            if (!hit) continue;

            long exeBytes = 0;
            try
            {
                foreach (var v in LinuxMemory.ReadMaps(pid))
                {
                    if (v.Path.Contains("PathOfExile", StringComparison.OrdinalIgnoreCase)
                        && v.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        exeBytes += (long)v.End - (long)v.Start;
                }
            }
            catch { /* ignore */ }
            found.Add((pid, exeBytes));
        }

        // Prefer the process that actually maps the game EXE (skip wineserver / steam helpers).
        foreach (var (pid, _) in found.OrderByDescending(t => t.ExeBytes))
            yield return pid;
    }

    private static ProcessHandle AttachToProcessLinux(int processId, string? expectedProcessName)
    {
        if (!Directory.Exists($"/proc/{processId}"))
            throw new InvalidOperationException($"Process {processId} is not running.");

        if (!LinuxMemory.TryFindMainModule(processId, expectedProcessName, out var modulePath, out var baseAddr, out var size))
        {
            throw new InvalidOperationException(
                $"Could not find PathOfExile*.exe in /proc/{processId}/maps. {LinuxMemory.PtraceHint()}");
        }

        // Probe a few bytes so a ptrace_scope denial fails here with a useful message, not later
        // as a mysterious AOB miss.
        unsafe
        {
            byte b;
            if (!LinuxMemory.TryRead(processId, baseAddr, &b, 1, out _))
            {
                throw new UnauthorizedAccessException(
                    $"Cannot read PID {processId} memory. {LinuxMemory.PtraceHint()}");
            }
        }

        var name = expectedProcessName ?? Path.GetFileNameWithoutExtension(modulePath.Replace('\\', '/'));
        // On Linux the "handle" is the pid; CloseHandle is a no-op. MemoryReader uses ProcessId.
        return new ProcessHandle(processId, name, modulePath, baseAddr, size, (nint)processId);
    }

    private static (string ModulePath, nint BaseAddress, uint Size) ResolveMainModule(nint handle, string? expectedName)
    {
        // First call to size the buffer.
        if (!NativeMethods.EnumProcessModulesEx(handle, null, 0, out var needed, NativeMethods.LIST_MODULES_ALL))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumProcessModulesEx (sizing) failed");

        var moduleCount = needed / (uint)nint.Size;
        var modules = new nint[moduleCount];
        if (!NativeMethods.EnumProcessModulesEx(handle, modules, needed, out _, NativeMethods.LIST_MODULES_ALL))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumProcessModulesEx failed");

        // The first module returned is always the EXE itself for the process.
        // We still verify the filename matches what the caller expected (when supplied), to fail loud if the OS reorders things.
        var nameBuf = new char[1024];
        var len = NativeMethods.GetModuleFileNameEx(handle, modules[0], nameBuf, (uint)nameBuf.Length);
        if (len == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleFileNameEx failed for main module");

        var modulePath = new string(nameBuf, 0, (int)len);
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);

        if (expectedName != null && !string.Equals(moduleName, expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Main module name '{moduleName}' does not match expected '{expectedName}'. " +
                $"This indicates EnumProcessModulesEx returned an unexpected ordering.");

        if (!NativeMethods.GetModuleInformation(handle, modules[0], out var info, (uint)Marshal.SizeOf<NativeMethods.ModuleInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleInformation failed");

        return (modulePath, info.LpBaseOfDll, info.SizeOfImage);
    }

    /// <summary>
    /// Walk the entire user-space address range of the target process via VirtualQueryEx.
    /// Yields each contiguous region. Filter by State / Type / Protect to find scannable pages.
    /// </summary>
    private const long UserModeAddressUpperBound = 0x7FFF_FFFF_FFFFL; // user-mode upper bound on x64 Windows

    internal IEnumerable<NativeMethods.MemoryBasicInformation> EnumerateRegions(
        nint startAddress = 0,
        nint endAddress = 0)
    {
        if (endAddress == 0) endAddress = unchecked((nint)UserModeAddressUpperBound);
        if (Handle == 0) throw new ObjectDisposedException(nameof(ProcessHandle));

        if (OperatingSystem.IsLinux())
        {
            foreach (var v in LinuxMemory.ReadMaps(ProcessId))
            {
                if (v.End <= startAddress) continue;
                if (v.Start >= endAddress) break;
                yield return LinuxMemory.ToMbi(v);
            }
            yield break;
        }

        var addr = startAddress;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        while (addr < endAddress)
        {
            var written = NativeMethods.VirtualQueryEx(Handle, addr, out var mbi, mbiSize);
            if (written == 0) yield break; // either past end or process is gone

            yield return mbi;

            var next = (nint)((long)mbi.BaseAddress + (long)mbi.RegionSize);
            if (next <= addr) yield break; // defensive: avoid infinite loop on malformed responses
            addr = next;
        }
    }

    /// <summary>Convenience filter: only committed, readable, non-guard regions â€” what's safe to scan.</summary>
    public IEnumerable<(nint Address, long Size)> EnumerateReadableRegions(
        bool privateOnly = true,
        bool excludeImage = false)
    {
        foreach (var mbi in EnumerateRegions())
        {
            if (mbi.State != NativeMethods.MEM_COMMIT) continue;
            if ((mbi.Protect & NativeMethods.PAGE_GUARD) != 0) continue;
            if ((mbi.Protect & NativeMethods.READABLE_PROTECT_MASK) == 0) continue;
            if (privateOnly && mbi.Type != NativeMethods.MEM_PRIVATE) continue;
            if (excludeImage && mbi.Type == NativeMethods.MEM_IMAGE) continue;

            yield return (mbi.BaseAddress, (long)mbi.RegionSize);
        }
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            if (OperatingSystem.IsWindows())
                NativeMethods.CloseHandle(Handle);
            Handle = 0;
        }
        GC.SuppressFinalize(this);
    }
}
