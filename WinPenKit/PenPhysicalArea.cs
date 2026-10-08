namespace WinPenKit;

/// <summary>
/// How big the tablet's active area is, in millimetres, and how much of it a session's points
/// are drawn from. Query via <see cref="IPenSession.PhysicalArea"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three things that sound alike and are not.</b> The <i>device</i> area is the whole
/// sensing surface, a fact about the tablet. The <i>mapped</i> area is the part of it the
/// driver currently maps to the desktop, which is the whole of it unless the user has chosen a
/// partial or proportion-forced mapping. The mapped pixels are the desktop rectangle that part
/// lands on. A distance between two <see cref="PenPoint.DesktopX"/> values is a number of those
/// pixels, so the number that turns it into millimetres is the mapped area over the mapped
/// pixels, and not the device area over the screen.
/// </para>
/// <para>
/// <b>Consistent with the points, by construction.</b> The Wintab digitizer session converts a
/// raw position to <see cref="PenPoint.DesktopX"/> by scaling the context's input rectangle onto
/// its system rectangle. These figures are that same input and system rectangle, so
/// <see cref="MillimetresPerPixelX"/> is exactly the scale the conversion applied. It is no
/// better than the positions are: a driver that places the pen wrongly on the desktop (see
/// issues 129 and 130) has its distances wrong by the same factor.
/// </para>
/// <para>
/// <b>The mapped figures can overstate what the pen reaches.</b> They are the context's rectangles,
/// and neither Wintab nor WM_POINTER says when the driver has cropped the tablet to one display:
/// measured with the display mapping set to a single monitor, only part of the tablet delivered
/// anything while these still described all of it. The scale is unaffected; the extent is not.
/// See <c>Docs/PEN-SWEEP.md</c>.
/// </para>
/// </remarks>
/// <param name="DeviceWidthMm">The whole active area, horizontally.</param>
/// <param name="DeviceHeightMm">The whole active area, vertically.</param>
/// <param name="MappedWidthMm">The part of it mapped to the desktop, horizontally.</param>
/// <param name="MappedHeightMm">The part of it mapped to the desktop, vertically.</param>
/// <param name="MappedWidthPixels">The desktop pixels that part lands on, horizontally.</param>
/// <param name="MappedHeightPixels">The desktop pixels that part lands on, vertically.</param>
public readonly record struct PenPhysicalArea(
    double DeviceWidthMm,
    double DeviceHeightMm,
    double MappedWidthMm,
    double MappedHeightMm,
    double MappedWidthPixels,
    double MappedHeightPixels)
{
    /// <summary>Millimetres on the tablet for one desktop pixel of horizontal movement.</summary>
    public double MillimetresPerPixelX => MappedWidthMm / MappedWidthPixels;

    /// <summary>Millimetres on the tablet for one desktop pixel of vertical movement.</summary>
    public double MillimetresPerPixelY => MappedHeightMm / MappedHeightPixels;
}
