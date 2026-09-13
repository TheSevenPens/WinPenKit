using System.Diagnostics;
using WinPenKit;
using WinPenKit.Diagnostics;

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
        failed += EpochProbe();

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

    // ── The epoch probe's verdict ───────────────────────────────

    /// <summary>
    /// <see cref="WintabEpochProbe.Report"/> reaches both of its verdicts.
    /// </summary>
    /// <remarks>
    /// <para>The probe itself needs a tablet and a person to draw with it, so its verdict is
    /// produced once per session at most and always against whatever that machine happens to
    /// do. If pkTime turns out to sit on the GetTickCount epoch, every run of the probe will
    /// pass forever, and a probe that can only pass has not been shown to be able to detect
    /// anything -- which is the fault this repository has already found twice, in the surface
    /// alignment check and in the clock fix that passed with the fix removed.</para>
    /// <para>So the analysis is exercised here against synthetic sample sets built to be each
    /// of the three shapes it has to tell apart. No tablet involved, and the failing cases are
    /// the point: they are the evidence that a passing verdict on real hardware means
    /// something.</para>
    /// </remarks>
    private static int EpochProbe()
    {
        int failed = 0;

        // Shape 1: same epoch, free-running. pkTime is the tick count's low 32 bits, delayed by
        // a delivery latency that varies per packet the way a real one does.
        const uint baseRaw = 4_000_000_000;   // high enough that the (uint) cast is exercised
        var same = new List<(uint, long)>();
        for (int i = 0; i < 400; i++)
        {
            uint raw = baseRaw + (uint)(i * 6);
            long latency = 2 + (i % 7);        // 2..8 ms, never negative
            same.Add((raw, raw + latency));
        }
        failed += ExpectVerdict("probe/same-epoch", same, expectPass: true);

        // Shape 1b: the same clock, but with pkTime occasionally a millisecond AHEAD of the tick
        // count. This is not a corner case, it is what the hardware does: GetTickCount64 steps
        // by about 15.6 ms while pkTime is finer, so a packet stamped just after a tick reads
        // ahead of it. The first version of the analysis subtracted in unsigned arithmetic, so
        // -1 ms came back as 4294967295 and the real run was declared unanchorable. Without this
        // case the suite passed while the probe was wrong about the one machine it ran on.
        var ahead = new List<(uint, long)>();
        for (int i = 0; i < 400; i++)
        {
            uint raw = baseRaw + (uint)(i * 6);
            long latency = (i % 5 == 0) ? -1 : 2 + (i % 7);
            ahead.Add((raw, raw + latency));
        }
        failed += ExpectVerdict("probe/tick-lags-pktime", ahead, expectPass: true);

        // Shape 2: a different origin. pkTime counts from context open, so it starts near zero
        // while the machine has been up for days. Offsets are huge but perfectly stable, which
        // is what makes this the case a stability check alone would wave through.
        var other = new List<(uint, long)>();
        for (int i = 0; i < 400; i++)
        {
            uint raw = (uint)(i * 6);
            other.Add((raw, 500_000_000L + raw + 3));
        }
        failed += ExpectVerdict("probe/foreign-epoch", other, expectPass: false);

        // Shape 3: same origin, but the counter only advances while packets arrive. Continuous
        // stretches look identical to shape 1; the five-second pause is where it separates, and
        // it is why the probe asks for one.
        var stalled = new List<(uint, long)>();
        long tick = baseRaw;
        uint pk = baseRaw;
        for (int i = 0; i < 400; i++)
        {
            if (i == 200) tick += 5000;        // the pause: wall clock moves, pkTime does not
            pk += 6;
            tick += 6;
            stalled.Add((pk, tick + 3));
        }
        failed += ExpectVerdict("probe/stalls-when-idle", stalled, expectPass: false);

        // Shape 4: too little data to say anything. A probe that renders a verdict on twelve
        // packets would be worse than one that declines to.
        failed += ExpectVerdict("probe/too-few-packets", [.. same.Take(12)], expectPass: false);

        return failed;
    }

    private static int ExpectVerdict(string name, IReadOnlyList<(uint Raw, long Tick)> samples,
                                     bool expectPass)
    {
        var sink = new StringWriter();
        int code = WintabEpochProbe.Report(sink, samples);
        bool passed = code == 0;

        if (passed == expectPass)
        {
            Console.WriteLine($"[PASS] {name,-28} verdict {(passed ? "anchorable" : "not anchorable")}");
            return 0;
        }

        Console.WriteLine($"[FAIL] {name,-28} expected {(expectPass ? "anchorable" : "not anchorable")}, got the other");
        foreach (var line in sink.ToString().Split('\n'))
            Console.WriteLine($"           | {line.TrimEnd()}");
        return 1;
    }

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
