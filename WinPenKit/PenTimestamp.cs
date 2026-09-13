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
    /// <summary>How far a 32-bit millisecond counter runs before it repeats: 2^32 ms, 49.7 days.</summary>
    public const long Wrap32 = 1L << 32;

    /// <summary>
    /// A <c>QueryPerformanceCounter</c> reading, such as
    /// <c>POINTER_INFO.PerformanceCount</c>, in microseconds.
    /// </summary>
    /// <remarks>
    /// Divided before multiplying rather than after. A machine with a 10 MHz counter and a few
    /// days of uptime is already past 10^12 ticks, and multiplying that by a million first
    /// overflows a signed 64-bit value inside a normal service life -- 11 days on a 10 MHz
    /// counter. Splitting the division costs one extra divide and is exact.
    /// </remarks>
    public static long FromPerformanceCount(ulong performanceCount)
    {
        long ticks = (long)performanceCount;
        long freq = Stopwatch.Frequency;
        if (freq <= 0) return 0;
        return (ticks / freq) * 1_000_000L + (ticks % freq) * 1_000_000L / freq;
    }

    /// <summary>
    /// A millisecond count in microseconds, with no wrap handling. Only for a source known to
    /// be 64 bits at its origin.
    /// </summary>
    /// <remarks>
    /// This does not add resolution. A backend whose clock advances in whole milliseconds
    /// still does so after the multiply; the unit is uniform so that consumers do not branch,
    /// and <see cref="PenConventions.Timestamp"/> says what the real granularity is.
    /// </remarks>
    public static long FromMilliseconds(long milliseconds) => milliseconds * 1_000L;

    /// <summary>
    /// A framework event timestamp, anchored against the full 64-bit system clock and returned
    /// in microseconds.
    /// </summary>
    /// <remarks>
    /// <para><b>A 64-bit property is not a 64-bit clock.</b> WPF's
    /// <c>StylusEventArgs.Timestamp</c> is an <c>int</c> and visibly narrow. Avalonia's and
    /// Qt's are declared 64-bit and still carry a value that has already wrapped: on Windows
    /// both take <c>GetMessageTime</c>, which is 32 bits, and widen it afterwards. Reading the
    /// declared type and concluding the clock is wide is the same error as reading
    /// <c>MaxPressure</c> 32767 as a level count.
    /// </para>
    /// <para>So rather than detect a wrap, this removes the question. Every one of these
    /// sources was measured to track <see cref="Environment.TickCount64"/>, so the full reading
    /// is recoverable by taking the nearest multiple of 2^32 ms that makes the two agree:</para>
    /// <code>
    /// extended = raw + round((TickCount64 - raw) / 2^32) * 2^32
    /// </code>
    /// <para>That is exact for a 32-bit source of either signedness, and a no-op for a source
    /// that really is 64 bits, where the difference is already near zero. It holds no state, so
    /// unlike a jump detector it does not care how long the session sat idle, whether the
    /// packet before it was discarded by a capture region, or whether this is the first packet
    /// after a wrap.</para>
    /// <para>The jump detector it replaces could not do that. It fired on a backward step of
    /// more than half the range, which a stroke drawn across the boundary produces and a
    /// session resuming after an idle does not: fed two WPF packets 25 days apart, it returned
    /// a difference of about minus 24.7 days.</para>
    /// <para>Valid only for a clock on the <c>GetTickCount</c> epoch. A driver clock with an
    /// unstated origin cannot be anchored this way -- see <see cref="DeviceTickCounter"/>.</para>
    /// </remarks>
    public static long FromSystemTicks(long rawMilliseconds) =>
        FromSystemTicks(rawMilliseconds, Environment.TickCount64);

    /// <summary>
    /// The same conversion against a caller-supplied reference clock.
    /// </summary>
    /// <remarks>
    /// Exists so the wrap can be tested. The boundary is 49.7 days of uptime, and a machine
    /// that has not reached it makes the anchoring a no-op -- so a check that reads the real
    /// clock passes whether the arithmetic is there or not. That is precisely what happened:
    /// the first version of the clock suite passed unchanged with the anchoring deleted,
    /// because the machine it ran on had been up 1.4 days.
    /// </remarks>
    public static long FromSystemTicks(long rawMilliseconds, long nowMilliseconds)
    {
        // Floor of (difference + half a range) over a range: round-to-nearest, and correct for
        // a negative difference, where integer division would truncate toward zero instead.
        double k = Math.Floor(((double)(nowMilliseconds - rawMilliseconds) + Wrap32 / 2.0) / Wrap32);
        return FromMilliseconds(rawMilliseconds + (long)k * Wrap32);
    }
}

/// <summary>
/// Extends a device's narrow millisecond counter, for a clock whose origin is unknown and so
/// cannot be anchored against the system's.
/// </summary>
/// <remarks>
/// <para>Wintab's <c>pkTime</c> is a <c>uint</c> and wraps to zero after about 49.7 days.
/// Wintab documents no origin for it and none has been measured, because Wintab ignores
/// synthetic pen input and settling it needs a tablet. Without an origin there is nothing to
/// anchor to, so this detects the wrap rather than deriving it, the way
/// <see cref="PenTimestamp.FromSystemTicks"/> can for the framework clocks.</para>
/// <para>Detection is a backward jump of more than half the range. Pen packets arrive
/// milliseconds apart, so nothing legitimate moves backward, and half a range is 24.9 days of
/// margin.</para>
/// <para><b>What this cannot do.</b> It sees only the packets it is given, so a wrap that
/// happens while the session is stopped, or across a gap in which every packet was discarded by
/// a capture region, is missed, and the difference across that gap is then wrong by 49.7 days.
/// That is a real limit rather than a rounding error, and it is why the framework backends no
/// longer use this approach. Establishing Wintab's epoch would let them share one.</para>
/// </remarks>
public sealed class DeviceTickCounter
{
    private long _high;
    private long _last = long.MinValue;

    /// <summary>
    /// The next raw reading, extended and converted to microseconds. Call once per packet, in
    /// arrival order.
    /// </summary>
    public long Next(long raw)
    {
        if (_last != long.MinValue && raw < _last && _last - raw > PenTimestamp.Wrap32 / 2)
            _high += PenTimestamp.Wrap32;

        _last = raw;
        return PenTimestamp.FromMilliseconds(_high + raw);
    }
}
