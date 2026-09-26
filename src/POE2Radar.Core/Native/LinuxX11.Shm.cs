using System.Runtime.InteropServices;

namespace POE2Radar.Core.Native;

/// <summary>
/// MIT-SHM (X shared-memory images) for the overlay's per-frame present. A plain <c>XPutImage</c> pushes
/// the whole frame (8 MB at 1080p) through the X socket every frame; with MIT-SHM the pixels live in a
/// SysV shared segment the X server reads directly, so a present is one small request. Anything that goes
/// wrong (extension missing, remote display, a sandbox with its own IPC namespace, attach rejected) makes
/// <see cref="TryCreateShmImage"/> return false and the caller keeps using <c>XPutImage</c>.
/// </summary>
public static partial class LinuxX11
{
    private const string LibC = "libc";

    /// <summary>XShmSegmentInfo (x86_64): ShmSeg shmseg; int shmid; char* shmaddr; Bool readOnly.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XShmSegmentInfo
    {
        public nuint ShmSeg;
        public int ShmId;
        public nint ShmAddr;
        public int ReadOnly;
    }

    [LibraryImport(Xext, EntryPoint = "XShmQueryExtension")]
    private static partial int XShmQueryExtension(nint display);

    [LibraryImport(Xext, EntryPoint = "XShmCreateImage")]
    private static unsafe partial nint XShmCreateImage(nint display, nint visual, uint depth, int format, nint data,
        XShmSegmentInfo* shminfo, uint width, uint height);

    [LibraryImport(Xext, EntryPoint = "XShmAttach")]
    private static unsafe partial int XShmAttach(nint display, XShmSegmentInfo* shminfo);

    [LibraryImport(Xext, EntryPoint = "XShmDetach")]
    private static unsafe partial int XShmDetach(nint display, XShmSegmentInfo* shminfo);

    [LibraryImport(Xext, EntryPoint = "XShmPutImage")]
    private static partial int XShmPutImage(nint display, nuint d, nint gc, nint image, int srcX, int srcY,
        int dstX, int dstY, uint w, uint h, int sendEvent);

    [LibraryImport(X11, EntryPoint = "XSetErrorHandler")]
    private static unsafe partial nint XSetErrorHandler(nint handler);

    [LibraryImport(LibC, EntryPoint = "shmget", SetLastError = true)]
    private static partial int shmget(int key, nuint size, int shmflg);

    [LibraryImport(LibC, EntryPoint = "shmat", SetLastError = true)]
    private static partial nint shmat(int shmid, nint shmaddr, int shmflg);

    [LibraryImport(LibC, EntryPoint = "shmdt")]
    private static partial int shmdt(nint shmaddr);

    [LibraryImport(LibC, EntryPoint = "shmctl")]
    private static partial int shmctl(int shmid, int cmd, nint buf);

    private const int IPC_PRIVATE = 0, IPC_CREAT = 0x200, IPC_RMID = 0, IPC_STAT = 2;

    /// <summary>shm_nattch of a segment (glibc x86_64 struct shmid_ds: shm_nattch @ +88), or -1.</summary>
    private static unsafe long AttachCount(int shmid)
    {
        var buf = stackalloc byte[256];
        if (shmctl(shmid, IPC_STAT, (nint)buf) != 0) return -1;
        return *(long*)(buf + 88);
    }

    // XImage field offsets (x86_64): data @16, bytes_per_line @44.
    private const int XImageDataOffset = 16, XImageBytesPerLineOffset = 44;

    private static volatile bool _shmProbeFailed;

    [UnmanagedCallersOnly]
    private static int ShmProbeErrorHandler(nint display, nint errorEvent)
    {
        _shmProbeFailed = true;
        return 0;
    }

    /// <summary>A live MIT-SHM image: <see cref="Image"/> (XImage*), the shared pixel memory
    /// <see cref="Pixels"/> (premultiplied BGRA, <see cref="RowBytes"/> stride) and the segment info the
    /// server attached. Free with <see cref="DestroyShmImage"/>.</summary>
    public readonly record struct ShmImage(nint Image, nint Pixels, int RowBytes, nint SegmentInfo);

    /// <summary>Create a width×height ZPixmap MIT-SHM image for <paramref name="visual"/>/<paramref name="depth"/>
    /// and attach it to the server. False (with everything released) when MIT-SHM is unavailable or the attach
    /// is refused. Set POE2RADAR_NO_XSHM=1 to force the plain XPutImage path.</summary>
    public static unsafe bool TryCreateShmImage(nint display, nint visual, int depth, int width, int height, out ShmImage img)
    {
        img = default;
        if (display == 0 || width <= 0 || height <= 0) return false;
        if (Environment.GetEnvironmentVariable("POE2RADAR_NO_XSHM") is { Length: > 0 } off && off != "0") return false;

        XShmSegmentInfo* info = null;
        nint ximage = 0, addr = -1;
        var shmid = -1;
        try
        {
            if (XShmQueryExtension(display) == 0) return false;
            info = (XShmSegmentInfo*)NativeMemory.AllocZeroed((nuint)sizeof(XShmSegmentInfo));
            ximage = XShmCreateImage(display, visual, (uint)depth, ZPixmap, 0, info, (uint)width, (uint)height);
            if (ximage == 0) return false;
            var rowBytes = Marshal.ReadInt32(ximage, XImageBytesPerLineOffset);
            if (rowBytes < width * 4) return false;

            shmid = shmget(IPC_PRIVATE, (nuint)((long)rowBytes * height), IPC_CREAT | 0x180 /* 0600 */);
            if (shmid < 0) return false;
            addr = shmat(shmid, 0, 0);
            if (addr == -1) return false;
            info->ShmId = shmid;
            info->ShmAddr = addr;
            info->ReadOnly = 0;
            Marshal.WriteIntPtr(ximage, XImageDataOffset, addr);

            // A refused attach is reported asynchronously as an X error, whose default handler EXITS the
            // process — so trap errors around attach + sync, then restore the previous handler.
            bool ok;
            lock (Gate)
            {
                _shmProbeFailed = false;
                var prev = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&ShmProbeErrorHandler);
                var attached = XShmAttach(display, info) != 0;
                XSync(display, 0);
                XSetErrorHandler(prev);
                ok = attached && !_shmProbeFailed;
            }
            // The server must have attached OUR segment: it attaches by id in ITS IPC namespace, so from a
            // different namespace (sandbox) the same id can name some other segment and the attach "succeeds".
            // Our segment's attach count (us + server = 2) tells the two apart.
            if (ok && AttachCount(shmid) < 2)
            {
                ok = false;
                XShmDetach(display, info);
                XSync(display, 0);
            }
            // Mark for removal now: the segment lives until both we and the server detach (no leak on crash).
            shmctl(shmid, IPC_RMID, 0);
            if (!ok) return false;

            img = new ShmImage(ximage, addr, rowBytes, (nint)info);
            ximage = 0; addr = -1; info = null; shmid = -1;   // ownership transferred
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (ximage != 0) { Marshal.WriteIntPtr(ximage, XImageDataOffset, 0); XDestroyImage(ximage); }
            if (addr != -1) shmdt(addr);
            if (shmid >= 0) shmctl(shmid, IPC_RMID, 0);
            if (info != null) NativeMemory.Free(info);
        }
    }

    /// <summary>Queue a present of the image. The server reads the shared pixels when it processes the request,
    /// so the caller must <c>XSync</c> before drawing into them again.</summary>
    public static void PutShmImage(nint display, nuint window, nint gc, in ShmImage img, int width, int height)
        => XShmPutImage(display, window, gc, img.Image, 0, 0, 0, 0, (uint)width, (uint)height, 0);

    /// <summary>Detach from the server and release the image + shared memory. Safe on a default value.</summary>
    public static unsafe void DestroyShmImage(nint display, ShmImage img)
    {
        if (img.Image == 0) return;
        var info = (XShmSegmentInfo*)img.SegmentInfo;
        if (display != 0 && info != null)
        {
            XShmDetach(display, info);
            XSync(display, 0);
        }
        Marshal.WriteIntPtr(img.Image, XImageDataOffset, 0);   // XDestroyImage would free() the shm address
        XDestroyImage(img.Image);
        if (img.Pixels != 0) shmdt(img.Pixels);
        if (info != null) NativeMemory.Free(info);
    }
}
