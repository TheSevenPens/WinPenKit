using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using PenSession;
using PenSession.Wpf;
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

    // SkiaSharp bitmap-backed canvas.
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private WriteableBitmap? _wpfBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    public MainWindow()
    {
        InitializeComponent();

        CompositionTarget.Rendering += RenderTimer_Tick;

        Loaded += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // WM_POINTER subclassing doesn't receive events in WPF.
            var apiList = PenSessionFactory.GetAvailableApis()
                .Where(a => a != InputApi.WmPointer).ToList();
            apiList.Add(InputApi.WpfStylus);
            _apis = apiList;

            foreach (var api in _apis)
            {
                string name = api switch
                {
                    InputApi.WintabSystem => "Wintab",
                    InputApi.WintabDigitizer => "Wintab (high-res)",
                    InputApi.WpfStylus => "WPF Stylus",
                    _ => api.ToString()
                };
                ApiCombo.Items.Add(name);
            }

            if (ApiCombo.Items.Count > 0)
                ApiCombo.SelectedIndex = 0;
        };

        Closing += (_, _) =>
        {
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
        int w = (int)CanvasArea.ActualWidth;
        int h = (int)CanvasArea.ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_skBitmap != null && _bitmapWidth == w && _bitmapHeight == h) return;

        var oldBitmap = _skBitmap;
        var oldCanvas = _skCanvas;

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);
        _bitmapWidth = w;
        _bitmapHeight = h;

        // Clear to background.
        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        // Copy old content if resizing.
        if (oldBitmap != null)
        {
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        // Create WPF bitmap for display.
        _wpfBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
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

        Title = "Scribble WPF - PenSession";
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

        int maxP = _session.MaxPressure;
        bool drew = false;

        foreach (var pt in points)
        {
            Point canvasPt;
            try
            {
                canvasPt = CanvasArea.PointFromScreen(new Point(pt.DesktopX, pt.DesktopY));
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
                float width = (float)pt.Pressure / maxP * (float)_brushSize + 0.5f;

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

        RawPosLabel.Text = $"Raw: {last.RawX},{last.RawY}";
        ScreenPosLabel.Text = $"Screen: {last.DesktopX:F0},{last.DesktopY:F0}";

        // App = position relative to the window client area
        Point appPt;
        try { appPt = this.PointFromScreen(new Point(last.DesktopX, last.DesktopY)); }
        catch { appPt = new Point(); }
        AppPosLabel.Text = $"App: {appPt.X:F0},{appPt.Y:F0}";

        // Canvas = position relative to the drawing surface
        Point lastCanvas;
        try { lastCanvas = CanvasArea.PointFromScreen(new Point(last.DesktopX, last.DesktopY)); }
        catch { lastCanvas = new Point(); }
        CanvasPosLabel.Text = $"Canvas: {lastCanvas.X:F1},{lastCanvas.Y:F1}";

        float pct = maxP > 0 ? (float)last.Pressure / maxP * 100f : 0f;
        RawPressureLabel.Text = $"Raw: {last.Pressure}";
        NormPressureLabel.Text = $"Norm: {pct:F1}%";

        AzimuthLabel.Text = $"Azimuth: {last.Azimuth:F1}";
        AltitudeLabel.Text = $"Altitude: {last.Altitude:F1}";
        TwistLabel.Text = $"Twist: {last.Twist:F1}";
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
