using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using WinPenKit;
using WinPenKit.Wpf;
using WinPenKit.Diagnostics;
using SkiaSharp;

namespace Scribble.Wpf;

public partial class MainWindow : Window
{
    private IPenSession? _session;
    private IntPtr _hwnd;
    private bool _renderActive;

    private Point? _lastCanvasPoint;
    private double _brushSize = 6;
    private IReadOnlyList<InputApi> _apis = [];
    private DateTime _lastPointTime;
    private readonly PenButtonTracker _buttons = new();
    private static readonly Brush ActiveDot = Brushes.LimeGreen;
    private static readonly Brush InactiveDot = Brushes.Gray;
    private static readonly Brush EraserDot_Active = Brushes.OrangeRed;

    // SkiaSharp bitmap-backed canvas.
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private WriteableBitmap? _wpfBitmap;

    // Physical pixels, not DIPs. The canvas used to be sized from ActualWidth - which is DIPs -
    // and handed to a WriteableBitmap declared at 96 dpi, so on a scaled display WPF magnified
    // the result to fit. At 1.75x that is a canvas drawn at 57% of the screen's resolution and
    // then blown up, which looks bumpy no matter how precise the pen positions are. It is why
    // every input API looked equally bad here.
    private int _bitmapWidth;
    private int _bitmapHeight;

    // DIPs, for bounds-checking pen positions, which arrive in DIPs.
    private double _canvasDipWidth;
    private double _canvasDipHeight;

    // Display scaling, so drawing can stay in DIPs while the bitmap is in pixels.
    private double _renderScale = 1.0;

    // Set by --record. Captures the session's stream so one API can be measured against
    // another away from the tablet that produced it.
    private StrokeRecorder? _recorder;
    private string? _recordPath;

    /// <summary>Starts capturing the pen stream to <paramref name="path"/> on close.</summary>
    internal void RecordTo(string path)
    {
        _recordPath = path;
        _recorder = new StrokeRecorder();
    }

    // Canvas origin in desktop device pixels, refreshed whenever the surface is rebuilt.
    private double _canvasOriginX;
    private double _canvasOriginY;


    /// <summary>
    /// The application's own desktop-to-canvas conversion, exposed so a replay can be pushed
    /// through the same code the pen goes through. A replay that used its own arithmetic
    /// would be testing itself.
    /// </summary>
    private (double X, double Y) DesktopToCanvas(double x, double y)
    {
        // Re-read first, exactly as the render tick does before converting a batch of points.
        // Every other sample computes the origin inline here; this one used to read a field
        // that only the surface rebuild wrote, which is the bug L3.origin-tracks-window exists
        // to catch.
        RefreshCanvasOrigin();
        return ((x - _canvasOriginX) / _renderScale, (y - _canvasOriginY) / _renderScale);
    }

    /// <summary>
    /// Re-reads where the canvas sits on the desktop.
    /// </summary>
    /// <remarks>
    /// Called on every render tick that has points, not only when the surface is rebuilt.
    /// Dragging the window changes this origin and raises no size change, so an origin cached
    /// at bitmap-creation time goes stale the moment the window moves - and every pen position
    /// after that converts against the old one, putting the ink exactly the drag distance away
    /// from the pen. Both sessions show it, because the fault sits downstream of both.
    ///
    /// No check catches this. <c>L1.surface-alignment</c> asks whether the origin is a whole
    /// number rather than whether it is correct, and the replay positions its input with
    /// <c>CenteredOn(_canvasOriginX, ...)</c> before subtracting the same value, so a wrong
    /// origin cancels itself exactly.
    /// </remarks>
    private void RefreshCanvasOrigin()
    {
        if (WinPenKit.Wpf.WpfCoordinates.GetTransform(CanvasArea) is { } xf)
        {
            _canvasOriginX = xf.OriginX;
            _canvasOriginY = xf.OriginY;
        }
    }

    /// <summary>
    /// Runs the launch-time acceptance checks against this window and returns the report.
    /// Called after the first layout pass, since the surface does not exist before then.
    /// </summary>
    /// <param name="replayPath">Recording to push through the coordinate conversion, adding
    /// the level 2 and 3 checks. Null runs levels 0 and 1 only.</param>
    /// <remarks>
    /// Asynchronous because of <c>L1.presentation-sampling</c>, which watches the screen until
    /// the markers it drew appear there. Awaiting yields this thread, which is the one that has
    /// to render them.
    /// </remarks>
    internal async Task<SelfTest> RunSelfTest(string? replayPath = null)
    {
        var t = new SelfTest { AppName = "Scribble.Wpf" };

        RefreshCanvasOrigin();

        t.CheckDpiAwareness();
        t.CheckWindowPlacement(_hwnd);
        t.ReportScale(_renderScale);

        t.CheckSurfacePhysical(_bitmapWidth, _bitmapHeight,
                               _canvasDipWidth, _canvasDipHeight, _renderScale);
        t.CheckSurfaceAlignment(_canvasOriginX, _canvasOriginY);

        // Stretch="None" means the image is presented at the bitmap's own DIP size, which is
        // its pixel count divided by the DPI it was declared at. Measuring it rather than
        // asserting it is the point: this is exactly where a correctly sized bitmap can still
        // be scaled back off the pixel grid.
        double presentedPxW = DrawImage.ActualWidth * _renderScale;
        double presentedPxH = DrawImage.ActualHeight * _renderScale;
        t.CheckPresentation1To1(_bitmapWidth, _bitmapHeight, presentedPxW, presentedPxH);

        if (replayPath != null)
        {
            var stroke = StrokeReplay.Load(replayPath)
                .CenteredOn(_canvasOriginX, _canvasOriginY, _bitmapWidth, _bitmapHeight);
            t.CheckReplay(stroke, DesktopToCanvas, _renderScale);
        }

        // Last two, in this order: one measures what is on the screen, the other moves the
        // window. Measuring first means the window has not just been moved back.
        await MeasurePresentationSampling(t);

        t.CheckOriginTracksWindow(_hwnd, DesktopToCanvas, _renderScale);

        return t;
    }

    /// <summary>
    /// Draws the probe's markers into the canvas, presents a frame, and lets the probe find
    /// them on the screen.
    /// </summary>
    /// <remarks>
    /// The canvas is cleared first so nothing already on it can be mistaken for a marker, and
    /// the markers are left in place: the self test shuts the application down straight after.
    /// </remarks>
    private async Task MeasurePresentationSampling(SelfTest t)
    {
        if (_skCanvas == null)
        {
            t.Skip("L1.presentation-sampling", "no drawing surface");
            return;
        }

        var probe = new PresentationProbe(_bitmapWidth, _bitmapHeight);

        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        // The canvas carries Scale(_renderScale) so that stroke widths can be given in DIPs.
        // Marker coordinates are surface pixels by definition, so they are drawn with no
        // transform -- the same reason EnsureBitmap resets the matrix to blit the old bitmap.
        _skCanvas.Save();
        _skCanvas.ResetMatrix();
        probe.Draw(m =>
        {
            using var paint = new SKPaint { Color = new SKColor(m.R, m.G, m.B), IsAntialias = false };
            _skCanvas.DrawRect(m.X, m.Y, m.Size, m.Size, paint);
        });
        _skCanvas.Restore();

        CopyToWpfBitmap();

        await probe.MeasureAsync(t, _hwnd, TimeSpan.FromSeconds(5));
    }


    public MainWindow()
    {
        // Wintab hands packets to whichever context is on top of the driver's overlap order, and
        // losing focus to another application drops this one down it. Without telling the session
        // we are back, the first stroke after returning is silently swallowed. Matches what Qt
        // does on window activation, which is why Qt apps do not have the bug.
        Activated += (_, _) => _session?.OnActivated();

        InitializeComponent();

        CompositionTarget.Rendering += RenderTimer_Tick;

        Loaded += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // The window manager cascades each launch a little further down, so an application
            // that fits on one run hangs below the work area a few runs later - and pen input
            // aimed at the part hanging off is discarded with no error.
            WinPenKit.WindowPlacement.ClampToWorkArea(_hwnd);

            _apis = WpfPenApis.GetAvailable();

            foreach (var api in _apis)
                ApiCombo.Items.Add(api.Label());

            if (ApiCombo.Items.Count > 0)
                ApiCombo.SelectedIndex = 0;
        };

        Closing += (_, _) =>
        {
            if (_recorder != null && _recordPath != null)
            {
                int written = _recorder.Save(_recordPath);
                Console.Error.WriteLine($"[record] {written} points -> {_recordPath}");
            }

            _renderActive = false;
            _session?.Stop();
            _session?.Dispose();
            _skCanvas?.Dispose();
            _skBitmap?.Dispose();
        };
    }

    // ── Skia bitmap management ───────────────────────────────────

    private void EnsureBitmap()
    {
        double dipW = CanvasArea.ActualWidth;
        double dipH = CanvasArea.ActualHeight;
        if (dipW <= 0 || dipH <= 0) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1.0;

        int w = (int)Math.Ceiling(dipW * scale);
        int h = (int)Math.Ceiling(dipH * scale);
        if (_skBitmap != null && _bitmapWidth == w && _bitmapHeight == h) return;

        var oldBitmap = _skBitmap;
        var oldCanvas = _skCanvas;

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);

        // Drawing code keeps working in DIPs; the canvas transform is the only thing that knows
        // about display scaling.
        _skCanvas.Scale((float)scale);

        _bitmapWidth = w;
        _bitmapHeight = h;
        _canvasDipWidth = dipW;
        _canvasDipHeight = dipH;
        _renderScale = scale;

        RefreshCanvasOrigin();

        // Physical pixels of canvas against pixels of bitmap. These must match, or the bitmap is
        // being scaled on its way to the screen.
        SurfaceLabel.Text =
            $"Surface: {w}x{h}px  scale {scale:F2}  canvas {dipW:F0}x{dipH:F0}dip";

        // Where the image actually lands, in device pixels. A fractional offset here means WPF
        // is resampling the whole canvas to draw it between pixels, which softens every edge at
        // once - indistinguishable from a bad brush engine, and invisible to any check of the
        // coordinates or the resolution.
        try
        {
            var originDip = DrawImage.TransformToAncestor(this).Transform(new Point(0, 0));
            double px = originDip.X * scale, py = originDip.Y * scale;
            double fx = Math.Abs(px - Math.Round(px)), fy = Math.Abs(py - Math.Round(py));
            bool aligned = fx < 0.01 && fy < 0.01;
            OffsetLabel.Text = $"Offset: {px:F2},{py:F2}px {(aligned ? "aligned" : "FRACTIONAL")}";
            OffsetLabel.Foreground = aligned
                ? new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77))
                : new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));
        }
        catch (InvalidOperationException)
        {
            // Not arranged yet; the next SizeChanged will report it.
        }

        // Clear to background.
        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        // Copy old content if resizing.
        if (oldBitmap != null)
        {
            // Old pixels are already physical, so the DIP transform has to come off for the
            // blit or the preserved content would be magnified by the scale factor each resize.
            _skCanvas.Save();
            _skCanvas.ResetMatrix();
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            _skCanvas.Restore();
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        // Declared at the display's dpi, so WPF lays the image out at its DIP size and presents
        // the pixels 1:1 instead of scaling them.
        _wpfBitmap = new WriteableBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Bgra32, null);
        CopyToWpfBitmap();
        DrawImage.Source = _wpfBitmap;
    }

    private void CopyToWpfBitmap()
    {
        if (_skBitmap == null || _wpfBitmap == null) return;

        _wpfBitmap.Lock();
        unsafe
        {
            var src = _skBitmap.GetPixels();
            var dst = _wpfBitmap.BackBuffer;
            int bytes = _bitmapWidth * _bitmapHeight * 4;
            Buffer.MemoryCopy((void*)src, (void*)dst, bytes, bytes);
        }
        _wpfBitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmapWidth, _bitmapHeight));
        _wpfBitmap.Unlock();
    }

    private void ClearBitmap()
    {
        _skCanvas?.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        CopyToWpfBitmap();
    }

    private void CanvasArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureBitmap();
    }

    // ── Session lifecycle ────────────────────────────────────────

    private void StartSession()
    {
        if (_apis.Count == 0 || ApiCombo.SelectedIndex < 0) return;

        _session?.Stop();
        _session?.Dispose();
        _buttons.Reset();

        var api = _apis[ApiCombo.SelectedIndex];
        _session = api == InputApi.WpfStylus
            ? new WpfStylusSession(CanvasArea)
            : PenSessionFactory.Create(api);
        _lastCanvasPoint = null;

        EnsureBitmap();

        var error = _session.Start(_hwnd);
        if (error != null)
        {
            Title = $"Scribble WPF - {error}";
            _session.Dispose();
            _session = null;
            return;
        }

        // Described here rather than at save time, so the header names the session the points
        // actually came from. Switching the API while recording lands in Describe's
        // spans-more-than-one-session path instead of relabelling everything captured so far.
        _recorder?.Describe(_session.GetType().Name, _session.MaxPressure);

        Title = "Scribble WPF - WinPenKit";
        _renderActive = true;
    }

    // ── Render timer ─────────────────────────────────────────────

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (!_renderActive || _session == null || _skCanvas == null) return;

        var points = _session.DrainPoints();
        if (points.Length == 0)
        {
            if ((DateTime.UtcNow - _lastPointTime).TotalMilliseconds > 200)
            {
                ProximityDot.Fill = Brushes.Gray;
                ProximityLabel.Text = "Out";
            }
            return;
        }

        RefreshCanvasOrigin();

        int maxP = _session.MaxPressure;
        bool drew = false;

        foreach (var pt in points)
        {
            _buttons.Update(pt);
            _recorder?.Add(pt);

            // Deliberately not CanvasArea.PointFromScreen: it truncates through an integer
            // Win32 POINT, which quantizes every pen position to a whole device pixel and
            // facets the stroke. Measured here at 100% of 3014 points before this changed.
            Point canvasPt = new(
                (pt.DesktopX - _canvasOriginX) / _renderScale,
                (pt.DesktopY - _canvasOriginY) / _renderScale);

            // Bounds in DIPs: canvasPt is in DIPs, the bitmap is in pixels.
            if (canvasPt.X < 0 || canvasPt.X > _canvasDipWidth ||
                canvasPt.Y < 0 || canvasPt.Y > _canvasDipHeight)
            {
                _lastCanvasPoint = null;
                continue;
            }

            if (_lastCanvasPoint is { } from && pt.Pressure > 0 && maxP > 0)
            {
                // Brush size is a count of physical pixels, the same as in every other
                // sample. The canvas carries Scale(scale), so a width handed to it in canvas
                // units arrives on screen multiplied by that - divide it back out here rather
                // than letting the same slider mean 6px in one sample and 13.5 in another.
                float width = ((float)pt.Pressure / maxP * (float)_brushSize + 0.5f)
                              / (float)_renderScale;

                using var paint = new SKPaint
                {
                    Color = SKColors.Black,
                    StrokeWidth = width,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke
                };

                _skCanvas.DrawLine(
                    (float)from.X, (float)from.Y,
                    (float)canvasPt.X, (float)canvasPt.Y,
                    paint);
                drew = true;
            }

            _lastCanvasPoint = canvasPt;
        }

        if (drew)
            CopyToWpfBitmap();

        // Update telemetry.
        var last = points[^1];
        _lastPointTime = DateTime.UtcNow;

        ProximityDot.Fill = Brushes.LimeGreen;
        ProximityLabel.Text = "Proximity";
        CursorLabel.Text = $"Cursor: {last.Cursor}";

        // The unit comes from the session; this backend has no device-native coordinate, so
        // it reports None and the readout says so rather than printing zeros.
        var rawUnits = _session?.Conventions.RawUnits ?? PenRawUnits.None;
        RawPosLabel.Text = rawUnits == PenRawUnits.None
            ? "Raw: --"
            : $"Raw: {last.RawX},{last.RawY} ({rawUnits.Label()})";
        // F2, not F0: at 1.75x, PointFromScreen turns an integer desktop coordinate into a
        // fractional DIP anyway, so a decimal Canvas readout proves nothing about the input.
        // The fractional part has to be visible here or quantization is undetectable.
        ScreenPosLabel.Text = $"Screen: {last.DesktopX:F2},{last.DesktopY:F2}";

        // App = position relative to the window client area
        Point appPt;
        try { appPt = this.PointFromScreen(new Point(last.DesktopX, last.DesktopY)); }
        catch { appPt = new Point(); }
        AppPosLabel.Text = $"App: {appPt.X:F0},{appPt.Y:F0}";

        // Canvas = position relative to the drawing surface
        Point lastCanvas;
        lastCanvas = new Point(
            (last.DesktopX - _canvasOriginX) / _renderScale,
            (last.DesktopY - _canvasOriginY) / _renderScale);
        CanvasPosLabel.Text = $"Canvas: {lastCanvas.X:F2},{lastCanvas.Y:F2}";

        float pct = maxP > 0 ? (float)last.Pressure / maxP * 100f : 0f;
        RawPressureLabel.Text = $"Raw: {last.Pressure}";
        NormPressureLabel.Text = $"Norm: {pct:F1}%";

        AzimuthLabel.Text = $"Azimuth: {last.Azimuth:F1}";
        AltitudeLabel.Text = $"Altitude: {last.Altitude:F1}";
        TwistLabel.Text = $"Twist: {last.Twist:F1}";

        TipDot.Fill = (_buttons.IsTipDown && !_buttons.IsEraser) ? ActiveDot : InactiveDot;
        EraserDot.Fill = _buttons.IsEraser ? EraserDot_Active : InactiveDot;
        Barrel1Dot.Fill = _buttons.IsBarrelDown(1) ? ActiveDot : InactiveDot;
        Barrel2Dot.Fill = _buttons.IsBarrelDown(2) ? ActiveDot : InactiveDot;
        Barrel3Dot.Fill = _buttons.IsBarrelDown(3) ? ActiveDot : InactiveDot;
        if (_buttons.LastRawButtons != 0)
            RawButtonsLabel.Text = $"0x{_buttons.LastRawButtons:X8}";
    }

    // ── Event handlers ───────────────────────────────────────────

    private void ApiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
            StartSession();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearBitmap();
        _lastCanvasPoint = null;
    }

    private void BrushSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _brushSize = e.NewValue;
        if (BrushSizeLabel != null)
            BrushSizeLabel.Text = $"{(int)e.NewValue} px";
    }
}
