using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace PenSession.WinUI;

/// <summary>
/// WinUI 3 pointer input session. Attaches to a XAML <see cref="UIElement"/>'s
/// pointer events and produces <see cref="PenPoint"/> records in desktop
/// screen-pixel coordinates — the same format as Wintab sessions.
///
/// <para>Lives in PenSession.WinUI (not PenSession) because it depends on
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

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024; // PointerPoint pressure 0.0–1.0 → scaled to 0–1024

    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[WinUI Pointer] Element={_element.GetType().Name}";

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
        return count;
    }

    public void RefreshMapping() { }
    public void Dispose() => Stop();

    // ── Event handler ────────────────────────────────────────────

    private void OnPointerEvent(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_element);

        // Only handle pen input.
        if (point.PointerDeviceType != PointerDeviceType.Pen)
            return;

        var props = point.Properties;

        // Convert element-relative DIPs to desktop screen pixels.
        var (desktopX, desktopY) = ElementDipsToDesktopPixels(
            point.Position.X, point.Position.Y);

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
            RawX: (int)desktopX,
            RawY: (int)desktopY,
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
            Source: InputApi.WinUiPointer));

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
