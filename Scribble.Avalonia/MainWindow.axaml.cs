using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Linq;
using WinPenKit;
using WinPenKit.Diagnostics;
using WinPenKit.Avalonia;
using SkiaSharp;

namespace Scribble.Avalonia;

public partial class MainWindow : Window
{
    private IPenSession? _session;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private Point? _lastCanvasPoint;
    private double _brushSize = 6;
    private IReadOnlyList<InputApi> _apis = [];
    private DateTime _lastPointTime;
    private readonly PenButtonTracker _buttons = new();
    private static readonly IBrush ActiveDot = Brushes.LimeGreen;
    private static readonly IBrush InactiveDot = Brushes.Gray;
    private static readonly IBrush EraserActiveDot = Brushes.OrangeRed;

    // Skia bitmap-backed canvas.
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private WriteableBitmap? _avBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    /// <summary>Layout-unit-to-device-pixel ratio the surface was built at.</summary>
    private double _renderScale = 1.0;

    public MainWindow()
    {
        // Wintab hands packets to whichever context is on top of the driver's overlap order, and
        // losing focus to another application drops this one down it. Without telling the session
        // we are back, the first stroke after returning is silently swallowed. Matches what Qt
        // does on window activation, which is why Qt apps do not have the bug.
        Activated += (_, _) => _session?.OnActivated();

        InitializeComponent();

        _renderTimer.Tick += RenderTimer_Tick;

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Value")
            {
                _brushSize = BrushSizeSlider.Value;
                BrushSizeLabel.Text = $"{(int)_brushSize} px";
            }
        };

        CanvasArea.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name is "Bounds")
                EnsureBitmap();
        };

        Opened += (_, _) =>
        {
            // WM_POINTER subclassing doesn't receive events in Avalonia.
            var apiList = PenSessionFactory.GetAvailableApis()
                .Where(a => a != InputApi.WmPointer).ToList();
            apiList.Add(InputApi.AvaloniaPointer);
            _apis = apiList;

            foreach (var api in _apis)
            {
                string name = api switch
                {
                    InputApi.WintabSystem => "Wintab",
                    InputApi.WintabDigitizer => "Wintab (high-res)",
                    InputApi.AvaloniaPointer => "Avalonia Pointer",
                    _ => api.ToString()
                };
                ApiCombo.Items.Add(name);
            }

            ApiCombo.SelectionChanged += ApiCombo_SelectionChanged;

            if (ApiCombo.Items.Count > 0)
                ApiCombo.SelectedIndex = 0;
        };

        Closing += (_, _) =>
        {
            _renderTimer.Stop();
            _session?.Stop();
            _session?.Dispose();
            _skCanvas?.Dispose();
            _skBitmap?.Dispose();
        };
    }

    /// <summary>
    /// The window's own desktop-to-canvas conversion, exposed so a replay goes through the
    /// same code the pen does.
    /// </summary>
    private (double X, double Y) DesktopToCanvas(double x, double y)
    {
        double scale = RenderScaling;
        var windowOrigin = this.PointToScreen(new global::Avalonia.Point(0, 0));
        var canvasOrigin = CanvasArea.TranslatePoint(new global::Avalonia.Point(0, 0), this)
                           ?? new global::Avalonia.Point(0, 0);
        return ((x - windowOrigin.X) - canvasOrigin.X * scale,
                (y - windowOrigin.Y) - canvasOrigin.Y * scale);
    }

    /// <summary>Runs the launch-time acceptance checks against this window.</summary>
    internal SelfTest RunSelfTest(string? replayPath = null)
    {
        var t = new SelfTest { AppName = "Scribble.Avalonia" };

        double scale = RenderScaling;

        t.CheckDpiAwareness();
        t.CheckWindowPlacement(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        t.ReportScale(scale);

        // Bounds are DIPs in Avalonia, so the ratio here is the render scaling.
        t.CheckSurfacePhysical(_bitmapWidth, _bitmapHeight,
                               CanvasArea.Bounds.Width, CanvasArea.Bounds.Height, scale);

        // Window origin is a whole device pixel, so taking it through PixelPoint loses
        // nothing; the canvas offset within the window is the part that must stay fractional.
        var windowOrigin = this.PointToScreen(new global::Avalonia.Point(0, 0));
        var dipOffset = CanvasArea.TranslatePoint(new global::Avalonia.Point(0, 0), this)
                        ?? new global::Avalonia.Point(0, 0);
        t.CheckSurfaceAlignment(windowOrigin.X + dipOffset.X * scale,
                                windowOrigin.Y + dipOffset.Y * scale);

        t.CheckPresentation1To1(_bitmapWidth, _bitmapHeight,
                                DrawImage.Bounds.Width * scale,
                                DrawImage.Bounds.Height * scale);

        if (replayPath != null)
        {
            // The canvas draws in physical pixels, so the snap scale is 1.0 - the coordinates
            // this conversion produces are already device pixels.
            var stroke = StrokeReplay.Load(replayPath)
                .CenteredOn(windowOrigin.X + dipOffset.X * scale,
                            windowOrigin.Y + dipOffset.Y * scale,
                            _bitmapWidth, _bitmapHeight);
            t.CheckReplay(stroke, DesktopToCanvas, 1.0);
        }

        return t;
    }

    // ── Skia bitmap management ───────────────────────────────────

    private void EnsureBitmap()
    {
        // Physical pixels, not DIPs. Bounds are DIPs, and a bitmap sized from them is
        // magnified to fit the canvas - at 2.25x that is a surface drawn at 44% of the
        // display's resolution, which no amount of coordinate precision survives.
        double scale = RenderScaling;
        int w = (int)Math.Ceiling(CanvasArea.Bounds.Width * scale);
        int h = (int)Math.Ceiling(CanvasArea.Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;
        if (_skBitmap != null && _bitmapWidth == w && _bitmapHeight == h) return;

        var oldBitmap = _skBitmap;
        var oldCanvas = _skCanvas;

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);
        _bitmapWidth = w;
        _bitmapHeight = h;
        _renderScale = scale;

        // Clear to background color.
        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        // Copy old content if resizing.
        if (oldBitmap != null)
        {
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        // Declared at the display's dpi, so Avalonia lays the image out at its DIP size and
        // presents the pixels 1:1 instead of scaling them.
        _avBitmap = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96 * scale, 96 * scale),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            global::Avalonia.Platform.AlphaFormat.Premul);

        CopyToAvBitmap();
        DrawImage.Source = _avBitmap;
    }

    private void CopyToAvBitmap()
    {
        if (_skBitmap == null || _avBitmap == null) return;

        using var fb = _avBitmap.Lock();
        unsafe
        {
            var src = _skBitmap.GetPixels();
            var dst = fb.Address;
            int bytes = _bitmapWidth * _bitmapHeight * 4;
            Buffer.MemoryCopy((void*)src, (void*)dst, bytes, bytes);
        }
    }

    private void ClearBitmap()
    {
        _skCanvas?.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        CopyToAvBitmap();
        DrawImage.InvalidateVisual();
    }

    // ── Session lifecycle ────────────────────────────────────────

    private void StartSession()
    {
        if (_apis.Count == 0 || ApiCombo.SelectedIndex < 0) return;

        _session?.Stop();
        _session?.Dispose();
        _buttons.Reset();

        var api = _apis[ApiCombo.SelectedIndex];
        _session = api == InputApi.AvaloniaPointer
            ? new AvaloniaPointerSession(CanvasArea)
            : PenSessionFactory.Create(api);
        _lastCanvasPoint = null;

        EnsureBitmap();

        // Get the window handle for Wintab/WM_POINTER sessions.
        IntPtr hwnd = IntPtr.Zero;
        if (TryGetPlatformHandle() is { } handle)
            hwnd = handle.Handle;

        var error = _session.Start(hwnd);
        if (error != null)
        {
            Title = $"Scribble Avalonia - {error}";
            _session.Dispose();
            _session = null;
            return;
        }

        Title = "Scribble Avalonia - WinPenKit";
        _renderTimer.Start();
    }

    // ── Render timer ─────────────────────────────────────────────

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null || _skCanvas == null) return;

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

        int maxP = _session.MaxPressure;
        bool drew = false;

        foreach (var pt in points)
        {
            _buttons.Update(pt);

            // Desktop pixels to canvas-local DIPs.
            //
            // Converted by hand rather than through PointToClient, which takes a PixelPoint and
            // so forces the position onto the whole-pixel grid on the way in. That is not a
            // rounding detail: quantizing the same hi-res input to whole pixels takes the median
            // turn between consecutive segments from about 1.5 degrees to 11.3, because at the
            // ~2px steps a tablet reports there are only a handful of directions a segment on an
            // integer grid can point in. The path stops following the pen and starts zigzagging.
            //
            // The window origin is genuinely on a pixel boundary, so taking it as an integer
            // loses nothing; only the pen's own position needs the precision kept.
            Point canvasPt;
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) continue;

                var windowOrigin = topLevel.PointToScreen(new Point(0, 0));
                double scale = topLevel.RenderScaling;
                var clientPt = new Point(
                    (pt.DesktopX - windowOrigin.X) / scale,
                    (pt.DesktopY - windowOrigin.Y) / scale);

                var canvasOrigin = CanvasArea.TranslatePoint(new Point(0, 0), topLevel);
                if (canvasOrigin == null) continue;

                // Physical pixels, matching the surface. Scaling up here rather than
                // dividing the pen position down keeps the sub-pixel precision intact.
                canvasPt = new Point(
                    (clientPt.X - canvasOrigin.Value.X) * scale,
                    (clientPt.Y - canvasOrigin.Value.Y) * scale);
            }
            catch
            {
                _lastCanvasPoint = null;
                continue;
            }

            if (canvasPt.X < 0 || canvasPt.X > _bitmapWidth ||
                canvasPt.Y < 0 || canvasPt.Y > _bitmapHeight)
            {
                _lastCanvasPoint = null;
                continue;
            }

            if (_lastCanvasPoint is { } from && pt.Pressure > 0 && maxP > 0)
            {
                // Brush size is a DIP size, so it scales with the surface.
                float width = ((float)pt.Pressure / maxP * (float)_brushSize + 0.5f)
                              * (float)_renderScale;

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
        {
            CopyToAvBitmap();
            DrawImage.InvalidateVisual();
        }

        // Update telemetry.
        var last = points[^1];
        _lastPointTime = DateTime.UtcNow;

        ProximityDot.Fill = Brushes.LimeGreen;
        ProximityLabel.Text = "Proximity";
        CursorLabel.Text = $"Cursor: {last.Cursor}";

        RawPosLabel.Text = $"Raw: {last.RawX},{last.RawY}";
        ScreenPosLabel.Text = $"Screen: {last.DesktopX:F0},{last.DesktopY:F0}";

        // App = position relative to the window client area
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var screenPt = new PixelPoint((int)last.DesktopX, (int)last.DesktopY);
                var clientPt = topLevel.PointToClient(screenPt);
                AppPosLabel.Text = $"App: {clientPt.X:F0},{clientPt.Y:F0}";
            }
        }
        catch { AppPosLabel.Text = "App: --,--"; }

        CanvasPosLabel.Text = _lastCanvasPoint is { } cp
            ? $"Canvas: {cp.X:F1},{cp.Y:F1}" : "Canvas: --,--";

        float pct = maxP > 0 ? (float)last.Pressure / maxP * 100f : 0f;
        RawPressureLabel.Text = $"Raw: {last.Pressure}";
        NormPressureLabel.Text = $"Norm: {pct:F1}%";

        AzimuthLabel.Text = $"Azimuth: {last.Azimuth:F1}";
        AltitudeLabel.Text = $"Altitude: {last.Altitude:F1}";
        TwistLabel.Text = $"Twist: {last.Twist:F1}";

        TipDot.Fill = (_buttons.IsTipDown && !_buttons.IsEraser) ? ActiveDot : InactiveDot;
        EraserDot.Fill = _buttons.IsEraser ? EraserActiveDot : InactiveDot;
        Barrel1Dot.Fill = _buttons.IsBarrelDown(1) ? ActiveDot : InactiveDot;
        Barrel2Dot.Fill = _buttons.IsBarrelDown(2) ? ActiveDot : InactiveDot;
        Barrel3Dot.Fill = _buttons.IsBarrelDown(3) ? ActiveDot : InactiveDot;
        if (_buttons.LastRawButtons != 0)
            RawButtonsLabel.Text = $"0x{_buttons.LastRawButtons:X8}";
    }

    // ── Event handlers ───────────────────────────────────────────

    private void ApiCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        StartSession();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        ClearBitmap();
        _lastCanvasPoint = null;
    }
}
