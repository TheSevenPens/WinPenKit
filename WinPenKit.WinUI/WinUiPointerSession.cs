using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace WinPenKit.WinUI;

/// <summary>
/// WinUI 3 pointer input session. Attaches to a XAML <see cref="UIElement"/>'s
/// pointer events and produces <see cref="PenPoint"/> records in desktop
/// screen-pixel coordinates — the same format as Wintab sessions.
///
/// <para>Lives in WinPenKit.WinUI (not WinPenKit) because it depends on
/// WinUI 3 XAML types. Implements <see cref="IPenSession"/> so apps can
/// swap between Wintab and WinUI pointer input at runtime.</para>
/// </summary>
public sealed class WinUiPointerSession : IPenSession
{
    private readonly UIElement _element;
    private readonly IntPtr _hwnd;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;

    // ── Win32 P/Invoke for DIP → screen pixel conversion ─────────

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType,
        out uint dpiX, out uint dpiY);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // ── IPenSession ──────────────────────────────────────────────

    public InputApi Api => InputApi.WinUiPointer;

    /// <summary>
    /// This framework exposes no device-native coordinate, so the raw fields carry nothing
    /// and are written as zero. Reporting DesktopX truncated would look like a second
    /// measurement and be the same one with its fraction removed.
    /// </summary>
    public PenConventions Conventions => new(
        PenRawUnits.None,
        PenButtonEncoding.PointerFlags,
        PenCursorNumbering.Normalised,
        PenTimestampSource.SystemTicks);

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024; // PointerPoint pressure 0.0–1.0 → scaled to 0–1024

    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[WinUI Pointer] Element={_element.GetType().Name}";
    public IPenCaptureRegion? CaptureRegion { get; set; }

    /// <summary>
    /// Creates a WinUI pointer session targeting a specific XAML element.
    /// </summary>
    /// <param name="element">The UIElement to attach pointer events to (typically the canvas).</param>
    /// <param name="hwnd">The app window handle, needed for DIP → screen pixel conversion.</param>
    public WinUiPointerSession(UIElement element, IntPtr hwnd)
    {
        _element = element;
        _hwnd = hwnd;
    }

    public string? Start(IntPtr appWindowHandle = default)
    {
        _element.PointerMoved += OnPointerEvent;
        _element.PointerPressed += OnPointerEvent;
        _element.PointerReleased += OnPointerEvent;
        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        _element.PointerMoved -= OnPointerEvent;
        _element.PointerPressed -= OnPointerEvent;
        _element.PointerReleased -= OnPointerEvent;
        IsRunning = false;
    }

    public PenPoint[] DrainPoints()
    {
        _hasNewData = false;
        var list = new List<PenPoint>();
        while (_points.TryDequeue(out var pt))
            list.Add(pt);
        return [.. list];
    }

    public int DrainPoints(Span<PenPoint> buffer)
    {
        _hasNewData = false;
        int count = 0;
        while (count < buffer.Length && _points.TryDequeue(out var pt))
            buffer[count++] = pt;

        // A buffer smaller than the queue leaves points behind. Clearing the flag and
        // stopping there told a caller that polls HasNewData the queue was empty when it was
        // not, and if the pen had lifted nothing would set it again: those points waited for
        // a drain that happened for some other reason.
        //
        // Only ever set true here, never false. The false above still happens before the
        // drain, so a point enqueued by the pen thread mid-drain sets the flag itself and
        // this cannot overwrite it.
        if (!_points.IsEmpty) _hasNewData = true;

        return count;
    }

    public void RefreshMapping() { }
    public void Dispose() => Stop();

    // ── Event handler ────────────────────────────────────────────

    private void OnPointerEvent(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Every point the framework coalesced into this event, not just the last one.
        //
        // GetCurrentPoint alone returns the most recent sample and silently discards whatever
        // WinUI merged behind it, which on a fast stroke is most of the stroke. The three
        // WM_POINTER backends already recover theirs through GetPointerPenInfoHistory, and
        // WPF gets the whole batch from GetStylusPoints, so this was the odd one out.
        //
        // Replayed in reverse, because this collection is newest first.
        //
        // Microsoft documents the opposite -- "the last item in the collection is equivalent to
        // the PointerPoint object returned by GetCurrentPoint" -- and that is wrong here.
        // Measured on WinUI 3 with an injected stroke travelling in increasing X: a 40-point
        // batch ran 353.8 down to 295.1, and GetCurrentPoint returned 353.8, which is
        // points[0]. So index 0 is the newest sample, the same order
        // GetPointerPenInfoHistory uses and the opposite of Avalonia's.
        //
        // Taking the documentation at its word would reverse every batch. On a fast stroke
        // that does not look like a bug; it looks like jitter, because each batch is drawn
        // backwards inside a path that is still going the right way overall.
        var points = e.GetIntermediatePoints(_element);
        if (points is null || points.Count == 0)
        {
            EnqueuePoint(e.GetCurrentPoint(_element));
            return;
        }

        for (int i = points.Count - 1; i >= 0; i--)
            EnqueuePoint(points[i]);
    }

    /// <summary>
    /// Converts one <see cref="PointerPoint"/> and queues it.
    /// </summary>
    /// <remarks>
    /// Split out of the handler so that the per-point work is written once and the loop above
    /// cannot drift from the single-point path beside it.
    /// </remarks>
    private void EnqueuePoint(Microsoft.UI.Input.PointerPoint point)
    {
        // Only handle pen input.
        if (point.PointerDeviceType != PointerDeviceType.Pen)
            return;

        var props = point.Properties;

        // Convert element-relative DIPs to desktop screen pixels.
        var (desktopX, desktopY) = ElementDipsToDesktopPixels(
            point.Position.X, point.Position.Y);

        // Spatial scope: drop points outside an explicit capture region.
        if (CaptureRegion is { } region && !region.Contains(desktopX, desktopY))
            return;

        // Pressure: PointerPoint gives 0.0–1.0, scale to 0–1024.
        uint pressure = (uint)(props.Pressure * 1024f);

        // Tilt: XTilt/YTilt in degrees.
        double tiltX = props.XTilt;
        double tiltY = props.YTilt;

        // Convert to spherical.
        TiltToSpherical(tiltX, tiltY, out double azimuth, out double altitude);

        // Twist in degrees.
        double twist = props.Twist;

        // Buttons.
        uint buttons = 0;
        if (props.IsBarrelButtonPressed) buttons |= 0x0001;
        if (props.IsEraser) buttons |= 0x0002;

        // Cursor type.
        uint cursor = props.IsEraser ? PenCursorType.Eraser : PenCursorType.PenTip;

        _points.Enqueue(new PenPoint(
            DesktopX: desktopX,
            DesktopY: desktopY,
            // Zero, with Conventions.RawUnits reporting None. This used to be
            // (int)desktopX, which reads as a device-native value and is DesktopX with its
            // fraction dropped.
            RawX: 0,
            RawY: 0,
            Pressure: pressure,
            Azimuth: azimuth,
            Altitude: altitude,
            Twist: twist,
            TiltX: tiltX,
            TiltY: tiltY,
            Z: 0,
            Status: 0,
            Buttons: buttons,
            Cursor: cursor,
            Source: InputApi.WinUiPointer,
            // Already microseconds, so no conversion -- and the unit is earned. Drawn on by
            // hand: 1878 points, 1878 distinct timestamps, gaps whose greatest common divisor
            // is 1us. Under synthetic injection every reading ended in the same sub-millisecond
            // remainder, which reads as a millisecond clock with a fixed offset; the offset
            // belonged to the injector.
            //
            // Not anchored the way the millisecond backends are. Anchoring needs the source's
            // width, and whether this microsecond value comes from a 32-bit millisecond clock
            // underneath is NOT established: Avalonia's and Qt's do, and this one was not
            // traced. If it does, it wraps after about 49.7 days like theirs. Tracked rather
            // than guessed, because anchoring a clock that is genuinely 64-bit and on another
            // epoch would corrupt every reading rather than fix a rare one.
            TimestampMicroseconds: (long)point.Timestamp));

        _hasNewData = true;
    }

    // ── Coordinate conversion ────────────────────────────────────

    private (double x, double y) ElementDipsToDesktopPixels(double dipX, double dipY)
    {
        var transform = _element.TransformToVisual(null);
        var elementOrigin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

        double windowDipX = dipX + elementOrigin.X;
        double windowDipY = dipY + elementOrigin.Y;

        var clientOrigin = new POINT { X = 0, Y = 0 };
        uint dpiX = 96, dpiY = 96;
        var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            ClientToScreen(_hwnd, ref clientOrigin);
            var hMon = MonitorFromWindow(_hwnd, 2);
            if (hMon != IntPtr.Zero)
                GetDpiForMonitor(hMon, 0, out dpiX, out dpiY);
        }
        finally
        {
            SetThreadDpiAwarenessContext(oldCtx);
        }

        return (
            windowDipX * (dpiX / 96.0) + clientOrigin.X,
            windowDipY * (dpiY / 96.0) + clientOrigin.Y);
    }

    // ── Tilt conversion ──────────────────────────────────────────

    private static void TiltToSpherical(double tiltX, double tiltY,
        out double azimuth, out double altitude)
    {
        double mag = Math.Sqrt(tiltX * tiltX + tiltY * tiltY);

        altitude = Math.Clamp(90.0 - mag, 0.0, 90.0);

        if (mag > 0.5)
        {
            double rad = Math.Atan2(-tiltX, tiltY);
            double deg = rad * 180.0 / Math.PI;
            azimuth = ((deg % 360.0) + 360.0) % 360.0;
        }
        else
        {
            azimuth = 0.0;
        }
    }
}
