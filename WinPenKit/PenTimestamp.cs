using System.Diagnostics;

namespace WinPenKit;

/// <summary>
/// Converts each backend's clock into the microseconds
/// <see cref="PenPoint.TimestampMicroseconds"/> carries.
/// </summary>
/// <remarks>
/// One place for the arithmetic so that six backends cannot drift into six conversions, which
/// is the same reason <see cref="PenRawUnitsExtensions"/> exists for unit labels.
/// </remarks>
public static class PenTimestamp
{
    /// <summary>
    /// A <c>QueryPerformanceCounter</c> reading, such as
    /// <c>POINTER_INFO.PerformanceCount</c>, in microseconds.
    /// </summary>
    /// <remarks>
    /// Divided before multiplying rather than after. A machine with a 10 MHz counter and a few
    /// days of uptime is already past 10^12 ticks, and multiplying that by a million first
    /// overflows a signed 64-bit value inside a normal service life. Splitting the division
    /// costs one extra divide and is exact.
    /// </remarks>
    public static long FromPerformanceCount(ulong performanceCount)
    {
        long ticks = (long)performanceCount;
        long freq = Stopwatch.Frequency;
        if (freq <= 0) return 0;
        return (ticks / freq) * 1_000_000L + (ticks % freq) * 1_000_000L / freq;
    }

    /// <summary>
    /// A millisecond count -- Wintab's <c>pkTime</c>, or a framework event timestamp -- in
    /// microseconds.
    /// </summary>
    /// <remarks>
    /// This does not add resolution. A backend whose clock advances in whole milliseconds
    /// still does so after the multiply; the unit is uniform so that consumers do not branch,
    /// and <see cref="PenConventions.Timestamp"/> says what the real granularity is.
    /// </remarks>
    public static long FromMilliseconds(long milliseconds) => milliseconds * 1_000L;
}

/// <summary>
/// Extends a backend's narrow millisecond counter to a 64-bit one that does not wrap.
/// </summary>
/// <remarks>
/// <para>Two backends count milliseconds in fewer than 64 bits, and both wrap inside a machine
/// uptime people actually reach:</para>
/// <list type="bullet">
/// <item><description>WPF's <c>StylusEventArgs.Timestamp</c> is <c>int</c>. It passes
/// <c>int.MaxValue</c> after about 24.9 days and continues <b>negative</b>.</description></item>
/// <item><description>Wintab's <c>pkTime</c> is <c>uint</c>, wrapping to 0 after about 49.7
/// days.</description></item>
/// </list>
/// <para>Left alone, a stroke drawn across either boundary yields a difference that is wrong by
/// the whole range, and the sign flip on WPF makes it wrong for days rather than for one
/// packet. Neither is common; both are silent, and a timestamp that is silently wrong is worse
/// than one that is absent, because <see cref="PenTimestampSource.None"/> at least says so.</para>
/// <para>Detection is by a backward jump larger than half the counter's range. Pen packets
/// arrive milliseconds apart, so nothing legitimate moves backward at all, and half a range is
/// 12 days of margin on the narrower of the two. One instance per session, so the state cannot
/// leak between devices.</para>
/// <para>This cannot recover a wrap that happened while the session was not running. It keeps
/// one session's stream continuous, which is exactly the contract
/// <see cref="PenPoint.TimestampMicroseconds"/> offers.</para>
/// </remarks>
public sealed class MillisecondCounter
{
    private readonly long _range;
    private long _high;
    private long _last = long.MinValue;

    /// <param name="bits">Width of the backend's counter: 32 for both WPF and Wintab.</param>
    public MillisecondCounter(int bits = 32) => _range = 1L << bits;

    /// <summary>
    /// The next raw reading, extended and converted to microseconds. Call once per packet, in
    /// arrival order.
    /// </summary>
    public long Next(long raw)
    {
        if (_last != long.MinValue && raw < _last && _last - raw > _range / 2)
            _high += _range;

        _last = raw;
        return PenTimestamp.FromMilliseconds(_high + raw);
    }
}
