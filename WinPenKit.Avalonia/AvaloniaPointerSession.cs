using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace WinPenKit.Avalonia;

/// <summary>
/// Avalonia pointer input session. Attaches to an Avalonia control's
/// pointer events and produces <see cref="PenPoint"/> records in desktop
/// screen-pixel coordinates.
/// </summary>
public sealed class AvaloniaPointerSession : IPenSession
{
    private readonly Control _element;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;

    public InputApi Api => InputApi.AvaloniaPointer;

    /// <summary>
    /// This framework exposes no device-native coordinate, so the raw fields carry nothing
    /// and are written as zero. Reporting DesktopX truncated would look like a second
    /// measurement and be the same one with its fraction removed.
    /// </summary>
    public PenConventions Conventions => new(
        PenRawUnits.None,
        PenButtonEncoding.PointerFlags,
        PenCursorNumbering.Normalised,
        PenTimestampSource.SystemTicks);

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024;
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[Avalonia Pointer] Element={_element.GetType().Name}";
    public IPenCaptureRegion? CaptureRegion { get; set; }

    public AvaloniaPointerSession(Control element)
    {
        _element = element;
    }

    public string? Start(IntPtr appWindowHandle = default)
    {
        // Use Tunnel routing so events are received at this element before any
        // child control can mark them Handled and suppress bubbling. This ensures
        // AvaloniaPointerSession works correctly when attached to a root/container
        // element that hosts interactive children (TextBox, Button, etc.).
        _element.AddHandler(InputElement.PointerMovedEvent,    OnPointerEvent, RoutingStrategies.Tunnel);
        _element.AddHandler(InputElement.PointerPressedEvent,  OnPointerEvent, RoutingStrategies.Tunnel);
        _element.AddHandler(InputElement.PointerReleasedEvent, OnPointerEvent, RoutingStrategies.Tunnel);
        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        _element.RemoveHandler(InputElement.PointerMovedEvent,    OnPointerEvent);
        _element.RemoveHandler(InputElement.PointerPressedEvent,  OnPointerEvent);
        _element.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerEvent);
        IsRunning = false;
    }

    public PenPoint[] DrainPoints()
    {
        _hasNewData = false;
        var list = new List<PenPoint>();
        while (_points.TryDequeue(out var pt))
            list.Add(pt);
        return [.. list];
    }

    public int DrainPoints(Span<PenPoint> buffer)
    {
        _hasNewData = false;
        int count = 0;
        while (count < buffer.Length && _points.TryDequeue(out var pt))
            buffer[count++] = pt;

        // A buffer smaller than the queue leaves points behind. Clearing the flag and
        // stopping there told a caller that polls HasNewData the queue was empty when it was
        // not, and if the pen had lifted nothing would set it again: those points waited for
        // a drain that happened for some other reason.
        //
        // Only ever set true here, never false. The false above still happens before the
        // drain, so a point enqueued by the pen thread mid-drain sets the flag itself and
        // this cannot overwrite it.
        if (!_points.IsEmpty) _hasNewData = true;

        return count;
    }

    public void RefreshMapping() { }
    public void Dispose() => Stop();

    private void OnPointerEvent(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(_element);

        // Only handle pen input.
        if (point.Pointer.Type != PointerType.Pen)
            return;

        var props = point.Properties;

        // Convert element-relative coords to screen pixels.
        double desktopX = 0, desktopY = 0;
        try
        {
            var topLevel = TopLevel.GetTopLevel(_element);
            if (topLevel == null) return;

            // Get element position in window.
            var elementPos = _element.TranslatePoint(point.Position, topLevel);
            if (elementPos == null) return;

            // Window-relative DIPs to screen pixels, by hand.
            //
            // PointToScreen(Point) returns a PixelPoint, whose members are integers - so using it
            // here would throw away the sub-pixel position Avalonia just went to the trouble of
            // providing. Avalonia reads ptHimetricLocationRaw and maps it through
            // GetPointerDeviceRects, falling back to whole pixels only where that API is missing,
            // so point.Position genuinely carries a fraction on Windows.
            //
            // Scaling only the offset preserves it: the window origin is genuinely on a pixel
            // boundary, so taking that as an integer costs nothing.
            var windowOrigin = topLevel.PointToScreen(new Point(0, 0));
            double scale = topLevel.RenderScaling;
            desktopX = windowOrigin.X + elementPos.Value.X * scale;
            desktopY = windowOrigin.Y + elementPos.Value.Y * scale;
        }
        catch
        {
            return;
        }

        // Spatial scope: drop points outside an explicit capture region. (With
        // no region this session is already scoped to its attached control.)
        if (CaptureRegion is { } region && !region.Contains(desktopX, desktopY))
            return;

        // Pressure: 0.0–1.0 → 0–1024.
        uint pressure = (uint)(props.Pressure * 1024f);

        // Tilt: Avalonia provides XTilt/YTilt as float, not nullable in all versions.
        double tiltX = 0, tiltY = 0;
        try
        {
            tiltX = props.XTilt;
            tiltY = props.YTilt;
        }
        catch { }

        TiltToSpherical(tiltX, tiltY, out double azimuth, out double altitude);

        double twist = 0;
        try { twist = props.Twist; }
        catch { }

        uint buttons = 0;
        if (props.IsBarrelButtonPressed) buttons |= 0x0001;
        if (props.IsEraser) buttons |= 0x0002;

        uint cursor = props.IsEraser ? PenCursorType.Eraser : PenCursorType.PenTip;

        _points.Enqueue(new PenPoint(
            DesktopX: desktopX,
            DesktopY: desktopY,
            // Zero, with Conventions.RawUnits reporting None. This used to be
            // (int)desktopX, which reads as a device-native value and is DesktopX with its
            // fraction dropped.
            RawX: 0,
            RawY: 0,
            Pressure: pressure,
            Azimuth: azimuth,
            Altitude: altitude,
            Twist: twist,
            TiltX: tiltX,
            TiltY: tiltY,
            Z: 0,
            Status: 0,
            Buttons: buttons,
            Cursor: cursor,
            Source: InputApi.AvaloniaPointer,
            // Avalonia documents this only as "the time when the input occurred" and states
            // no unit. Measured against GetTickCount64 and found to track it within a
            // millisecond, so it is milliseconds on that epoch.
            //
            // FromSystemTicks, not FromMilliseconds, even though the property is a ulong. On
            // Windows Avalonia fills it from GetMessageTime, which is 32 bits, and widens the
            // result -- so the value has already wrapped by the time it is a ulong and the
            // width of the property says nothing about the width of the clock.
            TimestampMicroseconds: PenTimestamp.FromSystemTicks((long)e.Timestamp)));

        _hasNewData = true;
    }

    private static void TiltToSpherical(double tiltX, double tiltY,
        out double azimuth, out double altitude)
    {
        double mag = Math.Sqrt(tiltX * tiltX + tiltY * tiltY);

        altitude = Math.Clamp(90.0 - mag, 0.0, 90.0);

        if (mag > 0.5)
        {
            double rad = Math.Atan2(-tiltX, tiltY);
            double deg = rad * 180.0 / Math.PI;
            azimuth = ((deg % 360.0) + 360.0) % 360.0;
        }
        else
        {
            azimuth = 0.0;
        }
    }
}
