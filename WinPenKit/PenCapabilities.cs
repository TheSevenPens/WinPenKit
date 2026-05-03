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

    /// <summary>Sub-pixel tablet-native resolution (digitizer hi-res mode).</summary>
    HiRes = 1 << 5,

    /// <summary>Eraser detection (via cursor type or pen flags).</summary>
    Eraser = 1 << 6,
}
