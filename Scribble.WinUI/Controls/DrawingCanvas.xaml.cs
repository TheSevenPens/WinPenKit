using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using SkiaSharp;

namespace Scribble.WinUI.Controls;

/// <summary>
/// Bitmap-backed drawing surface using SkiaSharp.
/// Accepts line segments and renders them to an offscreen <see cref="SKBitmap"/>,
/// displayed via a <see cref="WriteableBitmap"/> in an <see cref="Image"/> element.
///
/// <para>Standardized on SkiaSharp across all managed scribble apps
/// (WPF, WinUI 3, Avalonia) for consistent rendering behavior.</para>
/// </summary>
public sealed partial class DrawingCanvas : UserControl
{
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private WriteableBitmap? _wbBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private readonly List<StrokeSegment> _pendingSegments = [];
    private volatile bool _dirty;

    private readonly record struct StrokeSegment(
        float X1, float Y1, float X2, float Y2, float Width);

    public DrawingCanvas()
    {
        this.InitializeComponent();

        CanvasArea.SizeChanged += (_, _) => EnsureBitmap();
    }

    /// <summary>
    /// Returns the canvas origin relative to the window's XAML content
    /// root, in DIPs.
    /// </summary>
    public Point GetPositionInWindow()
    {
        return TransformToVisual(null).TransformPoint(new Point(0, 0));
    }

    /// <summary>
    /// Queues a stroke segment for rendering. Thread-safe.
    /// </summary>
    public void QueueStroke(float x1, float y1, float x2, float y2, float width)
    {
        lock (_pendingSegments)
        {
            _pendingSegments.Add(new StrokeSegment(x1, y1, x2, y2, width));
        }
        _dirty = true;
    }

    /// <summary>
    /// Queues a line segment for rendering. Thread-safe.
    /// Accepts a XAML <see cref="Line"/> for API compatibility.
    /// </summary>
    public void QueueLine(Line line)
    {
        QueueStroke(
            (float)line.X1, (float)line.Y1,
            (float)line.X2, (float)line.Y2,
            (float)line.StrokeThickness);
    }

    /// <summary>
    /// Commits all queued segments to the bitmap. Must be called on the UI thread.
    /// </summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;

        StrokeSegment[] segments;
        lock (_pendingSegments)
        {
            segments = [.. _pendingSegments];
            _pendingSegments.Clear();
        }

        if (segments.Length == 0 || _skCanvas == null) return;

        // Widths arrive as a count of physical pixels, matching the other samples. The canvas
        // carries Scale(scale) so that stroke coordinates can stay in effective pixels, which
        // would multiply the width too - divide it back out here, the one place the scale is
        // known.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        foreach (var seg in segments)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = (float)(seg.Width / scale),
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            _skCanvas.DrawLine(seg.X1, seg.Y1, seg.X2, seg.Y2, paint);
        }

        CopyToWriteableBitmap();
    }

    /// <summary>
    /// Clears all strokes. Must be called on the UI thread.
    /// </summary>
    public void Clear()
    {
        lock (_pendingSegments)
        {
            _pendingSegments.Clear();
        }

        _skCanvas?.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        CopyToWriteableBitmap();
    }

    /// <summary>Backing bitmap size in device pixels.</summary>
    internal (int Width, int Height) BitmapSize => (_bitmapWidth, _bitmapHeight);

    /// <summary>Canvas size in XAML's effective pixels, and the scale relating them to
    /// device pixels.</summary>
    internal (double Width, double Height, double Scale) CanvasLogicalSize =>
        (CanvasArea.ActualWidth, CanvasArea.ActualHeight, XamlRoot?.RasterizationScale ?? 1.0);

    /// <summary>Size the image actually reaches the screen at, in device pixels.</summary>
    internal (double Width, double Height) PresentedDeviceSize
    {
        get
        {
            double s = XamlRoot?.RasterizationScale ?? 1.0;
            return (DrawImage.ActualWidth * s, DrawImage.ActualHeight * s);
        }
    }

    // ── Bitmap management ────────────────────────────────────────

    private void EnsureBitmap()
    {
        // Physical pixels, not effective pixels. ActualWidth is in XAML's effective pixels,
        // and a bitmap sized from it is magnified by the rasterization scale on its way to
        // the screen - at 2.25x that is a canvas drawn at 44% of the display's resolution.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Ceiling(CanvasArea.ActualWidth * scale);
        int h = (int)Math.Ceiling(CanvasArea.ActualHeight * scale);
        if (w <= 0 || h <= 0) return;
        if (_skBitmap != null && _bitmapWidth == w && _bitmapHeight == h) return;

        var oldBitmap = _skBitmap;
        var oldCanvas = _skCanvas;

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);

        // Stroke coordinates arrive in effective pixels, so the transform is the only thing
        // that knows the surface is physical. Everything downstream keeps working in the
        // units it already used.
        _skCanvas.Scale((float)scale);

        _bitmapWidth = w;
        _bitmapHeight = h;

        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        if (oldBitmap != null)
        {
            // Old pixels are already physical, so the scale comes off for the blit or the
            // preserved content grows by the scale factor on every resize.
            _skCanvas.Save();
            _skCanvas.ResetMatrix();
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            _skCanvas.Restore();
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        _wbBitmap = new WriteableBitmap(w, h);

        // WinUI has no per-bitmap dpi, so the image is sized explicitly in effective pixels:
        // w physical px shown across w/scale epx is exactly one texel per device pixel.
        // Left at its natural size it would occupy w epx and be magnified by the scale.
        DrawImage.Width = w / scale;
        DrawImage.Height = h / scale;

        CopyToWriteableBitmap();
        DrawImage.Source = _wbBitmap;
    }

    private void CopyToWriteableBitmap()
    {
        if (_skBitmap == null || _wbBitmap == null) return;

        using var stream = _wbBitmap.PixelBuffer.AsStream();
        var pixels = _skBitmap.GetPixelSpan();
        stream.Write(pixels);
        _wbBitmap.Invalidate();
    }
}
