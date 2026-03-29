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

        foreach (var seg in segments)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = seg.Width,
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

    // ── Bitmap management ────────────────────────────────────────

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

        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        if (oldBitmap != null)
        {
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        _wbBitmap = new WriteableBitmap(w, h);
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
