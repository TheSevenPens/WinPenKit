using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;

namespace WinPenKit.Wpf;

/// <summary>
/// WPF stylus input session. Attaches to a WPF <see cref="UIElement"/>'s
/// stylus events (StylusMove, StylusDown, StylusUp) and produces
/// <see cref="PenPoint"/> records in desktop screen-pixel coordinates.
///
/// <para>Lives in WinPenKit.Wpf (not WinPenKit) because it depends on
/// WPF types. Implements <see cref="IPenSession"/> so apps can swap
/// between Wintab and WPF stylus input at runtime.</para>
///
/// <para>WPF's stylus input comes through its own Wisp/RealTimeStylus
/// path (or WM_POINTER when enabled). This bypasses Win32 WM_POINTER
/// messages, which is why the framework-agnostic WmPointerSession
/// doesn't work in WPF apps.</para>
/// </summary>
public sealed class WpfStylusSession : IPenSession
{
    private readonly UIElement _element;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;

    // ── IPenSession ──────────────────────────────────────────────

    public InputApi Api => InputApi.WpfStylus;

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

    public int MaxPressure => 1024; // PressureFactor 0.0–1.0 → scaled to 0–1024
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[WPF Stylus] Element={_element.GetType().Name}";
    public IPenCaptureRegion? CaptureRegion { get; set; }

    /// <summary>
    /// Creates a WPF stylus session targeting a specific element.
    /// </summary>
    /// <param name="element">The UIElement to attach stylus events to (typically the canvas).</param>
    public WpfStylusSession(UIElement element)
    {
        _element = element;
    }

    public string? Start(IntPtr appWindowHandle = default)
    {
        _element.StylusMove += OnStylusEvent;
        _element.StylusDown += OnStylusEvent;
        _element.StylusUp += OnStylusEvent;
        _element.StylusInAirMove += OnStylusEvent;
        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        _element.StylusMove -= OnStylusEvent;
        _element.StylusDown -= OnStylusEvent;
        _element.StylusUp -= OnStylusEvent;
        _element.StylusInAirMove -= OnStylusEvent;
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

    // ── Event handler ────────────────────────────────────────────

    private void OnStylusEvent(object sender, StylusEventArgs e)
    {
        // Only handle pen input, not touch.
        if (e.StylusDevice?.TabletDevice?.Type != TabletDeviceType.Stylus)
            return;

        var stylusPoints = e.GetStylusPoints(_element);
        bool isEraser = e.Inverted;

        // Once per event, not once per point: the element has not moved between them.
        // Deliberately not Visual.PointToScreen - see WpfCoordinates. GetStylusPoints hands
        // over good sub-pixel DIPs and PointToScreen truncates every one of them to a whole
        // device pixel, which is the entire difference between a smooth stroke and a faceted
        // one.
        if (WpfCoordinates.GetTransform(_element) is not { } xf)
            return;

        foreach (var sp in stylusPoints)
        {
            // Convert element-relative DIPs to desktop screen pixels.
            var screenPt = new Point(
                xf.OriginX + sp.X * xf.ScaleX,
                xf.OriginY + sp.Y * xf.ScaleY);

            // Spatial scope: drop points outside an explicit capture region.
            if (CaptureRegion is { } region && !region.Contains(screenPt.X, screenPt.Y))
                continue;

            // Pressure: 0.0–1.0 → 0–1024.
            uint pressure = (uint)(sp.PressureFactor * 1024f);

            // Tilt: try to read XTiltOrientation / YTiltOrientation.
            // WPF reports in hundredths of degree (-9000 to +9000).
            // We need degrees (-90 to +90).
            double tiltX = 0, tiltY = 0;
            if (sp.HasProperty(StylusPointProperties.XTiltOrientation))
                tiltX = sp.GetPropertyValue(StylusPointProperties.XTiltOrientation) / 100.0;
            if (sp.HasProperty(StylusPointProperties.YTiltOrientation))
                tiltY = sp.GetPropertyValue(StylusPointProperties.YTiltOrientation) / 100.0;

            // Convert to spherical.
            TiltToSpherical(tiltX, tiltY, out double azimuth, out double altitude);

            // Twist in degrees.
            double twist = 0;
            if (sp.HasProperty(StylusPointProperties.TwistOrientation))
                twist = sp.GetPropertyValue(StylusPointProperties.TwistOrientation) / 100.0;

            // Buttons.
            //
            // Bit 0 is barrel in the encoding PenButtonTracker reads. This used to be set for
            // every button that was down, with no filter: StylusButtons includes the tip
            // switch, so a plain tip stroke lit the barrel indicator for its whole length,
            // where WinUI, Avalonia and WM_POINTER all read a dedicated barrel flag and
            // reported nothing.
            //
            // The tip switch is excluded by identity; anything else that is down counts as
            // barrel. Filtering to StylusPointProperties.BarrelButton alone would be stricter,
            // but devices differ in which property they report a side switch under, and a
            // stricter filter risks dropping a real barrel press on hardware that cannot be
            // tested here. Losing a barrel press is a worse failure than the one being fixed.
            //
            // Two side switches still collapse onto one bit. That is the encoding's ceiling,
            // already documented in HOW_TO_USE.md, and not something this filter can lift.
            uint buttons = 0;
            foreach (var btn in e.StylusDevice.StylusButtons)
            {
                if (btn.StylusButtonState != StylusButtonState.Down) continue;
                if (btn.Guid == StylusPointProperties.TipButton.Id) continue;
                buttons |= 0x0001;
            }
            if (isEraser) buttons |= 0x0002;

            // Cursor type.
            uint cursor = isEraser ? PenCursorType.Eraser : PenCursorType.PenTip;

            _points.Enqueue(new PenPoint(
                DesktopX: screenPt.X,
                DesktopY: screenPt.Y,
                // Zero, with Conventions.RawUnits reporting None. This used to be
                // (int)screenPt.X, which reads as a device-native value and is DesktopX with
                // its fraction dropped.
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
                Source: InputApi.WpfStylus,
                // Milliseconds on the GetTickCount epoch, measured on 12 Sep 2026 -- WPF
                // documents no unit either. Drawn on by hand, the clock does resolve to the
                // millisecond: sixteen gaps of exactly 1000us across one stroke. What repeats a
                // value is the batching, not the clock -- one timestamp per StylusEventArgs, so
                // 2442 points carried 885 of them. See PenTimestampSource.SystemTicks.
                TimestampMicroseconds: PenTimestamp.FromSystemTicks(e.Timestamp)));

            _hasNewData = true;
        }
    }

    // ── Tilt conversion ──────────────────────────────────────────

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
