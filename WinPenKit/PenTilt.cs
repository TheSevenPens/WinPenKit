namespace WinPenKit;

/// <summary>
/// Converts between the two tilt representations <see cref="PenPoint"/> carries: spherical
/// (<see cref="PenPoint.Azimuth"/>, <see cref="PenPoint.Altitude"/>) and planar
/// (<see cref="PenPoint.TiltX"/>, <see cref="PenPoint.TiltY"/>), all in degrees.
/// </summary>
/// <remarks>
/// <para>Wintab reports spherical angles and the pointer APIs report planar ones, so every
/// backend converts one into the other. The conversion is exact: a planar angle is the angle
/// the pen makes with the vertical when projected onto the XZ or YZ plane, so
/// <c>tan(TiltX)</c> and <c>tan(TiltY)</c> are the horizontal components of the pen's
/// direction divided by its vertical component. The backends previously used a linear
/// approximation, <c>TiltX = (90 - Altitude) * sin(Azimuth)</c>, which agrees on the axes and
/// differs off them: 8.3 degrees at azimuth 45 and altitude 30.</para>
/// <para>Sign convention, unchanged from the approximation: positive <see cref="PenPoint.TiltX"/>
/// is a tilt to the right, positive <see cref="PenPoint.TiltY"/> is a tilt toward the user, and
/// azimuth is <c>atan2(-tan TiltX, tan TiltY)</c>. Whether that matches the direction a Wintab
/// driver means by <c>orAzimuth</c> has not been measured against a pen held at a known angle.</para>
/// </remarks>
public static class PenTilt
{
    /// <summary>
    /// Below this angle from vertical, in degrees, azimuth is reported as 0. The direction of
    /// a nearly upright pen is dominated by sensor noise.
    /// </summary>
    public const double UprightThreshold = 0.5;

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Converts azimuth and altitude, in degrees, to planar TiltX and TiltY in degrees.</summary>
    public static (double TiltX, double TiltY) ToPlanar(double azimuth, double altitude)
    {
        double fromVertical = (90.0 - altitude) * DegToRad;
        double az = azimuth * DegToRad;
        double horizontal = Math.Sin(fromVertical);
        double vertical = Math.Cos(fromVertical);
        double tiltX = -Math.Atan2(horizontal * Math.Sin(az), vertical) * RadToDeg;
        double tiltY = Math.Atan2(horizontal * Math.Cos(az), vertical) * RadToDeg;
        return (tiltX, tiltY);
    }

    /// <summary>
    /// Converts planar TiltX and TiltY, in degrees, to azimuth (0 to 360) and altitude
    /// (0 to 90) in degrees.
    /// </summary>
    public static (double Azimuth, double Altitude) ToSpherical(double tiltX, double tiltY)
    {
        double tx = Math.Tan(Math.Clamp(tiltX, -90.0, 90.0) * DegToRad);
        double ty = Math.Tan(Math.Clamp(tiltY, -90.0, 90.0) * DegToRad);
        double fromVertical = Math.Atan(Math.Sqrt(tx * tx + ty * ty)) * RadToDeg;
        double altitude = Math.Clamp(90.0 - fromVertical, 0.0, 90.0);
        if (fromVertical <= UprightThreshold)
            return (0.0, altitude);
        double azimuth = Math.Atan2(-tx, ty) * RadToDeg;
        return (((azimuth % 360.0) + 360.0) % 360.0, altitude);
    }
}
