using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Native;

public static partial class LinuxX11
{
    private const string X11 = "libX11.so.6";

    private const string Xtst = "libXtst.so.6";

    private const string Xfixes = "libXfixes.so.3";

    public const nint XA_ATOM = 4;

    public const nint XA_WINDOW = 33;

    public const nint XA_CARDINAL = 6;

    public const nint XA_STRING = 31;

    public const int XK_F1 = 0xFFBE;

    [StructLayout(LayoutKind.Sequential)]
    public struct XVisualInfo
    {
        public nint Visual;
        public nuint VisualId;
        public int Screen;
        public int Depth;
        public int Class;
        public nuint RedMask, GreenMask, BlueMask;
        public int ColormapSize;
        public int BitsPerRgb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public nuint BackgroundPixmap;
        public nuint BackgroundPixel;
        public nuint BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint EventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap;
        public nuint Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    // XEvent is a 24-long union (192 bytes on x86_64). Undersizing this makes XNextEvent smash the stack.
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XEvent
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint Root;
        public nuint Subwindow;
        public nuint Time;
        public int X, Y, XRoot, YRoot;
        public uint State, Button;
        public int SameScreen;
    }

    [LibraryImport(X11, EntryPoint = "XInitThreads")]
    public static partial int XInitThreads();

    [LibraryImport(X11, EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XOpenDisplay(string? name);

    [LibraryImport(X11, EntryPoint = "XQueryTree")]
    public static partial int XQueryTree(nint display, nuint w, out nuint root, out nuint parent, out nint children, out uint nChildren);

    [LibraryImport(X11, EntryPoint = "XCloseDisplay")]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(X11, EntryPoint = "XDefaultScreen")]
    public static partial int XDefaultScreen(nint display);

    [LibraryImport(X11, EntryPoint = "XRootWindow")]
    public static partial nuint XRootWindow(nint display, int screen);

    [LibraryImport(X11, EntryPoint = "XDefaultRootWindow")]
    public static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(X11, EntryPoint = "XMatchVisualInfo")]
    public static partial int XMatchVisualInfo(nint display, int screen, int depth, int cls, out XVisualInfo vinfo);

    [LibraryImport(X11, EntryPoint = "XCreateColormap")]
    public static partial nuint XCreateColormap(nint display, nuint w, nint visual, int alloc);

    [LibraryImport(X11, EntryPoint = "XCreateWindow")]
    public static partial nuint XCreateWindow(
        nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint cls, nint visual, nuint valuemask, ref XSetWindowAttributes attrs);

    [LibraryImport(X11, EntryPoint = "XDestroyWindow")]
    public static partial int XDestroyWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XMapRaised")]
    public static partial int XMapRaised(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XUnmapWindow")]
    public static partial int XUnmapWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XMoveResizeWindow")]
    public static partial int XMoveResizeWindow(nint display, nuint w, int x, int y, uint width, uint height);

    [LibraryImport(X11, EntryPoint = "XRaiseWindow")]
    public static partial int XRaiseWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XClearWindow")]
    public static partial int XClearWindow(nint display, nuint w);

    [LibraryImport(X11, EntryPoint = "XFlush")]
    public static partial int XFlush(nint display);

    [LibraryImport(X11, EntryPoint = "XSync")]
    public static partial int XSync(nint display, int discard);

    [LibraryImport(X11, EntryPoint = "XPending")]
    public static partial int XPending(nint display);

    [LibraryImport(X11, EntryPoint = "XNextEvent")]
    public static partial int XNextEvent(nint display, out XEvent evt);

    [LibraryImport(X11, EntryPoint = "XSelectInput")]
    public static partial int XSelectInput(nint display, nuint w, nint mask);

    [LibraryImport(X11, EntryPoint = "XCreateGC")]
    public static partial nint XCreateGC(nint display, nuint d, nuint valuemask, nint values);

    [LibraryImport(X11, EntryPoint = "XFreeGC")]
    public static partial int XFreeGC(nint display, nint gc);

    [LibraryImport(X11, EntryPoint = "XPutImage")]
    public static partial int XPutImage(nint display, nuint d, nint gc, nint image, int srcX, int srcY, int dstX, int dstY, uint w, uint h);

    [LibraryImport(X11, EntryPoint = "XCreateImage")]
    public static partial nint XCreateImage(nint display, nint visual, uint depth, int format, int offset, nint data, uint width, uint height, int bitmapPad, int bytesPerLine);

    [LibraryImport(X11, EntryPoint = "XDestroyImage")]
    public static partial int XDestroyImage(nint image);

    [LibraryImport(X11, EntryPoint = "XStoreName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int XStoreName(nint display, nuint w, string name);

    [LibraryImport(X11, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(X11, EntryPoint = "XChangeProperty")]
    public static partial int XChangeProperty(nint display, nuint w, nint property, nint type, int format, int mode, nint data, int nElements);

    [LibraryImport(X11, EntryPoint = "XGetWindowProperty")]
    public static partial int XGetWindowProperty(
        nint display, nuint w, nint property, nint offset, nint length, int delete, nint reqType,
        out nint actualType, out int actualFormat, out nuint nItems, out nuint bytesAfter, out nint prop);

    [LibraryImport(X11, EntryPoint = "XFree")]
    public static partial int XFree(nint data);

    [LibraryImport(X11, EntryPoint = "XGetWindowAttributes")]
    public static partial int XGetWindowAttributes(nint display, nuint w, out XWindowAttributes attrs);

    [LibraryImport(X11, EntryPoint = "XTranslateCoordinates")]
    public static partial int XTranslateCoordinates(nint display, nuint src, nuint dest, int srcX, int srcY, out int destX, out int destY, out nuint child);

    [LibraryImport(X11, EntryPoint = "XQueryPointer")]
    public static partial int XQueryPointer(
        nint display, nuint w, out nuint root, out nuint child, out int rootX, out int rootY,
        out int winX, out int winY, out uint mask);

    [LibraryImport(X11, EntryPoint = "XSendEvent")]
    public static partial int XSendEvent(nint display, nuint window, int propagate, nint eventMask, ref XEvent evt);

    [LibraryImport(X11, EntryPoint = "XSetInputFocus")]
    public static partial int XSetInputFocus(nint display, nuint focus, int revertTo, nuint time);

    [LibraryImport(X11, EntryPoint = "XSendEvent")]
    public static partial int XSendEventClient(nint display, nuint window, int propagate, nint eventMask, ref XClientMessageEvent evt);

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XClientMessageEvent
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public nint L0, L1, L2, L3, L4;
    }

    [LibraryImport(X11, EntryPoint = "XAutoRepeatOff")]
    public static partial int XAutoRepeatOff(nint display);

    [LibraryImport(X11, EntryPoint = "XAutoRepeatOn")]
    public static partial int XAutoRepeatOn(nint display);

    [LibraryImport(X11, EntryPoint = "XGrabPointer")]
    public static partial int XGrabPointer(nint display, nuint grabWindow, int ownerEvents, uint eventMask, int pointerMode, int keyboardMode, nuint confineTo, nuint cursor, nuint time);

    [LibraryImport(X11, EntryPoint = "XUngrabPointer")]
    public static partial int XUngrabPointer(nint display, nuint time);

    [LibraryImport(X11, EntryPoint = "XQueryKeymap")]
    public static unsafe partial int XQueryKeymap(nint display, byte* keys);

    [LibraryImport(X11, EntryPoint = "XKeysymToKeycode")]
    public static partial byte XKeysymToKeycode(nint display, nuint keysym);

    [LibraryImport(X11, EntryPoint = "XStringToKeysym", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XStringToKeysym(string s);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeKeyEvent")]
    public static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);

    [LibraryImport(Xtst, EntryPoint = "XTestFakeButtonEvent")]
    public static partial int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);

    [LibraryImport(X11, EntryPoint = "XWarpPointer")]
    public static partial int XWarpPointer(nint display, nuint srcW, nuint destW,
        int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);

    [LibraryImport(Xfixes, EntryPoint = "XFixesCreateRegion")]
    public static partial nint XFixesCreateRegion(nint display, nint rectangles, int nRectangles);

    [LibraryImport(Xfixes, EntryPoint = "XFixesDestroyRegion")]
    public static partial int XFixesDestroyRegion(nint display, nint region);

    [LibraryImport(Xfixes, EntryPoint = "XFixesSetWindowShapeRegion")]
    public static partial int XFixesSetWindowShapeRegion(nint display, nuint window, int shapeKind, int xOff, int yOff, nint region);

    [LibraryImport(X11, EntryPoint = "XGetInputFocus")]
    public static partial int XGetInputFocus(nint display, out nuint focus, out int revert);
}
