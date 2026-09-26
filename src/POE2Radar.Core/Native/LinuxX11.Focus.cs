using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace POE2Radar.Core.Native;

public static partial class LinuxX11
{
    // ── Foreground detection that survives Wayland compositors. XWayland's _NET_ACTIVE_WINDOW is only as good as
    //    the compositor's X window manager: Hyprland (verified on 0.56) leaves it at None while an X client is
    //    focused, so "is PoE2 in front?" read from X alone is always false there. On Hyprland we ask the
    //    compositor itself (IPC socket, "activewindow") and compare process ids; elsewhere the X answer is used,
    //    matched by window id OR by the window's _NET_WM_PID (Wine may recreate its toplevel on a mode switch). ──

    private static readonly string? HyprSocket = FindHyprSocket();

    private static readonly object HyprGate = new();

    private static long _hyprAt;

    private static int _hyprPid;

    /// <summary>True when running under Hyprland with a reachable IPC socket.</summary>
    public static bool IsHyprland => HyprSocket is not null;

    private static string? FindHyprSocket()
    {
        var sig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (string.IsNullOrEmpty(sig)) return null;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        foreach (var dir in new[] { runtime is null ? null : Path.Combine(runtime, "hypr", sig), Path.Combine("/tmp/hypr", sig) })
            if (dir is not null && File.Exists(Path.Combine(dir, ".socket.sock"))) return Path.Combine(dir, ".socket.sock");
        return null;
    }

    /// <summary>PID of Hyprland's focused window (0 = none / unknown). Cached ~100 ms; thread-safe.</summary>
    public static int HyprlandActivePid()
    {
        if (HyprSocket is null) return 0;
        lock (HyprGate)
        {
            var now = Environment.TickCount64;
            if (now - _hyprAt < 100) return _hyprPid;
            _hyprAt = now;
            _hyprPid = QueryHyprlandPid(HyprSocket);
            return _hyprPid;
        }
    }

    private static int QueryHyprlandPid(string socketPath)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 100, SendTimeout = 100 };
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            s.Send(Encoding.ASCII.GetBytes("j/activewindow"));
            var buf = new byte[16384];
            var total = 0;
            int n;
            while (total < buf.Length && (n = s.Receive(buf, total, buf.Length - total, SocketFlags.None)) > 0) total += n;
            return ParseHyprlandPid(buf.AsSpan(0, total));
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException) { return 0; }
    }

    /// <summary>The "pid" field of a Hyprland <c>j/activewindow</c> reply (0 when absent / not JSON).</summary>
    public static int ParseHyprlandPid(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!JsonDocument.TryParseValue(ref reader, out var doc)) return 0;
            using (doc)
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("pid", out var p)
                       && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pid) ? pid : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>Is the game (window <paramref name="hwnd"/>, process <paramref name="pid"/>) the focused window?</summary>
    public static bool IsGameForeground(nint hwnd, int pid)
    {
        if (IsHyprland) return pid != 0 && HyprlandActivePid() == pid;
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        if (fg == hwnd) return true;
        EnsureDisplay();
        return pid != 0 && _display != 0 && WindowPid(unchecked((nuint)fg)) == pid;
    }
}
