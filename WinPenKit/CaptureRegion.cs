using System.Runtime.InteropServices;

namespace WinPenKit;

/// <summary>
/// A screen-space region that constrains which pen points a session reports.
/// Points whose desktop (physical screen-pixel) coordinates fall outside the
/// region are dropped before they reach <see cref="IPenSession.DrainPoints()"/>.
///
/// <para>This is the mechanism WinPenKit uses to give every backend a
/// consistent <i>spatial scope</i>. Wintab is desktop-global by nature,
/// WM_POINTER is window-scoped, and framework pointer sessions are
/// control-scoped; constraining all of them to the same region makes a
/// consumer's experience identical regardless of which input API is active.</para>
///
/// <para><b>Threading:</b> <see cref="Contains"/> may be called from a
/// background capture thread (Wintab sessions evaluate it on their message-pump
/// thread). Implementations must be thread-safe and must not touch UI objects
/// directly — cache any UI-derived bounds on the UI thread and read the cached
/// value here. See <c>ControlCaptureRegion</c> in WinPenKit.Avalonia for the
/// canonical pattern.</para>
/// </summary>
public interface IPenCaptureRegion
{
    /// <summary>
    /// Returns true if the given desktop point (physical screen pixels) lies
    /// inside the region and the pen data should be reported.
    /// </summary>
    bool Contains(double desktopX, double desktopY);
}

/// <summary>
/// Built-in <see cref="IPenCaptureRegion"/> implementations.
/// </summary>
public static class PenCaptureRegion
{
    /// <summary>
    /// A region that accepts every point — desktop-wide capture. Only backends
    /// advertising <see cref="PenCapabilities.GlobalCapture"/> (Wintab) actually
    /// deliver points outside the app window; other backends remain limited to
    /// their window or control by the operating system regardless.
    /// </summary>
    public static IPenCaptureRegion Unbounded { get; } = new UnboundedRegion();

    /// <summary>
    /// A region matching a window's current bounds (via <c>GetWindowRect</c>),
    /// re-evaluated on every point so it tracks the window as it moves or
    /// resizes. A zero handle accepts every point (no filtering).
    /// </summary>
    public static IPenCaptureRegion Window(IntPtr hwnd) => new WindowRegion(hwnd);

    /// <summary>A fixed screen-pixel rectangle.</summary>
    public static IPenCaptureRegion Rect(double x, double y, double width, double height)
        => new RectRegion(x, y, width, height);

    private sealed class UnboundedRegion : IPenCaptureRegion
    {
        public bool Contains(double desktopX, double desktopY) => true;
    }

    private sealed class RectRegion(double x, double y, double width, double height) : IPenCaptureRegion
    {
        public bool Contains(double desktopX, double desktopY)
            => desktopX >= x && desktopX < x + width
            && desktopY >= y && desktopY < y + height;
    }

    private sealed class WindowRegion(IntPtr hwnd) : IPenCaptureRegion
    {
        public bool Contains(double desktopX, double desktopY)
        {
            if (hwnd == IntPtr.Zero) return true;          // no window known → don't filter
            if (!GetWindowRect(hwnd, out var r)) return true;
            return desktopX >= r.left && desktopX < r.right
                && desktopY >= r.top && desktopY < r.bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
    }
}
