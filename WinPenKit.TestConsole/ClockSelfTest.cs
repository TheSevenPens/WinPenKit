using System.Diagnostics;
using WinPenKit;

namespace WinPenKit.TestConsole;

/// <summary>
/// Checks the timestamp arithmetic against the faults it exists for.
/// </summary>
/// <remarks>
/// <para>Run with <c>--selftest-clock</c>. It needs no tablet and no window, which is the
/// point: a 32-bit millisecond counter wraps once every 49.7 days, so neither boundary will
/// ever be reached in ordinary use of this repository and a defect in the handling would sit
/// undisturbed until someone's machine had been up long enough.</para>
/// <para><b>What this reaches, and what it does not.</b> It calls
/// <see cref="PenTimestamp"/> and <see cref="DeviceTickCounter"/> directly. It is not
/// <c>WpfStylusSession</c>, not <c>WintabSessionBase</c>, and not the C++ copy of the wrap
/// detector in <c>wintab_session_impl.cpp</c> -- a defect in that copy still prints a pass
/// here. Each session hands its raw reading to these functions and does nothing else with it,
/// so the arithmetic is the part worth pinning; the wiring is not covered, and saying
/// otherwise would be the overclaim this suite exists to catch.</para>
/// <para>An earlier version of this file was described as verified "in both directions". The
/// failing run quoted at the time came from editing production code, which is not something
/// this file can do. The cases below fail against the arithmetic as it stood before the
/// anchoring fix, which is a claim the file supports on its own.</para>
/// </remarks>
public static class ClockSelfTest
{
    public static int Run()
    {
        int failed = 0;
        failed += SystemTicks();
        failed += DeviceTicks();
        failed += PerformanceCounter();

        Console.WriteLine(failed == 0 ? "RESULT clock checks passed" : $"RESULT {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ── The anchored path: WPF, Avalonia, Qt ────────────────────

    /// <summary>
    /// <see cref="PenTimestamp.FromSystemTicks"/> recovers the full reading from a narrow one
    /// by anchoring against <see cref="Environment.TickCount64"/>.
    /// </summary>
    /// <remarks>
    /// Each case builds the input forward -- "what a 32-bit counter would show at this true
    /// time" -- and checks the true time comes back. Deriving the input from the answer would
    /// be a test of nothing, which is how the surface-alignment check in this repository once
    /// passed on both sides of a real fault.
    /// </remarks>
    private static int SystemTicks()
    {
        int failed = 0;

        // Sixty days of uptime, not the real clock. The boundary is at 49.7 days, so on a
        // machine below it the anchoring is a no-op and a check that reads Environment
        // .TickCount64 passes whether the arithmetic is present or not -- which is exactly what
        // the first version of this file did, on a machine that had been up 1.4 days.
        const long sixtyDays = 60L * 24 * 60 * 60 * 1000;
        long now = sixtyDays;

        // What WPF reports at a given true time: the low 32 bits read as a signed int, which
        // is negative for half of every cycle.
        static long AsWpfInt(long trueMs) => unchecked((int)(uint)trueMs);
        // What Avalonia and Qt report: the low 32 bits widened unsigned.
        static long AsUInt32(long trueMs) => (long)(uint)trueMs;

        // Past the boundary, where a 32-bit source has wrapped and a signed one is negative.
        failed += Expect("system/wpf-int-source",
            PenTimestamp.FromSystemTicks(AsWpfInt(now), now), now * 1000);

        failed += Expect("system/uint-source",
            PenTimestamp.FromSystemTicks(AsUInt32(now), now), now * 1000);

        // A source that really is 64 bits has to pass through untouched, or anchoring would be
        // a fix that breaks the backends it was not meant for.
        failed += Expect("system/already-wide",
            PenTimestamp.FromSystemTicks(now, now), now * 1000);

        // Below the boundary nothing should change either.
        const long oneDay = 24L * 60 * 60 * 1000;
        failed += Expect("system/before-any-wrap",
            PenTimestamp.FromSystemTicks(AsUInt32(oneDay), oneDay), oneDay * 1000);

        // A difference measured across the boundary itself: the last reading before the wrap
        // and the first after it, five milliseconds apart. This is the case the jump detector
        // got wrong, returning about minus 24.7 days.
        {
            long before = PenTimestamp.Wrap32 - 2;   // 2ms before the counter returns to zero
            long after = PenTimestamp.Wrap32 + 3;    // 3ms after
            long nowAfter = after;
            long d = PenTimestamp.FromSystemTicks(AsUInt32(after), nowAfter)
                   - PenTimestamp.FromSystemTicks(AsUInt32(before), nowAfter);
            failed += Expect("system/across-the-wrap", d, 5000);
        }

        // And the idle case both reviewers found: a session that saw nothing for 25 days and
        // then received one packet. Stateless anchoring does not care.
        {
            long early = 5_000;
            long late = early + 25L * 24 * 60 * 60 * 1000 * 2;   // 50 days later, past the wrap
            long d = PenTimestamp.FromSystemTicks(AsUInt32(late), late)
                   - PenTimestamp.FromSystemTicks(AsUInt32(early), early);
            failed += Expect("system/idle-across-the-wrap", d, (late - early) * 1000);
        }

        return failed;
    }

    // ── The detected path: Wintab ───────────────────────────────

    private static int DeviceTicks()
    {
        int failed = 0;

        // A uint counter returning to zero, one millisecond per step.
        failed += Sequence("device/uint-wrap",
            [uint.MaxValue - 2, uint.MaxValue - 1, uint.MaxValue, 0, 1],
            [1000, 1000, 1000, 1000]);

        failed += Sequence("device/ordinary", [1000, 1016, 1031, 1047], [16000, 15000, 16000]);

        // A coarse clock repeating a value is normal. A zero delta is a reading, not a wrap.
        failed += Sequence("device/repeated-values", [5000, 5000, 5016, 5016], [0, 16000, 0]);

        // A small backward step is not a wrap. Adding a range to it would turn a 2ms oddity
        // into a 49-day one.
        failed += Sequence("device/small-backward-step", [9000, 8998], [-2000]);

        return failed;
    }

    // ── QPC ─────────────────────────────────────────────────────

    /// <summary>
    /// The split division in <see cref="PenTimestamp.FromPerformanceCount"/>, which exists
    /// because the naive form overflows within a normal uptime.
    /// </summary>
    private static int PerformanceCounter()
    {
        int failed = 0;
        long freq = Stopwatch.Frequency;

        failed += Expect("qpc/one-second", PenTimestamp.FromPerformanceCount((ulong)freq), 1_000_000);
        failed += Expect("qpc/zero", PenTimestamp.FromPerformanceCount(0), 0);

        // Twenty days of this machine's counter. At 10MHz the naive ticks * 1_000_000 has
        // already overflowed a signed 64-bit value by here.
        long twentyDays = freq * 60 * 60 * 24 * 20;
        failed += Expect("qpc/twenty-days", PenTimestamp.FromPerformanceCount((ulong)twentyDays),
            20L * 24 * 60 * 60 * 1_000_000);

        // Truncation is toward zero and stays under one microsecond.
        failed += Expect("qpc/truncates-down",
            PenTimestamp.FromPerformanceCount((ulong)(freq + (freq - 1))), 2_000_000 - 1);

        return failed;
    }

    // ── Reporting ───────────────────────────────────────────────

    private static int Expect(string name, long actual, long expected)
    {
        if (actual == expected)
        {
            Console.WriteLine($"[PASS] {name,-28} {actual}");
            return 0;
        }
        Console.WriteLine($"[FAIL] {name,-28} expected {expected}, got {actual}");
        return 1;
    }

    private static int Sequence(string name, long[] raw, long[] expectedDeltasUs)
    {
        var clock = new DeviceTickCounter();
        var got = new long[raw.Length];
        for (int i = 0; i < raw.Length; i++) got[i] = clock.Next(raw[i]);

        for (int i = 0; i < expectedDeltasUs.Length; i++)
        {
            long actual = got[i + 1] - got[i];
            if (actual != expectedDeltasUs[i])
            {
                Console.WriteLine($"[FAIL] {name,-28} delta {i}: expected {expectedDeltasUs[i]}us, got {actual}us");
                return 1;
            }
        }

        Console.WriteLine($"[PASS] {name,-28} {raw.Length} readings");
        return 0;
    }
}
