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
/// <para>Both corners are projected through <see cref="TopLevel.PointToScreen"/>,
/// so the rectangle is DPI-correct without manual scaling. Construct on the UI
/// thread and <see cref="Dispose"/> when the session stops.</para>
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

    /// <summary>Recomputes the cached screen rectangle. UI thread only.</summary>
    private void Refresh()
    {
        BindWindow();

        var topLevel = TopLevel.GetTopLevel(_control);
        if (topLevel is null || !_control.IsVisible) return;

        var bounds = _control.Bounds;
        var topLeft     = _control.TranslatePoint(new Point(0, 0), topLevel);
        var bottomRight = _control.TranslatePoint(new Point(bounds.Width, bounds.Height), topLevel);
        if (topLeft is null || bottomRight is null) return;

        var p1 = topLevel.PointToScreen(topLeft.Value);
        var p2 = topLevel.PointToScreen(bottomRight.Value);

        _box = new ScreenBox(
            Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
            Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
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
