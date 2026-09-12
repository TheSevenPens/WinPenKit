using System.Runtime.InteropServices;

namespace WinPenKit;

/// <summary>
/// Puts a window inside its monitor's work area, in device pixels, through Win32.
/// </summary>
/// <remarks>
/// <para>Pen input arrives by absolute screen position, so any part of a window hanging off
/// its monitor - or sitting under the taskbar - receives nothing. The window keeps running and
/// keeps painting, and strokes aimed at that region simply never arrive, which reads as a bug
/// in whatever is being tested rather than as a misplaced window.</para>
/// <para>A window manager produces this on its own. Each launch cascades the window a little
/// further down and to the right, so an application that fits on one run hangs off the bottom
/// a few runs later. <c>L0.window-placement</c> reports it correctly when it happens, which
/// makes that check depend on launch history rather than on the code - the reason to fix the
/// placement rather than loosen the check.</para>
/// <para>Call this once the window exists and again whenever its DPI changes, since a window
/// that fits at one display scale need not fit at another.</para>
/// </remarks>
public static class WindowPlacement
{
    /// <summary>
    /// Moves the window inside its monitor's work area, shrinking it first if it does not fit.
    /// Returns true when something moved or resized.
    /// </summary>
    /// <remarks>
    /// A maximized window already occupies exactly the work area, and its window rect overhangs
    /// by the invisible resize border on purpose, so this leaves one alone.
    /// </remarks>
    public static bool ClampToWorkArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsZoomed(hwnd) || IsIconic(hwnd)) return false;
        if (!GetWindowRect(hwnd, out RECT win)) return false;

        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        RECT wa = mi.rcWork;
        int w = win.Right - win.Left;
        int h = win.Bottom - win.Top;
        int waW = wa.Right - wa.Left;
        int waH = wa.Bottom - wa.Top;

        // Shrink first. Moving a window that is larger than the work area can never bring it
        // inside, so the position alone is not enough.
        int newW = Math.Min(w, waW);
        int newH = Math.Min(h, waH);

        int newX = Math.Clamp(win.Left, wa.Left, Math.Max(wa.Left, wa.Right - newW));
        int newY = Math.Clamp(win.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - newH));

        if (newX == win.Left && newY == win.Top && newW == w && newH == h) return false;

        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (newW == w && newH == h) flags |= SWP_NOSIZE;

        return SetWindowPos(hwnd, IntPtr.Zero, newX, newY, newW, newH, flags);
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
