namespace WinPenKit;

/// <summary>
/// Flags indicating which pen data features the current session supports.
/// Query via <see cref="IPenSession.Capabilities"/>.
/// </summary>
[Flags]
public enum PenCapabilities
{
    /// <summary>No capabilities detected.</summary>
    None = 0,

    /// <summary>Pen tip pressure (normal force).</summary>
    Pressure = 1 << 0,

    /// <summary>Tilt (azimuth and altitude, or X/Y tilt).</summary>
    Tilt = 1 << 1,

    /// <summary>Barrel twist (pen rotation around its long axis).</summary>
    Twist = 1 << 2,

    /// <summary>Z-axis height above the tablet surface.</summary>
    ZHeight = 1 << 3,

    /// <summary>Barrel buttons and button state reporting.</summary>
    Buttons = 1 << 4,

    /// <summary>
    /// Positions carry sub-pixel precision rather than whole screen pixels.
    /// </summary>
    /// <remarks>
    /// Set by the Wintab digitizer session, which asks the driver for tablet-native output, and
    /// by the WM_POINTER session when it can map the HIMETRIC position through the device rects.
    /// It is a statement about the data actually being delivered, not a mode that was selected:
    /// the WM_POINTER session always tries, and clears this flag if the device rects are
    /// unavailable and it has to fall back to whole pixels.
    /// </remarks>
    HiRes = 1 << 5,

    /// <summary>Eraser detection (via cursor type or pen flags).</summary>
    Eraser = 1 << 6,

    /// <summary>
    /// Desktop-wide capture: the session can report points anywhere on screen,
    /// not just over the application window. Set <see cref="IPenSession.CaptureRegion"/>
    /// to <see cref="PenCaptureRegion.Unbounded"/> to use it. Wintab only.
    /// </summary>
    GlobalCapture = 1 << 7,

    /// <summary>
    /// The session reports proximity, so <see cref="PenPoint.IsInProximity"/> means something.
    /// </summary>
    /// <remarks>
    /// Without this flag <see cref="PenPoint.IsInProximity"/> is false on every point the
    /// session produces, including hover points it does report. The five pointer backends
    /// leave <see cref="PenPoint.Status"/> at zero because the pointer APIs carry no
    /// equivalent of Wintab's proximity bit, so a consumer that uses the property to tell
    /// hover from no-pen has to ask this first.
    /// </remarks>
    Proximity = 1 << 8,
}
