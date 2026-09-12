using Avalonia;
using Avalonia.Controls;

namespace WinPenKit.Avalonia;

/// <summary>
/// An <see cref="IPenCaptureRegion"/> backed by an Avalonia <see cref="Control"/>'s
/// live on-screen bounds. Use it to scope <i>any</i> backend (including Wintab)
/// to a specific control, so the consumer's experience is identical regardless
/// of which input API is active.
///
/// <para><b>Threading:</b> the control's screen rectangle is recomputed on the
/// UI thread (on layout updates and window moves) and cached. <see cref="Contains"/>
/// only reads the cached rectangle, so it is safe to call from a Wintab capture
/// thread — it never touches Avalonia visuals off the UI thread.</para>
///
/// <para>The window origin is projected through <see cref="TopLevel.PointToScreen"/> and
/// the control's offsets are scaled by hand, rather than projecting both corners. Construct
/// on the UI thread and <see cref="Dispose"/> when the session stops.</para>
/// </summary>
public sealed class ControlCaptureRegion : IPenCaptureRegion, IDisposable
{
    private readonly Control _control;
    private readonly EventHandler<PixelPointEventArgs> _onWindowMoved;

    // Cached screen rectangle. Reference reads/writes are atomic; null means
    // "not computed yet" → Contains fails open rather than dropping all input.
    private volatile ScreenBox? _box;
    private WindowBase? _window;
    private bool _disposed;

    public ControlCaptureRegion(Control control)
    {
        _control = control;
        _onWindowMoved = (_, _) => Refresh();
        _control.LayoutUpdated += OnLayoutUpdated;
        Refresh(); // also binds the window for move tracking
    }

    public bool Contains(double desktopX, double desktopY)
    {
        var box = _box;
        if (box is null) return true; // bounds unknown — don't over-filter
        return desktopX >= box.Left && desktopX < box.Right
            && desktopY >= box.Top && desktopY < box.Bottom;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _control.LayoutUpdated -= OnLayoutUpdated;
        UnbindWindow();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

    /// <summary>A control that is not on screen contains nothing. Zero area, so
    /// <see cref="Contains"/> is false everywhere.</summary>
    private static readonly ScreenBox Offscreen = new(0, 0, 0, 0);

    /// <summary>Recomputes the cached screen rectangle. UI thread only.</summary>
    private void Refresh()
    {
        BindWindow();

        // No top level is "not attached yet", which is unknown rather than offscreen. Leave
        // the cached box alone so Contains keeps failing open.
        var topLevel = TopLevel.GetTopLevel(_control);
        if (topLevel is null) return;

        // Hidden is known, not unknown. Returning here without touching _box left the last
        // visible rectangle in place, so Wintab went on reporting points over the area the
        // control used to occupy while the Avalonia session, which stops producing events,
        // reported none.
        if (!_control.IsVisible)
        {
            _box = Offscreen;
            return;
        }

        var bounds = _control.Bounds;
        var topLeft     = _control.TranslatePoint(new Point(0, 0), topLevel);
        var bottomRight = _control.TranslatePoint(new Point(bounds.Width, bounds.Height), topLevel);
        if (topLeft is null || bottomRight is null) return;

        // One PointToScreen, for the window origin only. It takes a PixelPoint, so projecting
        // the far corner through it truncated the right and bottom bounds inward; Contains
        // tests desktopX < Right, so a point on a fractional edge -- ordinary at 1.25x and
        // 1.75x -- was dropped here while AvaloniaPointerSession, which avoids PointToScreen
        // for this very reason, reported it. The two disagreed about whether the pen was over
        // the canvas.
        //
        // The window origin is on a whole device pixel, so taking that through PixelPoint
        // loses nothing. The offsets within the window are scaled in doubles.
        var windowOrigin = topLevel.PointToScreen(new Point(0, 0));
        double scale = topLevel.RenderScaling;

        double left   = windowOrigin.X + topLeft.Value.X * scale;
        double top    = windowOrigin.Y + topLeft.Value.Y * scale;
        double right  = windowOrigin.X + bottomRight.Value.X * scale;
        double bottom = windowOrigin.Y + bottomRight.Value.Y * scale;

        _box = new ScreenBox(
            Math.Min(left, right), Math.Min(top, bottom),
            Math.Max(left, right), Math.Max(top, bottom));
    }

    private void BindWindow()
    {
        var window = TopLevel.GetTopLevel(_control) as WindowBase;
        if (ReferenceEquals(window, _window)) return;
        UnbindWindow();
        _window = window;
        if (_window is not null)
            _window.PositionChanged += _onWindowMoved;
    }

    private void UnbindWindow()
    {
        if (_window is not null)
            _window.PositionChanged -= _onWindowMoved;
        _window = null;
    }

    private sealed record ScreenBox(double Left, double Top, double Right, double Bottom);
}
