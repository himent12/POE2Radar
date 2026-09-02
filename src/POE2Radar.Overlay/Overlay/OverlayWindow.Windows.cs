using System.Runtime.InteropServices;
using POE2Radar.Overlay.Native;

namespace POE2Radar.Overlay;

public sealed partial class OverlayWindow
{
    private nint _hwnd;
    private nint _hInstance;
    private OverlayNative.WndProc? _wndProcDelegate;
    private nint _memDC;
    private nint _dibSection;
    private nint _dibSectionPrev;
    private nint _dibBits;
    private bool _trayAdded;
    private const uint WmTrayCallback = OverlayNative.WM_APP + 1;
    private const uint MenuExitId = 1;

    private void InitWindows()
    {
        _hInstance = OverlayNative.GetModuleHandleW(null);
        _wndProcDelegate = WndProc;
        RegisterWindowClass();
        CreateOverlayHwnd();
        AddTrayIcon();
    }

    private unsafe void RegisterWindowClass()
    {
        var className = "POE2RadarOverlay\0";
        fixed (char* pName = className)
        {
            var wc = new OverlayNative.WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<OverlayNative.WNDCLASSEXW>(),
                style         = OverlayNative.CS_HREDRAW | OverlayNative.CS_VREDRAW,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate!),
                hInstance     = _hInstance,
                lpszClassName = (nint)pName,
            };
            OverlayNative.RegisterClassExW(&wc);
        }
    }

    private void CreateOverlayHwnd()
    {
        var exStyle = OverlayNative.WS_EX_TOPMOST
                    | OverlayNative.WS_EX_TRANSPARENT
                    | OverlayNative.WS_EX_LAYERED
                    | OverlayNative.WS_EX_NOACTIVATE
                    | OverlayNative.WS_EX_TOOLWINDOW;

        _hwnd = OverlayNative.CreateWindowExW(
            exStyle, "POE2RadarOverlay", "POE2RadarOverlay",
            OverlayNative.WS_POPUP | OverlayNative.WS_VISIBLE,
            0, 0, 800, 600, 0, 0, _hInstance, 0);

        if (_hwnd == 0) throw new InvalidOperationException("CreateWindowExW failed");
        OverlayNative.ShowWindow(_hwnd, OverlayNative.SW_SHOW);
    }

    private void AddTrayIcon()
    {
        var nid = new OverlayNative.NOTIFYICONDATAW
        {
            cbSize           = (uint)Marshal.SizeOf<OverlayNative.NOTIFYICONDATAW>(),
            hWnd             = _hwnd,
            uID              = 1,
            uFlags           = OverlayNative.NIF_MESSAGE | OverlayNative.NIF_ICON | OverlayNative.NIF_TIP,
            uCallbackMessage = WmTrayCallback,
            hIcon            = OverlayNative.LoadIconW(0, OverlayNative.IDI_APPLICATION),
            szTip            = "POE2Radar — right-click to Exit",
            szInfo           = "",
            szInfoTitle      = "",
        };
        _trayAdded = OverlayNative.Shell_NotifyIconW(OverlayNative.NIM_ADD, ref nid);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        var nid = new OverlayNative.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<OverlayNative.NOTIFYICONDATAW>(),
            hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "",
        };
        OverlayNative.Shell_NotifyIconW(OverlayNative.NIM_DELETE, ref nid);
        _trayAdded = false;
    }

    private void ShowTrayMenu()
    {
        var menu = OverlayNative.CreatePopupMenu();
        if (menu == 0) return;
        OverlayNative.AppendMenuW(menu, OverlayNative.MF_STRING, MenuExitId, "Exit POE2Radar");
        OverlayNative.GetCursorPos(out var pt);
        OverlayNative.SetForegroundWindow(_hwnd);
        var cmd = OverlayNative.TrackPopupMenu(menu,
            OverlayNative.TPM_RIGHTBUTTON | OverlayNative.TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, 0);
        OverlayNative.DestroyMenu(menu);
        if (cmd == (int)MenuExitId) OverlayNative.PostQuitMessage(0);
    }

    private void ResizeWindows(int width, int height)
    {
        FreeBackingBitmap();
        var screenDC = OverlayNative.GetDC(0);
        try
        {
            _memDC = OverlayNative.CreateCompatibleDC(screenDC);
            var bmi = new OverlayNative.BITMAPINFO
            {
                bmiHeader = new OverlayNative.BITMAPINFOHEADER
                {
                    biSize        = (uint)Marshal.SizeOf<OverlayNative.BITMAPINFOHEADER>(),
                    biWidth       = width,
                    biHeight      = -height,
                    biPlanes      = 1,
                    biBitCount    = 32,
                    biCompression = OverlayNative.BI_RGB,
                },
            };
            _dibSection = OverlayNative.CreateDIBSection(_memDC, ref bmi, OverlayNative.DIB_RGB_COLORS, out _dibBits, 0, 0);
            if (_dibSection == 0) throw new InvalidOperationException("CreateDIBSection failed");
            _dibSectionPrev = OverlayNative.SelectObject(_memDC, _dibSection);
        }
        finally { OverlayNative.ReleaseDC(0, screenDC); }
    }

    private void FreeBackingBitmap()
    {
        if (_memDC != 0 && _dibSectionPrev != 0)
        {
            OverlayNative.SelectObject(_memDC, _dibSectionPrev);
            _dibSectionPrev = 0;
        }
        if (_dibSection != 0) { OverlayNative.DeleteObject(_dibSection); _dibSection = 0; _dibBits = 0; }
        if (_memDC != 0) { OverlayNative.DeleteDC(_memDC); _memDC = 0; }
    }

    private void PresentWindows()
    {
        if (_hwnd == 0 || _memDC == 0 || PixelBuffer == 0 || _dibBits == 0) return;
        unsafe
        {
            var n = (nuint)(PixelRowBytes * Height);
            Buffer.MemoryCopy((void*)PixelBuffer, (void*)_dibBits, (long)n, (long)n);
        }
        OverlayNative.GdiFlush();
        var screenDC = OverlayNative.GetDC(0);
        try
        {
            var dstPos = new OverlayNative.POINT { X = OriginX, Y = OriginY };
            var size   = new OverlayNative.SIZE  { cx = Width,  cy = Height };
            var srcPos = new OverlayNative.POINT { X = 0,       Y = 0 };
            var blend  = new OverlayNative.BLENDFUNCTION
            {
                BlendOp             = OverlayNative.AC_SRC_OVER,
                BlendFlags          = 0,
                SourceConstantAlpha = 255,
                AlphaFormat         = OverlayNative.AC_SRC_ALPHA,
            };
            OverlayNative.UpdateLayeredWindow(_hwnd, screenDC, ref dstPos, ref size, _memDC, ref srcPos, 0, ref blend, OverlayNative.ULW_ALPHA);
        }
        finally { OverlayNative.ReleaseDC(0, screenDC); }
    }

    private void SetClickThroughWindows(bool value)
    {
        if (_hwnd == 0) return;
        var ex = (uint)OverlayNative.GetWindowLongPtrW(_hwnd, OverlayNative.GWL_EXSTYLE);
        ex = value ? ex | OverlayNative.WS_EX_TRANSPARENT : ex & ~OverlayNative.WS_EX_TRANSPARENT;
        OverlayNative.SetWindowLongPtrW(_hwnd, OverlayNative.GWL_EXSTYLE, (nint)ex);
    }

    private bool PumpWindows()
    {
        while (OverlayNative.PeekMessageW(out var msg, 0, 0, 0, OverlayNative.PM_REMOVE))
        {
            if (msg.message == OverlayNative.WM_QUIT) return false;
            OverlayNative.TranslateMessage(ref msg);
            OverlayNative.DispatchMessageW(ref msg);
        }
        return true;
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmTrayCallback)
        {
            var ev = (uint)(lParam & 0xFFFF);
            if (ev is OverlayNative.WM_RBUTTONUP or OverlayNative.WM_LBUTTONUP) ShowTrayMenu();
            return 0;
        }
        if (msg == OverlayNative.WM_LBUTTONDOWN)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            OnClientClick?.Invoke(x, y);
            return 0;
        }
        if (msg == OverlayNative.WM_DESTROY)
        {
            OverlayNative.PostQuitMessage(0);
            return 0;
        }
        return OverlayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void DisposeWindows()
    {
        RemoveTrayIcon();
        FreeBackingBitmap();
        if (_hwnd != 0) { OverlayNative.DestroyWindow(_hwnd); _hwnd = 0; }
    }
}
