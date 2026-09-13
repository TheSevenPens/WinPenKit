namespace WinPenKit;

/// <summary>
/// What <see cref="PenPoint.RawX"/> and <see cref="PenPoint.RawY"/> are measured in.
/// </summary>
public enum PenRawUnits
{
    /// <summary>
    /// No device-native position is available, and the fields carry nothing. They are written
    /// as zero rather than filled with a copy of <see cref="PenPoint.DesktopX"/>, which is not
    /// a second opinion about position but the first one with information removed.
    /// </summary>
    None,

    /// <summary>The tablet's own coordinate space, as the digitizer reports it.</summary>
    TabletNative,

    /// <summary>Physical screen pixels, already mapped by the driver.</summary>
    ScreenPixels,

    /// <summary>Hundredths of a millimetre, from the pointer API's HIMETRIC position.</summary>
    HundredthsOfMillimetre,
}

/// <summary>
/// How <see cref="PenPoint.Buttons"/> is packed.
/// </summary>
public enum PenButtonEncoding
{
    /// <summary>
    /// One event per packet, <c>(action &lt;&lt; 16) | buttonNumber</c>. Packets with no
    /// button event carry zero, and state is held between them.
    /// </summary>
    WintabEvent,

    /// <summary>
    /// A bitmask replaced on every packet: bit 0 barrel, bit 1 eraser. No per-button
    /// identity, so a second or third barrel switch cannot be told from the first.
    /// </summary>
    PointerFlags,
}

/// <summary>
/// Where the numbers in <see cref="PenPoint.Cursor"/> come from.
/// </summary>
public enum PenCursorNumbering
{
    /// <summary>
    /// The session writes <see cref="PenCursorType"/>'s values, so
    /// <see cref="PenPoint.IsEraser"/> answers correctly.
    /// </summary>
    Normalised,

    /// <summary>
    /// The driver's own cursor number, passed through. Wintab cursor indices are assigned by
    /// the device, and <see cref="PenCursorType"/> documents 13 and 14 as observed values
    /// rather than standard ones, so <see cref="PenPoint.IsEraser"/> can be false with the
    /// eraser in use.
    /// </summary>
    DeviceAssigned,
}

/// <summary>
/// What a session's <see cref="PenPoint"/> values mean, for the fields whose meaning depends
/// on which backend produced them.
/// </summary>
/// <remarks>
/// <para><see cref="PenPoint"/> is this library's whole output, and a consumer holding one is
/// supposed to know what it says. For three of its fields that depended on the backend, and
/// nothing reported which convention was in force.</para>
/// <para><see cref="IPenSession.MaxPressure"/> is the pattern this follows: a value whose
/// scale varies, paired with a property naming the scale. It was simply never applied to
/// anything else.</para>
/// <para>This answers <i>which convention</i>. <see cref="PenCapabilities"/> answers
/// <i>supported or not</i>. They are different questions, and asking one flag to answer both
/// is how a hi-res flag came to survive a fallback that had turned hi-res off.</para>
/// </remarks>
public readonly record struct PenConventions(
    PenRawUnits RawUnits,
    PenButtonEncoding Buttons,
    PenCursorNumbering Cursor);

/// <summary>
/// One place for the short unit name a readout shows beside
/// <see cref="PenPoint.RawX"/>, so five samples cannot drift into five spellings.
/// </summary>
public static class PenRawUnitsExtensions
{
    /// <summary>
    /// A short label, or an empty string for <see cref="PenRawUnits.None"/> -- which has no
    /// unit because it has no value. Show a readout as unavailable in that case rather than
    /// printing the zeros.
    /// </summary>
    public static string Label(this PenRawUnits units) => units switch
    {
        PenRawUnits.TabletNative => "tablet",
        PenRawUnits.ScreenPixels => "px",
        PenRawUnits.HundredthsOfMillimetre => "0.01mm",
        _ => "",
    };
}
