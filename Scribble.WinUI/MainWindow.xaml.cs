using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using WinPenKit;
using WinPenKit.Diagnostics;
using System.Linq;

namespace Scribble.WinUI;

/// <summary>
/// WinUI 3 pen scribble demo. Composes a <see cref="Controls.ScribbleRibbon"/>
/// (consolidated status/control bar) with a <see cref="Controls.DrawingCanvas"/>
/// and a <see cref="PenSessionWinUI3"/> for pen input.
/// </summary>
public sealed partial class MainWindow : Window
{
    private PenSessionWinUI3? _session;
    private readonly CanvasInfoCache _canvasInfo = new();

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public MainWindow()
    {
        this.InitializeComponent();
        _renderTimer.Tick += RenderTimer_Tick;

        // The window manager cascades each launch a little further down, so an application that
        // fits on one run hangs below the work area a few runs later - and pen input aimed at
        // the part hanging off is discarded with no error.
        WinPenKit.WindowPlacement.ClampToWorkArea(
            WinRT.Interop.WindowNative.GetWindowHandle(this));

        // Populate the toolbar dropdown with discovered APIs + WinUI Pointer.
        // WM_POINTER subclassing doesn't receive events in WinUI 3.
        var apis = PenSessionFactory.GetAvailableApis()
            .Where(a => a != InputApi.WmPointer).ToList();
        apis.Add(InputApi.WinUiPointer);
        Toolbar.SetAvailableApis(apis);

        Toolbar.ContextModeChanged += (_, _) =>
        {
            if (_session?.IsRunning == true)
                Restart();
        };

        Toolbar.ClearClicked += (_, _) => Canvas.Clear();

        // Wintab hands packets to whichever context is on top of the driver's overlap order, and
        // losing focus to another application drops this one down it. Without telling the session
        // we are back, the first stroke after returning is silently swallowed. Matches what Qt
        // does on window activation, which is why Qt apps do not have the bug.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _session?.OnActivated();
        };

        Canvas.SizeChanged += (_, e) =>
        {
            _canvasInfo.Width = e.NewSize.Width;
            _canvasInfo.Height = e.NewSize.Height;

            if (_session?.IsRunning != true && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                Start();
        };

        Closed += (_, _) =>
        {
            Stop();
            _session?.Dispose();
            _renderTimer.Stop();
        };
    }

    private void CopyPenData_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var text = Toolbar.GetCopyText();
        if (_session != null)
        {
            var debug = _session.DebugInfo;
            if (debug.Length > 0)
                text += "\n" + debug;
        }
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        args.Handled = true;
    }

    private void Start()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _session ??= new PenSessionWinUI3(hwnd, _canvasInfo, Canvas);

        _canvasInfo.PositionInWindow = Canvas.GetPositionInWindow();

        var api = Toolbar.SelectedApi;
        Toolbar.ResetButtons();
        var error = _session.Start(api);

        if (error != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Scribble.WinUI] Start failed: {error}");
            Title = $"WinTab Scribble - {error}";
            return;
        }

        if (_session.IsRunning)
        {
            Title = "WinTab Scribble - WinUI 3";
            Toolbar.SetMode(_session.Api);
            _renderTimer.Start();
        }
    }

    private void Stop()
    {
        _renderTimer.Stop();
        _session?.Stop();
    }

    private void Restart()
    {
        Stop();
        Start();
    }

    private void RenderTimer_Tick(object? sender, object e)
    {
        if (_session == null) return;

        _canvasInfo.PositionInWindow = Canvas.GetPositionInWindow();
        _session.BrushSize = Toolbar.BrushSize;

        Toolbar.Tick();

        var segments = _session.DrainSegments();
        foreach (var seg in segments)
        {
            Canvas.QueueStroke(
                (float)seg.From.X, (float)seg.From.Y,
                (float)seg.To.X, (float)seg.To.Y,
                seg.Width);
        }

        Canvas.Flush();

        // Every tick, not only when points arrive: a counter that stops moving while the pen is
        // down says as much as one that climbs.
        Toolbar.UpdateCounters(_session.PointsSeen, _session.PointsOffCanvas, _session.SegmentsDrawn);

        if (_session.HasNewTelemetry)
        {
            Toolbar.UpdateTelemetry(_session.GetTelemetry());

            foreach (var pt in _session.LastDrainedPoints)
                Toolbar.UpdateButtons(pt);
        }
    }

    /// <summary>
    /// Arranges for the launch-time checks to run once the canvas has a surface, then exit
    /// with the report's code. The canvas is a private XAML field, so this lives here rather
    /// than in App.
    /// </summary>
    internal void ArmSelfTest(string? replayPath = null)
    {
        Canvas.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            DispatcherQueue.TryEnqueue(() => Environment.Exit(RunSelfTest(replayPath).Emit()));
        };
    }

    /// <summary>
    /// The window's own desktop-to-canvas conversion, in effective pixels, exposed so a replay
    /// goes through the same code the pen does.
    /// </summary>
    private (double X, double Y) DesktopToCanvas(double x, double y)
    {
        double scale = Canvas.CanvasLogicalSize.Scale;
        var pos = Canvas.GetPositionInWindow();
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        return ((x - origin.X) / scale - pos.X, (y - origin.Y) / scale - pos.Y);
    }

    /// <summary>Runs the launch-time acceptance checks against this window.</summary>
    internal SelfTest RunSelfTest(string? replayPath = null)
    {
        var t = new SelfTest { AppName = "Scribble.WinUI" };

        var (logicalW, logicalH, scale) = Canvas.CanvasLogicalSize;
        var (bmpW, bmpH) = Canvas.BitmapSize;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        t.CheckDpiAwareness();
        t.CheckWindowPlacement(hwnd);
        t.ReportScale(scale);

        // ActualWidth is in XAML effective pixels, so the ratio here is the rasterization
        // scale.
        t.CheckSurfacePhysical(bmpW, bmpH, logicalW, logicalH, scale);

        var posInWindow = Canvas.GetPositionInWindow();
        var clientOrigin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref clientOrigin);
        t.CheckSurfaceAlignment(clientOrigin.X + posInWindow.X * scale,
                                clientOrigin.Y + posInWindow.Y * scale);

        var (presW, presH) = Canvas.PresentedDeviceSize;
        t.CheckPresentation1To1(bmpW, bmpH, presW, presH);

        if (replayPath != null)
        {
            // The conversion yields effective pixels, so the snap scale is the rasterization
            // scale - that is what turns them into device pixels.
            var stroke = StrokeReplay.Load(replayPath)
                .CenteredOn(clientOrigin.X + posInWindow.X * scale,
                            clientOrigin.Y + posInWindow.Y * scale, bmpW, bmpH);
            t.CheckReplay(stroke, DesktopToCanvas, scale);
        }

        // Last: this one moves the window and puts it back.
        t.CheckOriginTracksWindow(hwnd, DesktopToCanvas, scale);

        return t;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    private sealed class CanvasInfoCache : ICanvasInfo
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public Point PositionInWindow { get; set; }
    }
}
