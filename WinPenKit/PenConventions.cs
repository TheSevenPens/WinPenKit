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
/// Which clock <see cref="PenPoint.TimestampMicroseconds"/> is counted on.
/// </summary>
/// <remarks>
/// <para>The timestamp itself is microseconds with no stated origin, so subtracting two of
/// them is always valid and reading one on its own is not. This says where the number came
/// from, for a diagnostician who needs to line pen points up against something else.</para>
/// <para>Resolution is a separate question from clock, and the two do not follow each other.
/// Measured on 12 Sep 2026, one machine, synthetic pen input:</para>
/// <list type="table">
/// <item><term>WM_POINTER, WinForms</term><description>100 ns, from
/// <c>POINTER_INFO.PerformanceCount</c></description></item>
/// <item><term>WinUI 3</term><description>1 ms. <c>PointerPoint.Timestamp</c> is declared in
/// microseconds and every reading ended in the same 171 µs, so the sub-millisecond digits are
/// a fixed offset rather than measurement</description></item>
/// <item><term>Avalonia</term><description>1 ms</description></item>
/// <item><term>WPF</term><description>about 15.6 ms -- consecutive points repeat a
/// value</description></item>
/// <item><term>Wintab</term><description>not established; see
/// <see cref="DeviceTicks"/></description></item>
/// </list>
/// </remarks>
public enum PenTimestampSource
{
    /// <summary>
    /// The backend supplies no timestamp, and <see cref="PenPoint.TimestampMicroseconds"/> is
    /// zero. Zero rather than the time the session read the packet, which measures this
    /// library's own scheduling and not the pen. Same rule as
    /// <see cref="PenRawUnits.None"/>.
    /// </summary>
    None,

    /// <summary>
    /// <c>QueryPerformanceCounter</c>, divided down to microseconds. Sub-microsecond at
    /// source, and the only backend clock that resolves finer than a millisecond.
    /// </summary>
    PerformanceCounter,

    /// <summary>
    /// The millisecond counter <c>GetTickCount64</c> reads, multiplied up to microseconds.
    /// Measured against that clock on the three managed frameworks and found to track it.
    /// </summary>
    SystemTicks,

    /// <summary>
    /// The driver's own millisecond counter, multiplied up to microseconds. Wintab's
    /// <c>pkTime</c>.
    /// </summary>
    /// <remarks>
    /// Its origin and its resolution were not measured -- Wintab does not respond to
    /// synthetic pen injection, so establishing either needs a tablet. Treat deltas as usable
    /// and everything else as unknown until that measurement exists.
    /// </remarks>
    DeviceTicks,
}

/// <summary>
/// What a session's <see cref="PenPoint"/> values mean, for the fields whose meaning depends
/// on which backend produced them.
/// </summary>
/// <remarks>
/// <para><see cref="PenPoint"/> is this library's whole output, and a consumer holding one is
/// supposed to know what it says. For four of its fields that depended on the backend, and
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
    PenCursorNumbering Cursor,
    PenTimestampSource Timestamp);

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
