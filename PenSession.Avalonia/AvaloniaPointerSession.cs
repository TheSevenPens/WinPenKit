using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace PenSession.Avalonia;

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

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024;
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[Avalonia Pointer] Element={_element.GetType().Name}";

    public AvaloniaPointerSession(Control element)
    {
        _element = element;
    }

    public string? Start(IntPtr appWindowHandle = default)
    {
        _element.PointerMoved += OnPointerEvent;
        _element.PointerPressed += OnPointerEvent;
        _element.PointerReleased += OnPointerEvent;
        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        _element.PointerMoved -= OnPointerEvent;
        _element.PointerPressed -= OnPointerEvent;
        _element.PointerReleased -= OnPointerEvent;
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

            // Convert window-relative to screen pixels.
            var screenPt = topLevel.PointToScreen(elementPos.Value);
            desktopX = screenPt.X;
            desktopY = screenPt.Y;
        }
        catch
        {
            return;
        }

        // Pressure: 0.0–1.0 → 0–1024.
        uint pressure = (uint)(props.Pressure * 1024f);

        // Tilt: Avalonia provides XTilt/YTilt as float, not nullable in all versions.
        int tiltX = 0, tiltY = 0;
        try
        {
            tiltX = (int)(props.XTilt * 10);
            tiltY = (int)(props.YTilt * 10);
        }
        catch { }

        TiltToSpherical(tiltX, tiltY, out int azimuth, out int altitude);

        int twist = 0;
        try { twist = (int)(props.Twist * 10); }
        catch { }

        uint buttons = 0;
        if (props.IsBarrelButtonPressed) buttons |= 0x0001;
        if (props.IsEraser) buttons |= 0x0002;

        uint cursor = props.IsEraser ? PenCursorType.Eraser : PenCursorType.PenTip;

        _points.Enqueue(new PenPoint(
            DesktopX: desktopX,
            DesktopY: desktopY,
            RawX: (int)desktopX,
            RawY: (int)desktopY,
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
            Source: InputApi.AvaloniaPointer));

        _hasNewData = true;
    }

    private static void TiltToSpherical(int tiltX, int tiltY,
        out int azimuth, out int altitude)
    {
        double tx = tiltX;
        double ty = tiltY;
        double mag = Math.Sqrt(tx * tx + ty * ty);

        altitude = Math.Clamp((int)(900.0 - mag), 0, 900);

        if (mag > 5.0)
        {
            double rad = Math.Atan2(-tx, ty);
            int tenths = (int)(rad * 1800.0 / Math.PI);
            azimuth = ((tenths % 3600) + 3600) % 3600;
        }
        else
        {
            azimuth = 0;
        }
    }
}
