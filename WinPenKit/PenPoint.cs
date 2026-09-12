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
    InputApi Source)
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
    public bool IsInProximity => (Status & 0x0001) != 0;
}
