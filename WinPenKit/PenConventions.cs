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
/// Measured 12 Sep 2026, one machine, <b>synthetic pen input</b> -- see the caveat below,
/// which is load-bearing for the first row.</para>
/// <list type="table">
/// <item><term>WM_POINTER, WinForms</term><description><b>1 µs, on hardware.</b> 2070 points,
/// 2070 distinct timestamps, greatest common divisor of the gaps exactly 1 µs. The finest
/// clock of any backend here</description></item>
/// <item><term>WinUI 3</term><description><b>1 µs, on hardware.</b> 1878 points, 1878 distinct
/// timestamps, gcd of the gaps exactly 1 µs. Under injection it looked like a millisecond clock
/// with a fixed sub-millisecond offset; the offset was the injector's</description></item>
/// <item><term>Avalonia</term><description><b>1 ms, on hardware, one stamp per point.</b> 2167
/// points carried 2167 distinct timestamps with no repeats. The resolution comes from the source
/// type -- <c>PointerEventArgs.Timestamp</c> counts milliseconds -- rather than from the
/// recording, whose smallest gap is 3 ms</description></item>
/// <item><term>WPF</term><description><b>1 ms clock, 15.6 ms batches, on hardware.</b> 2442
/// points carried 885 distinct timestamps. The clock is not the problem and never was; see
/// <see cref="SystemTicks"/></description></item>
/// <item><term>Qt (<c>Scribble.Qt</c>, not WinPenKit)</term><description><b>15.6 ms, on
/// hardware.</b> The one injected figure that survived contact with a tablet: across 809 gaps
/// the smallest is 15 ms</description></item>
/// <item><term>Wintab</term><description>not established; see
/// <see cref="DeviceTicks"/></description></item>
/// </list>
/// <para><b>Synthetic injection sets the floor it appears to measure</b>, which is no longer a
/// caution but an observed fact. <c>InjectSyntheticPointerInput</c> stamps its own events, so a
/// backend cannot be shown to resolve finer than the thing feeding it. WM_POINTER and WinUI
/// both measured 1 ms through it and both turned out to be a thousand times finer when drawn on
/// by hand; WPF's clock turned out to be 15 times finer than its batch cadence had suggested;
/// Avalonia looked like it repeated timestamps and does not. Every backend above has since been
/// drawn on, and of the five that had an injected figure to compare against, <b>four were
/// wrong</b>. Only Qt's survived.</para>
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
    /// <c>QueryPerformanceCounter</c>, divided down to microseconds.
    /// </summary>
    /// <remarks>
    /// The counter ticks every 100ns on a typical machine. Drawn on by hand, the field carries
    /// that fineness through: 2070 points, 2070 distinct timestamps, gaps whose greatest common
    /// divisor is 1 µs. Under synthetic injection the same field delivered exact millisecond
    /// multiples matching <c>dwTime</c>, which was the injector stamping its own events rather
    /// than anything about this clock.
    /// </remarks>
    PerformanceCounter,

    /// <summary>
    /// The millisecond counter <c>GetTickCount64</c> reads, multiplied up to microseconds.
    /// Measured against that clock on WPF, WinUI and Avalonia, and found to track it.
    /// </summary>
    /// <remarks>
    /// <para>One clock, two very different streams, which is why this value alone does not
    /// tell a consumer what it is holding.</para>
    /// <para><b>Avalonia and WinUI</b> deliver one point per event, each with its own
    /// timestamp, and on hardware neither repeats a value: 2167 Avalonia points carried 2167
    /// distinct timestamps, 1878 WinUI points 1878. An injected run suggested Avalonia repeated
    /// them -- 113 values for 172 points -- which was the injector outrunning its own clock.</para>
    /// <para><b>WPF does not.</b> <c>StylusEventArgs</c> carries a whole
    /// <c>StylusPointCollection</c> and the timestamp belongs to the event, so every point in
    /// the batch gets the same one. Drawn on by hand: 2442 points, <b>885 distinct
    /// timestamps</b>, about three points to a value.</para>
    /// <para>The clock itself is a millisecond clock and was never the limitation. Sixteen gaps
    /// of exactly 1000 µs appear in that recording, spread through the stroke, so the field does
    /// express a millisecond. What steps by 15.6 ms is the delivery -- the Windows timer tick --
    /// which a finer clock would not change. Earlier documentation called 15.6 ms the clock's
    /// granularity; it was the batch cadence.</para>
    /// <para>WPF exposes no per-point time, so this is a ceiling of the framework rather than
    /// a choice made here. Anything that needs per-point timing -- velocity, time-based
    /// smoothing -- has to treat a WPF batch as points sharing one instant, or interpolate
    /// across the batch and know it is inventing the values.</para>
    /// </remarks>
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
