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
    /// <remarks>
    /// Means the backend reads twist from its API. A pen without a rotation sensor still
    /// reports 0, on every backend.
    /// </remarks>
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
    /// The session reports the pen leaving proximity, as a point with
    /// <see cref="PenPoint.IsInProximity"/> false.
    /// </summary>
    /// <remarks>
    /// Without this flag <see cref="PenPoint.IsInProximity"/> is true on every point the
    /// session produces. The five pointer backends leave <see cref="PenPoint.Status"/> at zero
    /// because the pointer APIs carry no equivalent of Wintab's proximity bit: every point they
    /// deliver is in range, and no point marks the pen leaving. A consumer that needs to know
    /// when the pen has gone has to ask this first, or time out on the absence of points.
    /// </remarks>
    Proximity = 1 << 8,
}
