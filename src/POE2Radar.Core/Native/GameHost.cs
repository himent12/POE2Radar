using System.Runtime.InteropServices;

namespace POE2Radar.Core.Native;

/// <summary>
/// Cross-platform game-window + input helpers. Windows talks to user32; Linux talks to X11
/// (Proton/Wine clients are XWayland windows even on a Wayland desktop).
/// </summary>
public static partial class GameHost
{
    public struct Point { public int X; public int Y; }
    public struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    public static nint FindWindowForProcess(int processId)
        => OperatingSystem.IsLinux() ? LinuxX11.FindWindowForProcess(processId) : Win32.FindWindowForProcess(processId);

    public static bool TryGetWindowRect(nint hwnd, out Rect rect)
    {
        if (OperatingSystem.IsLinux()) return LinuxX11.TryGetWindowRect(hwnd, out rect);
        return Win32.TryGetWindowRect(hwnd, out rect);
    }

    public static nint GetForegroundWindow()
        => OperatingSystem.IsLinux() ? LinuxX11.GetForegroundWindow() : Win32.GetForegroundWindow();

    public static bool GetCursorPos(out Point pt)
        => OperatingSystem.IsLinux() ? LinuxX11.GetCursorPos(out pt) : Win32.GetCursorPos(out pt);

    public static bool ScreenToClient(nint hwnd, ref Point pt)
    {
        if (OperatingSystem.IsLinux()) return LinuxX11.ScreenToClient(hwnd, ref pt);
        return Win32.ScreenToClient(hwnd, ref pt);
    }

    public static bool TryGetClientSize(nint hwnd, out int width, out int height)
    {
        width = height = 0;
        if (!TryGetWindowRect(hwnd, out var r)) return false;
        width = r.Width; height = r.Height;
        return width > 0 && height > 0;
    }

    /// <summary>True iff the given hwnd is the OS-level foreground window.</summary>
    public static bool IsForeground(nint hwnd) => hwnd != 0 && GetForegroundWindow() == hwnd;

    public static short GetAsyncKeyState(int vKey)
        => OperatingSystem.IsLinux() ? LinuxX11.GetAsyncKeyState(vKey) : Win32.GetAsyncKeyState(vKey);

    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    public static void TapKey(ushort vk)
    {
        if (OperatingSystem.IsLinux()) LinuxX11.TapKey(vk);
        else Win32.TapKey(vk);
    }

    public static void BeginHighResTimer()
    {
        if (OperatingSystem.IsWindows()) Win32.timeBeginPeriod(1);
    }

    public static void EndHighResTimer()
    {
        if (OperatingSystem.IsWindows()) Win32.timeEndPeriod(1);
    }

    public static int DetectMonitorHz(nint hwnd)
        => OperatingSystem.IsLinux() ? LinuxX11.DetectMonitorHz() : Win32.DetectMonitorHz(hwnd);

    public static int GetScreenWidth()
        => OperatingSystem.IsLinux() ? LinuxX11.ScreenWidth() : Win32.GetSystemMetrics(0);

    public static int GetScreenHeight()
        => OperatingSystem.IsLinux() ? LinuxX11.ScreenHeight() : Win32.GetSystemMetrics(1);

    internal static partial class Win32
    {
        [LibraryImport("user32.dll", EntryPoint = "EnumWindows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
        private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static partial uint GetWindowThreadProcessId(nint hwnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint hwnd);

        [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(nint hwnd, out Rect lpRect);

        [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
        public static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
        public static partial int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
        private static extern bool GetCursorPosNative(out Point lpPoint);

        [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
        private static extern bool ScreenToClientNative(nint hWnd, ref Point lpPoint);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoEx lpmi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

        public static nint FindWindowForProcess(int processId)
        {
            nint found = 0;
            long bestArea = 0;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if ((int)pid != processId) return true;
                if (!IsWindowVisible(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; found = hwnd; }
                return true;
            }, 0);
            return found;
        }

        public static bool TryGetWindowRect(nint hwnd, out Rect rect) => GetWindowRect(hwnd, out rect);
        public static bool GetCursorPos(out Point pt) => GetCursorPosNative(out pt);
        public static bool ScreenToClient(nint hwnd, ref Point pt) => ScreenToClientNative(hwnd, ref pt);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MAPVK_VK_TO_VSC = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public nint dwExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static void TapKey(ushort vk)
        {
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) return;
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }

        [StructLayout(LayoutKind.Sequential)] private struct DisplayRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public uint cbSize;
            public DisplayRect rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public uint dmPanningWidth, dmPanningHeight;
        }

        public static int DetectMonitorHz(nint hwnd)
        {
            try
            {
                if (hwnd == 0) return 0;
                var hmon = MonitorFromWindow(hwnd, 2);
                if (hmon == 0) return 0;
                var mi = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfoW(hmon, ref mi)) return 0;
                var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettingsW(mi.szDevice, -1, ref dm)) return 0;
                return dm.dmDisplayFrequency > 1 ? (int)dm.dmDisplayFrequency : 0;
            }
            catch { return 0; }
        }
    }
}
