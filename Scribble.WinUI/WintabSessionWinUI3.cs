using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using PenSession;
using PenSession.WinUI;

namespace Scribble.WinUI;

/// <summary>
/// Provides the canvas position and size for desktop → canvas DIP conversion.
/// Implemented by the host (MainWindow) and updated on the UI thread;
/// read during <see cref="PenSessionWinUI3.DrainSegments"/>.
/// </summary>
public interface ICanvasInfo
{
    double Width { get; }
    double Height { get; }
    Point PositionInWindow { get; }
}

/// <summary>
/// A canvas-ready stroke segment in canvas-local DIPs.
/// Produced by the brush logic in <see cref="PenSessionWinUI3.DrainSegments"/>.
/// </summary>
public readonly record struct StrokeSegment(
    Point From,
    Point To,
    float Width);

/// <summary>
/// Snapshot of the latest pen telemetry for display in the UI.
/// </summary>
public readonly record struct PenTelemetry(
    PenPoint Point,
    Point ScreenPoint,
    Point AppPoint,
    Point CanvasPoint,
    int MaxPressure,
    InputApi Api);

/// <summary>
/// WinUI 3 wrapper around <see cref="IPenSession"/>. Converts
/// <see cref="PenPoint"/> desktop coordinates to canvas-local DIPs
/// and produces <see cref="StrokeSegment"/> records for the brush engine.
///
/// <para>All input API management, packet handling, coordinate mapping,
/// and tilt conversion lives in the PenSession library. This class only handles:</para>
/// <list type="bullet">
///   <item>Desktop → canvas DIP conversion (WinUI 3 specific: ClientToScreen + DPI)</item>
///   <item>Canvas bounds checking</item>
///   <item>Stroke continuity (_lastPoint tracking)</item>
///   <item>Brush logic (pressure → stroke width via BrushSize)</item>
///   <item>Telemetry formatting for the ribbon</item>
/// </list>
/// </summary>
public sealed class PenSessionWinUI3 : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly ICanvasInfo _canvasInfo;
    private readonly UIElement _canvasElement;
    private IPenSession? _session;

    private Point? _lastPoint;
    private PenPoint _latestPenPoint;
    private Point _latestCanvasPoint;
    private PenPoint[] _lastDrainedPoints = [];

    // ── Win32 P/Invoke (for desktop → canvas DIP conversion) ────────

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

    // ── Construction ────────────────────────────────────────────────

    public PenSessionWinUI3(IntPtr hwnd, ICanvasInfo canvasInfo, UIElement canvasElement)
    {
        _hwnd = hwnd;
        _canvasInfo = canvasInfo;
        _canvasElement = canvasElement;
    }

    // ── Public properties ───────────────────────────────────────────

    public bool IsRunning => _session?.IsRunning == true;
    public InputApi Api => _session?.Api ?? InputApi.WintabSystem;
    public int MaxPressure => _session?.MaxPressure ?? 0;

    /// <summary>
    /// Maximum brush size in pixels. Pressure scales from 0.5 to this value.
    /// </summary>
    public double BrushSize { get; set; } = 6;

    public string DebugInfo => _session?.DebugInfo ?? "";

    /// <summary>
    /// All raw PenPoints from the last <see cref="DrainSegments"/> call.
    /// </summary>
    public PenPoint[] LastDrainedPoints => _lastDrainedPoints;

    /// <summary>True if the last <see cref="DrainSegments"/> call processed
    /// any pen points (including hover with pressure=0).</summary>
    public bool HasNewTelemetry { get; private set; }

    // ── Lifecycle ───────────────────────────────────────────────────

    public string? Start(InputApi api)
    {
        // Stop and dispose previous session if switching APIs.
        if (_session != null)
        {
            _session.Stop();
            _session.Dispose();
        }

        _session = api == InputApi.WinUiPointer
            ? new WinUiPointerSession(_canvasElement, _hwnd)
            : PenSessionFactory.Create(api);

        _lastPoint = null;
        return _session.Start(_hwnd);
    }

    public void Stop()
    {
        _session?.Stop();
        _lastPoint = null;
    }

    public void RefreshMapping() => _session?.RefreshMapping();

    public void Dispose()
    {
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    // ── Output ──────────────────────────────────────────────────────

    public bool HasNewData => _session?.HasNewData == true;

    /// <summary>
    /// Drains <see cref="PenPoint"/> records from the session,
    /// converts to canvas-local DIPs, applies brush logic (pressure →
    /// width), and returns <see cref="StrokeSegment"/> records ready
    /// for XAML rendering.
    /// </summary>
    public StrokeSegment[] DrainSegments()
    {
        if (_session == null) return [];

        var points = _session.DrainPoints();
        _lastDrainedPoints = points;
        HasNewTelemetry = points.Length > 0;
        if (points.Length == 0) return [];

        int maxP = _session.MaxPressure;
        var segments = new List<StrokeSegment>();

        foreach (var pt in points)
        {
            var canvasPoint = DesktopToCanvasDips(pt.DesktopX, pt.DesktopY);

            double cw = _canvasInfo.Width;
            double ch = _canvasInfo.Height;
            if (canvasPoint.X < 0 || canvasPoint.X > cw ||
                canvasPoint.Y < 0 || canvasPoint.Y > ch)
            {
                _lastPoint = null;
                continue;
            }

            if (_lastPoint is { } from && pt.Pressure > 0 && maxP > 0)
            {
                float width = (float)pt.Pressure / maxP * (float)BrushSize + 0.5f;
                segments.Add(new StrokeSegment(from, canvasPoint, width));
            }

            _lastPoint = canvasPoint;
            _latestPenPoint = pt;
            _latestCanvasPoint = canvasPoint;
        }

        return [.. segments];
    }

    /// <summary>
    /// Returns the latest pen telemetry for the ribbon.
    /// </summary>
    public PenTelemetry GetTelemetry()
    {
        // Screen = raw desktop position
        var screenPoint = new Point(_latestPenPoint.DesktopX, _latestPenPoint.DesktopY);

        // App = position relative to the window client area
        var appPoint = DesktopToAppDips(_latestPenPoint.DesktopX, _latestPenPoint.DesktopY);

        return new PenTelemetry(
            _latestPenPoint,
            screenPoint,
            appPoint,
            _latestCanvasPoint,
            _session?.MaxPressure ?? 0,
            _session?.Api ?? InputApi.WintabSystem);
    }

    // ── Desktop → app/canvas DIP conversion ──────────────────────────

    private Point DesktopToAppDips(double desktopX, double desktopY)
    {
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

        return new Point(
            (desktopX - clientOrigin.X) * (96.0 / dpiX),
            (desktopY - clientOrigin.Y) * (96.0 / dpiY));
    }

    private Point DesktopToCanvasDips(double desktopX, double desktopY)
    {
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

        var canvasPos = _canvasInfo.PositionInWindow;
        return new Point(
            (desktopX - clientOrigin.X) * (96.0 / dpiX) - canvasPos.X,
            (desktopY - clientOrigin.Y) * (96.0 / dpiY) - canvasPos.Y);
    }
}
