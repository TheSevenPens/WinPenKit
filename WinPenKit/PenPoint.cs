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

    /// <summary>Raw X from the input API (tablet-native in digitizer mode, screen pixels in system mode).</summary>
    int RawX,

    /// <summary>Raw Y from the input API.</summary>
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
    /// <summary>
    /// The button action from the high word: Pressed, Released, or None.
    /// </summary>
    public PenButtonAction ButtonAction => (PenButtonAction)(Buttons >> 16);

    /// <summary>
    /// The button number from the low word (0 = tip, 1 = barrel 1, etc.).
    /// Only meaningful when <see cref="ButtonAction"/> is not <see cref="PenButtonAction.None"/>.
    /// </summary>
    public int ButtonNumber => (int)(Buttons & 0xFFFF);

    /// <summary>
    /// Returns true if the specified button was just pressed in this packet.
    /// </summary>
    public bool IsButtonPressed(int buttonNumber) =>
        ButtonAction == PenButtonAction.Pressed && ButtonNumber == buttonNumber;

    /// <summary>
    /// Returns true if the specified button was just released in this packet.
    /// </summary>
    public bool IsButtonReleased(int buttonNumber) =>
        ButtonAction == PenButtonAction.Released && ButtonNumber == buttonNumber;

    /// <summary>
    /// Returns true if the pen tip is being pressed in this packet.
    /// For continuous tip-down detection, check <see cref="Pressure"/> &gt; 0 instead.
    /// </summary>
    public bool IsTipPressed => IsButtonPressed(PenButtonNumber.Tip);

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
