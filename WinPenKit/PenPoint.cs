namespace WinPenKit;

/// <summary>
/// A framework-neutral pen data point produced by an <see cref="IPenSession"/>.
/// Contains the pen's desktop position (in physical screen pixels with
/// sub-pixel precision), raw tablet values, pressure, orientation, and
/// button state.
///
/// <para>This is raw pen data — not brush output. The consumer decides
/// how to interpret pressure (stroke width, opacity, etc.), orientation
/// (calligraphy angle, airbrush direction), and buttons (tool switching,
/// modifier keys).</para>
///
/// <para><b>DesktopX/Y:</b> Physical screen pixel coordinates as
/// <c>double</c>. In system mode, these are integer screen pixels cast
/// to double. In digitizer hi-res mode, these are computed via ScaleAxis
/// from the tablet's native coordinate range, preserving sub-pixel
/// precision. The consumer converts to canvas-local coordinates using
/// framework-specific methods.</para>
/// </summary>
public readonly record struct PenPoint(
    /// <summary>Desktop X in physical screen pixels (double for sub-pixel precision).</summary>
    double DesktopX,

    /// <summary>Desktop Y in physical screen pixels (double for sub-pixel precision).</summary>
    double DesktopY,

    /// <summary>Raw X from the input API, in whatever units that API reports natively.</summary>
    /// <remarks>
    /// <para>The unit differs per backend, and there is no field saying which one you have:</para>
    /// <list type="bullet">
    /// <item><description>Wintab digitizer context: tablet-native units</description></item>
    /// <item><description>Wintab system context: screen pixels, already mapped by the driver</description></item>
    /// <item><description>WM_POINTER: hundredths of a millimetre, from <c>ptHimetricLocationRaw</c></description></item>
    /// <item><description>Avalonia, WPF stylus and WinUI: <see cref="DesktopX"/> truncated to an
    /// <c>int</c>, because those frameworks expose no device-native coordinate</description></item>
    /// </list>
    /// <para>Treat this as a diagnostic rather than as a position. Sane values here against a
    /// wrong <see cref="DesktopX"/> point at the mapping; both wrong points upstream of it. The
    /// last case above carries no information <see cref="DesktopX"/> does not already carry,
    /// which is tracked in issue 24.</para>
    /// </remarks>
    int RawX,

    /// <summary>Raw Y from the input API. See <see cref="RawX"/> for the units.</summary>
    int RawY,

    /// <summary>Raw pen tip pressure (0 to <see cref="IPenSession.MaxPressure"/>).</summary>
    uint Pressure,

    /// <summary>Pen azimuth (compass direction) in degrees (0.0–360.0).
    /// APIs that report TiltX/TiltY are converted to azimuth/altitude.</summary>
    double Azimuth,

    /// <summary>Pen altitude (angle from tablet surface) in degrees (0.0–90.0).</summary>
    double Altitude,

    /// <summary>Pen barrel twist in degrees (0.0–360.0). 0 if unsupported.</summary>
    double Twist,

    /// <summary>Planar tilt X in degrees (-90.0 to +90.0). Positive = tilt right.
    /// Computed from Azimuth/Altitude for Wintab; native for WM_POINTER.</summary>
    double TiltX,

    /// <summary>Planar tilt Y in degrees (-90.0 to +90.0). Positive = tilt toward user.
    /// Computed from Azimuth/Altitude for Wintab; native for WM_POINTER.</summary>
    double TiltY,

    /// <summary>Height above the tablet surface. 0 if unsupported.</summary>
    int Z,

    /// <summary>Packet status flags.</summary>
    uint Status,

    /// <summary>Button state (encoding is API-specific; use helper properties).</summary>
    uint Buttons,

    /// <summary>Cursor type identifier (pen, eraser, puck).</summary>
    uint Cursor,

    /// <summary>Which input API produced this point.</summary>
    InputApi Source,

    /// <summary>
    /// When the point was produced, in microseconds. Subtract two of these; do not read one
    /// on its own.
    /// </summary>
    /// <remarks>
    /// <para>The origin is deliberately unstated. Every backend counts from a different
    /// place, and no two of them are comparable, so the only contract this field offers is
    /// that values from one running session never decrease and their difference is elapsed
    /// microseconds. Never decrease, not increase: a backend whose clock is coarser than its
    /// report rate gives consecutive points the same value, so a difference of zero is a
    /// normal reading and any consumer dividing by one has to expect it. That is what sampling rate, velocity and any time-based smoothing
    /// actually need.</para>
    /// <para>It is not a high-resolution clock on most backends. The unit is microseconds
    /// everywhere so that arithmetic is uniform, but only WM_POINTER resolves finer than a
    /// millisecond -- and WPF is coarser than that, repeating a value across consecutive
    /// points. <see cref="PenConventions.Timestamp"/> names the clock and carries the
    /// measured resolution per backend.</para>
    /// <para>Zero when <see cref="PenConventions.Timestamp"/> is
    /// <see cref="PenTimestampSource.None"/>. Zero is not a time; it means the backend
    /// supplied nothing. The session does not substitute its own clock, because that would
    /// measure when this library got around to reading the packet.</para>
    /// </remarks>
    long TimestampMicroseconds)
{
    // These five decode the Wintab encoding -- (action << 16) | buttonNumber -- and nothing
    // here knows whether that is the encoding in hand. The five pointer backends set only
    // bits 0 and 1, so Buttons >> 16 is always 0, ButtonAction is always None, and all three
    // predicates are always false on those backends. False, not "cannot say".
    //
    // PenButtonTracker branches on PenPoint.Source and decodes both encodings, which is why
    // it is the supported way to read buttons and these are not. They had no callers in any
    // repository when this attribute was added.

    /// <summary>
    /// The button action from the high word: Pressed, Released, or None.
    /// </summary>
    [Obsolete("Decodes the Wintab button encoding unconditionally, so it is always false on the five pointer backends. Use PenButtonTracker, which decodes per backend.")]
    public PenButtonAction ButtonAction => (PenButtonAction)(Buttons >> 16);

    /// <summary>
    /// The button number from the low word (0 = tip, 1 = barrel 1, etc.).
    /// Only meaningful when the action is not <see cref="PenButtonAction.None"/>.
    /// </summary>
    [Obsolete("Decodes the Wintab button encoding unconditionally, so it is always false on the five pointer backends. Use PenButtonTracker, which decodes per backend.")]
    public int ButtonNumber => (int)(Buttons & 0xFFFF);

    /// <summary>
    /// Returns true if the specified button was just pressed in this packet.
    /// </summary>
    [Obsolete("Decodes the Wintab button encoding unconditionally, so it is always false on the five pointer backends. Use PenButtonTracker, which decodes per backend.")]
    public bool IsButtonPressed(int buttonNumber) =>
        (Buttons >> 16) == (uint)PenButtonAction.Pressed
        && (Buttons & 0xFFFF) == (uint)buttonNumber;

    /// <summary>
    /// Returns true if the specified button was just released in this packet.
    /// </summary>
    [Obsolete("Decodes the Wintab button encoding unconditionally, so it is always false on the five pointer backends. Use PenButtonTracker, which decodes per backend.")]
    public bool IsButtonReleased(int buttonNumber) =>
        (Buttons >> 16) == (uint)PenButtonAction.Released
        && (Buttons & 0xFFFF) == (uint)buttonNumber;

    /// <summary>
    /// Returns true if the pen tip is being pressed in this packet.
    /// For continuous tip-down detection, check <see cref="Pressure"/> &gt; 0 instead.
    /// </summary>
    [Obsolete("Decodes the Wintab button encoding unconditionally, so it is always false on the five pointer backends. Use PenButtonTracker, which decodes per backend.")]
    public bool IsTipPressed =>
        (Buttons >> 16) == (uint)PenButtonAction.Pressed
        && (Buttons & 0xFFFF) == PenButtonNumber.Tip;

    /// <summary>
    /// Returns true if the eraser cursor is active. This is true whenever
    /// the eraser end is in proximity — even before touching the surface.
    /// </summary>
    public bool IsEraser => Cursor == PenCursorType.Eraser;

    /// <summary>
    /// Returns true if the pen is in proximity of the tablet surface.
    /// </summary>
    /// <remarks>
    /// Only meaningful when the session advertises
    /// <see cref="PenCapabilities.Proximity"/>. Without it this reads a bit nothing sets, so
    /// it is false on every point -- including the hover points the pointer backends do
    /// produce -- and false means "not reported" rather than "not in proximity".
    /// </remarks>
    public bool IsInProximity => (Status & 0x0001) != 0;
}
