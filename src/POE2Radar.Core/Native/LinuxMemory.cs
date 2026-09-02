using System.Globalization;
using System.Runtime.InteropServices;

namespace POE2Radar.Core.Native;

/// <summary>
/// Linux / Proton process-memory backend. Reads via <c>process_vm_readv</c> (same permission
/// check as ptrace) with a <c>/proc/&lt;pid&gt;/mem</c> fallback, and describes address space
/// from <c>/proc/&lt;pid&gt;/maps</c> so AOB scans and typed reads work against a Wine-mapped
/// PoE2 client.
/// </summary>
internal static partial class LinuxMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public nint Base;
        public nuint Length;
    }

    [LibraryImport("libc", EntryPoint = "process_vm_readv", SetLastError = true)]
    private static unsafe partial nint ProcessVmReadv(
        int pid, Iovec* localIov, nuint localIovCount, Iovec* remoteIov, nuint remoteIovCount, nuint flags);

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static unsafe partial nint Pread(int fd, void* buf, nuint count, long offset);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseFd(int fd);

    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int ChunkSize = 1024 * 1024;

    public static unsafe bool TryRead(int pid, nint address, void* buffer, nuint size, out nuint bytesRead)
    {
        bytesRead = 0;
        if (pid <= 0 || address == 0 || size == 0 || buffer == null) return false;

        var dst = (byte*)buffer;
        nuint remaining = size;
        var remote = address;
        while (remaining > 0)
        {
            var chunk = remaining > (nuint)ChunkSize ? (nuint)ChunkSize : remaining;
            nuint got = 0;
            if (!TryReadChunk(pid, remote, dst, chunk, out got) || got == 0)
                return bytesRead == size;
            bytesRead += got;
            if (got < chunk) return bytesRead == size;
            dst += got;
            remote += (nint)got;
            remaining -= got;
        }
        return bytesRead == size;
    }

    private static unsafe bool TryReadChunk(int pid, nint address, void* buffer, nuint size, out nuint bytesRead)
    {
        bytesRead = 0;
        var local = new Iovec { Base = (nint)buffer, Length = size };
        var remote = new Iovec { Base = address, Length = size };
        var n = ProcessVmReadv(pid, &local, 1, &remote, 1, 0);
        if (n > 0)
        {
            bytesRead = (nuint)n;
            return true;
        }

        // Fallback: /proc/<pid>/mem. Same Yama/ptrace permission as process_vm_readv.
        var fd = Open($"/proc/{pid}/mem", O_RDONLY | O_CLOEXEC);
        if (fd < 0) return false;
        try
        {
            var r = Pread(fd, buffer, size, (long)address);
            if (r > 0)
            {
                bytesRead = (nuint)r;
                return true;
            }
            return false;
        }
        finally
        {
            CloseFd(fd);
        }
    }

    public static string PtraceHint()
    {
        var scope = "unknown";
        try { scope = File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim(); }
        catch { /* ignore */ }
        return
            $"Linux memory reads need ptrace permission (yama.ptrace_scope is currently {scope}). " +
            "Either: sudo sysctl kernel.yama.ptrace_scope=0   " +
            "or: sudo setcap cap_sys_ptrace=ep /path/to/POE2Radar.Overlay";
    }

    public readonly record struct Vma(
        nint Start,
        nint End,
        bool Read,
        bool Write,
        bool Execute,
        bool Private,
        string Path);

    public static List<Vma> ReadMaps(int pid)
    {
        var list = new List<Vma>();
        string text;
        try { text = File.ReadAllText($"/proc/{pid}/maps"); }
        catch { return list; }

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // address-end perms offset dev inode pathname
            var space = raw.IndexOf(' ');
            if (space < 0) continue;
            var range = raw.AsSpan(0, space);
            var dash = range.IndexOf('-');
            if (dash < 0) continue;
            if (!ulong.TryParse(range[..dash], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var startU)) continue;
            if (!ulong.TryParse(range[(dash + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var endU)) continue;
            if (endU <= startU) continue;

            var rest = raw.AsSpan(space + 1);
            if (rest.Length < 4) continue;
            var r = rest[0] == 'r';
            var w = rest[1] == 'w';
            var x = rest[2] == 'x';
            var priv = rest[3] == 'p';

            var path = "";
            var pathIdx = raw.IndexOf('/');
            var deletedIdx = raw.IndexOf(" (deleted)", StringComparison.Ordinal);
            var anonIdx = raw.IndexOf('[');
            if (pathIdx >= 0)
            {
                var end = deletedIdx >= pathIdx ? deletedIdx : raw.Length;
                path = raw[pathIdx..end].Trim();
            }
            else if (anonIdx >= 0)
            {
                path = raw[anonIdx..].Trim();
            }

            list.Add(new Vma((nint)startU, (nint)endU, r, w, x, priv, path));
        }
        return list;
    }

    public static NativeMethods.MemoryBasicInformation ToMbi(Vma v)
    {
        uint protect = NativeMethods.PAGE_NOACCESS;
        if (v.Execute && v.Write && v.Read) protect = NativeMethods.PAGE_EXECUTE_READWRITE;
        else if (v.Execute && v.Read) protect = NativeMethods.PAGE_EXECUTE_READ;
        else if (v.Execute) protect = NativeMethods.PAGE_EXECUTE;
        else if (v.Write && v.Read) protect = NativeMethods.PAGE_READWRITE;
        else if (v.Read) protect = NativeMethods.PAGE_READONLY;

        uint type;
        if (IsImagePath(v.Path)) type = NativeMethods.MEM_IMAGE;
        else if (v.Path.Length == 0 || v.Path[0] == '[') type = NativeMethods.MEM_PRIVATE;
        else type = v.Private ? NativeMethods.MEM_PRIVATE : NativeMethods.MEM_MAPPED;

        return new NativeMethods.MemoryBasicInformation
        {
            BaseAddress = v.Start,
            AllocationBase = v.Start,
            AllocationProtect = protect,
            RegionSize = (nuint)((long)v.End - (long)v.Start),
            State = NativeMethods.MEM_COMMIT,
            Protect = protect,
            Type = type,
        };
    }

    public static bool IsImagePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '[') return false;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryFindMainModule(int pid, string? expectedName, out string modulePath, out nint baseAddr, out uint size)
    {
        modulePath = "";
        baseAddr = 0;
        size = 0;
        var maps = ReadMaps(pid);
        if (maps.Count == 0) return false;

        var exeNames = expectedName is null
            ? PoeProcessNames.ExeFileNames
            : new[] { expectedName + ".exe", expectedName };

        Vma? first = null;
        nint min = nint.MaxValue, max = 0;
        string path = "";
        foreach (var v in maps)
        {
            if (string.IsNullOrEmpty(v.Path) || v.Path[0] == '[') continue;
            if (!MatchesExe(v.Path, exeNames)) continue;
            if (first is null)
            {
                first = v;
                path = v.Path;
                min = v.Start;
                max = v.End;
            }
            else if (string.Equals(v.Path, path, StringComparison.Ordinal))
            {
                if (v.Start < min) min = v.Start;
                if (v.End > max) max = v.End;
            }
        }

        if (first is null)
        {
            // Fallback: any mapping whose path contains PathOfExile.
            foreach (var v in maps)
            {
                if (v.Path.Contains("PathOfExile", StringComparison.OrdinalIgnoreCase)
                    && v.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    first = v;
                    path = v.Path;
                    min = v.Start;
                    max = v.End;
                    break;
                }
            }
            if (first is not null)
            {
                foreach (var v in maps)
                {
                    if (!string.Equals(v.Path, path, StringComparison.Ordinal)) continue;
                    if (v.Start < min) min = v.Start;
                    if (v.End > max) max = v.End;
                }
            }
        }

        if (first is null) return false;

        modulePath = path;
        baseAddr = min;
        size = (uint)Math.Clamp((long)max - (long)min, 0, uint.MaxValue);

        if (TryReadSizeOfImage(pid, min, out var peSize) && peSize > 0x1000)
            size = peSize;

        return true;
    }

    private static bool MatchesExe(string path, IReadOnlyList<string> names)
    {
        var file = Path.GetFileName(path);
        foreach (var n in names)
        {
            if (file.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Equals(n + ".exe", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static unsafe bool TryReadSizeOfImage(int pid, nint moduleBase, out uint sizeOfImage)
    {
        sizeOfImage = 0;
        Span<byte> hdr = stackalloc byte[0x200];
        fixed (byte* p = hdr)
        {
            if (!TryRead(pid, moduleBase, p, (nuint)hdr.Length, out var n) || n < 0x40) return false;
        }
        if (hdr[0] != (byte)'M' || hdr[1] != (byte)'Z') return false;
        var eLfanew = BitConverter.ToInt32(hdr[0x3C..]);
        if (eLfanew < 0 || eLfanew > 0x1000) return false;

        Span<byte> pe = stackalloc byte[0x80];
        fixed (byte* p = pe)
        {
            if (!TryRead(pid, moduleBase + eLfanew, p, (nuint)pe.Length, out var n) || n < 0x58) return false;
        }
        if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return false;
        var magic = BitConverter.ToUInt16(pe[24..]);
        if (magic != 0x20B && magic != 0x10B) return false;
        sizeOfImage = BitConverter.ToUInt32(pe[24..][56..]);
        return sizeOfImage >= 0x1000;
    }
}

/// <summary>PoE2 client process names (Windows .exe stem). Shared by Windows + Linux attach.</summary>
internal static class PoeProcessNames
{
    public static readonly string[] Stems =
        ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExile_KG", "PathOfExileEGS"];

    public static readonly string[] ExeFileNames =
        ["PathOfExile.exe", "PathOfExileSteam.exe", "PathOfExile_x64.exe", "PathOfExile_KG.exe", "PathOfExileEGS.exe"];
}
