using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using WinPenKit;
using WinPenKit.Diagnostics;
using WinPenKit.WinUI;

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
    InputApi Api,
    // What Point.RawX is measured in, or None when this backend has no device-native
    // coordinate. Carried here because the ribbon has the point and not the session.
    PenRawUnits RawUnits);

/// <summary>
/// WinUI 3 wrapper around <see cref="IPenSession"/>. Converts
/// <see cref="PenPoint"/> desktop coordinates to canvas-local DIPs
/// and produces <see cref="StrokeSegment"/> records for the brush engine.
///
/// <para>All input API management, packet handling, coordinate mapping,
/// and tilt conversion lives in the WinPenKit library. This class only handles:</para>
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
    private StrokeRecorder? _recorder;

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

    /// <summary>
    /// Capture every drained point into <paramref name="recorder"/> until the session ends.
    /// </summary>
    /// <remarks>
    /// Set here rather than in the window, because this is where the points are drained --
    /// the window only sees <see cref="LastDrainedPoints"/> when telemetry changed, which is
    /// not every drain.
    /// </remarks>
    public void RecordTo(StrokeRecorder recorder) => _recorder = recorder;

    /// <summary>True if the last <see cref="DrainSegments"/> call processed
    /// any pen points (including hover with pressure=0).</summary>
    public bool HasNewTelemetry { get; private set; }

    // ── Diagnostic counters ──────────────────────────────────────
    //
    // For a fault where a stroke silently fails to appear. "No ink" has three quite different
    // causes and they need different fixes, so the counters are chosen to tell them apart:
    //
    //   Pts 0                  input never arrived - a window activation or event wiring problem
    //   Pts >0, Off >0, Seg 0  points arrive but convert to positions outside the canvas, so the
    //                          coordinate mapping is wrong
    //   Pts >0, Seg >0         points arrive and segments are produced, so the fault is in
    //                          rendering rather than input
    //
    // Cumulative since the session started; Clear resets them.

    /// <summary>Pen points drained from the session.</summary>
    public long PointsSeen { get; private set; }

    /// <summary>Points whose canvas position fell outside the canvas bounds.</summary>
    public long PointsOffCanvas { get; private set; }

    /// <summary>Segments handed to the canvas to draw.</summary>
    public long SegmentsDrawn { get; private set; }

    public void ResetCounters()
    {
        PointsSeen = 0;
        PointsOffCanvas = 0;
        SegmentsDrawn = 0;
    }

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
        var error = _session.Start(_hwnd);

        // Described at start, not at save: this sample switches pen API while a recording is
        // running, and the header has to name the session the points came from.
        if (error is null)
            _recorder?.Describe(_session.GetType().Name, _session.MaxPressure, _session.Conventions.Timestamp);

        return error;
    }

    public void Stop()
    {
        _session?.Stop();
        _lastPoint = null;
    }

    public void RefreshMapping() => _session?.RefreshMapping();

    /// <summary>
    /// Pass a window activation through to the inner session.
    /// </summary>
    /// <remarks>
    /// Forwarding is not optional. <see cref="IPenSession.OnActivated"/> is a default interface
    /// method, so this class - which holds an <see cref="IPenSession"/> rather than being one -
    /// would otherwise leave the Wintab session never hearing about focus at all, and the fix
    /// would look wired up while doing nothing.
    /// </remarks>
    public void OnActivated() => _session?.OnActivated();

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

        PointsSeen += points.Length;

        if (_recorder is { } rec)
            foreach (var pt in points)
                rec.Add(pt);

        foreach (var pt in points)
        {
            var canvasPoint = DesktopToCanvasDips(pt.DesktopX, pt.DesktopY);

            double cw = _canvasInfo.Width;
            double ch = _canvasInfo.Height;
            if (canvasPoint.X < 0 || canvasPoint.X > cw ||
                canvasPoint.Y < 0 || canvasPoint.Y > ch)
            {
                PointsOffCanvas++;
                _lastPoint = null;
                continue;
            }

            if (_lastPoint is { } from && pt.Pressure > 0 && maxP > 0)
            {
                float width = (float)pt.Pressure / maxP * (float)BrushSize + 0.5f;
                segments.Add(new StrokeSegment(from, canvasPoint, width));
                SegmentsDrawn++;
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
            _session?.Api ?? InputApi.WintabSystem,
            _session?.Conventions.RawUnits ?? PenRawUnits.None);
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
