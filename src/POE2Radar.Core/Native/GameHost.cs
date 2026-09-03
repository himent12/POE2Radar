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

    // ── Input routing. Foreground: real synthesized input (SendInput / XTest) — what the game expects.
    //    Background mode + game NOT foreground: window-addressed messages (PostMessage / XSendEvent) so the
    //    bot keeps playing while the user is alt-tabbed and nothing is typed into the focused app. ──
    private static nint _targetHwnd;
    private static bool _background;

    public static void SetInputTarget(nint hwnd, bool playInBackground)
    {
        _targetHwnd = hwnd;
        _background = playInBackground;
        if (OperatingSystem.IsLinux()) LinuxX11.InputTargetHwnd = hwnd;
    }

    /// <summary>
    /// Linux: route XTest input to a NESTED X server (gamescope / Xephyr) where the game is always focused —
    /// the only background route Wine games reliably accept. <paramref name="name"/>: ":N", "auto" (scan for a
    /// server hosting a Path of Exile window), or null/empty to use the main display.
    /// </summary>
    public static void SetInputDisplay(string? name)
    {
        if (OperatingSystem.IsLinux()) LinuxX11.SetInputDisplay(name);
    }

    /// <summary>A nested input server is connected (Linux).</summary>
    public static bool HasNestedInput => OperatingSystem.IsLinux() && LinuxX11.HasInputDisplay;
    public static string? NestedInputDisplay => OperatingSystem.IsLinux() ? LinuxX11.InputDisplayResolved : null;

    /// <summary>Route to the game window directly via messages (background mode, game not foreground, no
    /// nested server)?</summary>
    private static bool Targeted => _background && _targetHwnd != 0 && !HasNestedInput && GetForegroundWindow() != _targetHwnd;

    /// <summary>True while input is being routed to an unfocused game window.</summary>
    public static bool IsBackgroundActive => Targeted;

    /// <summary>Can mouse buttons be delivered in background mode? Windows: PostMessage bypasses activation.
    /// Linux: only through a nested server; a synthetic ButtonPress on the main display activates the window.</summary>
    public static bool BackgroundSupportsMouse => !OperatingSystem.IsLinux() || HasNestedInput;

    /// <summary>Would this key be dropped right now (mouse button in background mode on Linux)?</summary>
    public static bool WouldDrop(int vk) => Targeted && !BackgroundSupportsMouse && vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;

    public static short GetAsyncKeyState(int vKey)
        => OperatingSystem.IsLinux() ? LinuxX11.GetAsyncKeyState(vKey) : Win32.GetAsyncKeyState(vKey);

    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>Mouse button state polled from the OS (vk 1=LMB 2=RMB 4=MMB) — independent of which window
    /// gets the click event, so it works under fullscreen pointer grabs.</summary>
    public static bool IsMouseButtonDown(int vk)
        => OperatingSystem.IsLinux() ? LinuxX11.IsMouseButtonDown(vk) : (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void TapKey(ushort vk)
    {
        if (Targeted) { SendKeyTargeted(vk, true); SendKeyTargeted(vk, false); return; }
        if (OperatingSystem.IsLinux()) LinuxX11.TapKey(vk);
        else Win32.TapKey(vk);
    }

    /// <summary>Press and HOLD a key/button until <see cref="KeyUp"/> — continuous WASD movement + run.</summary>
    public static void KeyDown(ushort vk)
    {
        if (Targeted) { SendKeyTargeted(vk, true); return; }
        if (OperatingSystem.IsLinux()) LinuxX11.SetKey(vk, true);
        else Win32.SetKey(vk, true);
    }

    public static void KeyUp(ushort vk)
    {
        // Always release on BOTH paths: a key pressed while foreground must not stay down after alt-tab.
        if (_background && _targetHwnd != 0) SendKeyTargeted(vk, false);
        if (OperatingSystem.IsLinux()) LinuxX11.SetKey(vk, false);
        else Win32.SetKey(vk, false);
    }

    private static void SendKeyTargeted(ushort vk, bool down)
    {
        if (OperatingSystem.IsLinux()) LinuxX11.SendKeyToWindow(_targetHwnd, vk, down);
        else Win32.SendKeyToWindow(_targetHwnd, vk, down);
    }

    /// <summary>Undo any input-side state we changed (Linux: keyboard autorepeat). Call on shutdown.</summary>
    public static void RestoreInputState()
    {
        if (OperatingSystem.IsLinux()) LinuxX11.RestoreAutoRepeat();
    }

    public static void SetCursorPos(int x, int y)
    {
        if (Targeted)
        {
            // Never warp the real pointer while the user is in another app — tell the game where "its" cursor is.
            if (!TryGetWindowRect(_targetHwnd, out var r)) return;
            if (OperatingSystem.IsLinux()) LinuxX11.SendPointerToWindow(_targetHwnd, x - r.Left, y - r.Top);
            else Win32.SendPointerToWindow(_targetHwnd, x - r.Left, y - r.Top);
            return;
        }
        if (OperatingSystem.IsLinux()) LinuxX11.SetCursorPos(x, y);
        else Win32.SetCursorPos(x, y);
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

        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
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

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MAPVK_VK_TO_VSC = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public nint dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public nint dwExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static void TapKey(ushort vk)
        {
            if (TryMouseFlags(vk, out var down, out var up, out var data))
            {
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_MOUSE;
                inputs[0].U.mi = new MOUSEINPUT { dwFlags = down, mouseData = data };
                inputs[1].type = INPUT_MOUSE;
                inputs[1].U.mi = new MOUSEINPUT { dwFlags = up, mouseData = data };
                SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                return;
            }
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) return;
            var inputsK = new INPUT[2];
            inputsK[0].type = INPUT_KEYBOARD;
            inputsK[0].U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE };
            inputsK[1].type = INPUT_KEYBOARD;
            inputsK[1].U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP };
            SendInput(2, inputsK, Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")] private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
        private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        private static int _bgX, _bgY;
        private static nint _bgKeys; // MK_* button state for mouse messages

        public static void SendKeyToWindow(nint hwnd, ushort vk, bool down)
        {
            if (hwnd == 0) return;
            var lp = (nint)((_bgY << 16) | (_bgX & 0xFFFF));
            switch (vk)
            {
                case 0x01: _bgKeys = down ? _bgKeys | 0x1 : _bgKeys & ~0x1; PostMessageW(hwnd, down ? WM_LBUTTONDOWN : WM_LBUTTONUP, _bgKeys, lp); return;
                case 0x02: _bgKeys = down ? _bgKeys | 0x2 : _bgKeys & ~0x2; PostMessageW(hwnd, down ? WM_RBUTTONDOWN : WM_RBUTTONUP, _bgKeys, lp); return;
                case 0x04: _bgKeys = down ? _bgKeys | 0x10 : _bgKeys & ~0x10; PostMessageW(hwnd, down ? WM_MBUTTONDOWN : WM_MBUTTONUP, _bgKeys, lp); return;
            }
            var scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            var lParam = (nint)(1 | (scan << 16) | (down ? 0u : 0xC0000000u));
            PostMessageW(hwnd, down ? WM_KEYDOWN : WM_KEYUP, vk, lParam);
        }

        public static void SendPointerToWindow(nint hwnd, int clientX, int clientY)
        {
            if (hwnd == 0) return;
            _bgX = clientX; _bgY = clientY;
            PostMessageW(hwnd, WM_MOUSEMOVE, _bgKeys, (nint)((clientY << 16) | (clientX & 0xFFFF)));
        }

        public static void SetKey(ushort vk, bool down)
        {
            if (TryMouseFlags(vk, out var dflag, out var uflag, out var data))
            {
                var m = new INPUT[1];
                m[0].type = INPUT_MOUSE;
                m[0].U.mi = new MOUSEINPUT { dwFlags = down ? dflag : uflag, mouseData = data };
                SendInput(1, m, Marshal.SizeOf<INPUT>());
                return;
            }
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan == 0) return;
            var k = new INPUT[1];
            k[0].type = INPUT_KEYBOARD;
            k[0].U.ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) };
            SendInput(1, k, Marshal.SizeOf<INPUT>());
        }

        private static bool TryMouseFlags(ushort vk, out uint down, out uint up, out uint data)
        {
            down = up = data = 0;
            switch (vk)
            {
                case 0x01: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; return true;
                case 0x02: down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; return true;
                case 0x04: down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; return true;
                case 0x05: down = MOUSEEVENTF_XDOWN; up = MOUSEEVENTF_XUP; data = 1; return true;
                case 0x06: down = MOUSEEVENTF_XDOWN; up = MOUSEEVENTF_XUP; data = 2; return true;
                default: return false;
            }
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
