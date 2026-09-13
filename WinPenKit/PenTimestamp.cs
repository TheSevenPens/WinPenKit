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
