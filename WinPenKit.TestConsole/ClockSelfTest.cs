using WinPenKit;

namespace WinPenKit.TestConsole;

/// <summary>
/// Checks <see cref="MillisecondCounter"/> against the wraps it exists for.
/// </summary>
/// <remarks>
/// <para>Run with <c>--selftest-clock</c>. It needs no tablet and no window, which is the point:
/// WPF's millisecond counter wraps once every 24.9 days and Wintab's once every 49.7, so
/// neither will ever be seen in ordinary use of this repository, and a bug in the handling
/// would sit undisturbed until someone's machine had been up long enough.</para>
/// <para>Every case below is a sequence the counter must survive, and the last two are failures
/// it must <i>not</i> invent -- a wrap detector that fires on ordinary input is worse than none,
/// because it corrupts every stroke instead of one every few weeks.</para>
/// </remarks>
public static class ClockSelfTest
{
    private const long IntWrapEnd = int.MaxValue;          // WPF: last value before the sign flip
    private const long IntWrapStart = int.MinValue;        // WPF: first value after it
    private const long UintWrapEnd = uint.MaxValue;        // Wintab: last value before zero

    public static int Run()
    {
        int failed = 0;

        // WPF. An int counter passes int.MaxValue and continues negative, a backward jump of
        // the full 2^32. Elapsed time across it is 3ms, and must read as 3ms.
        failed += Case("wpf-int-wrap",
            [IntWrapEnd - 2, IntWrapEnd - 1, IntWrapEnd, IntWrapStart, IntWrapStart + 1],
            expectedDeltasUs: [1000, 1000, 1000, 1000]);

        // Wintab. A uint counter returns to zero, a backward jump of 2^32 - 1.
        failed += Case("wintab-uint-wrap",
            [UintWrapEnd - 2, UintWrapEnd - 1, UintWrapEnd, 0, 1],
            expectedDeltasUs: [1000, 1000, 1000, 1000]);

        // A session that starts after the machine has been up 25 days sees a negative value
        // first. The origin is unstated, so that is allowed; what matters is that it does not
        // read as a wrap and does not go backwards.
        failed += Case("starts-past-the-wrap",
            [IntWrapStart, IntWrapStart + 5, IntWrapStart + 9],
            expectedDeltasUs: [5000, 4000]);

        // Ordinary input, nowhere near a boundary.
        failed += Case("ordinary",
            [1000, 1016, 1031, 1047],
            expectedDeltasUs: [16000, 15000, 16000]);

        // A coarse clock repeating a value is normal -- WPF does it on every stroke. A zero
        // delta is a reading, not a wrap.
        failed += Case("repeated-values",
            [5000, 5000, 5016, 5016],
            expectedDeltasUs: [0, 16000, 0]);

        // A small backward step is not a wrap either. Nothing in a packet stream should move
        // backward at all, but if one did, adding 2^32 to it would turn a 2ms oddity into a
        // 49-day one.
        failed += Case("small-backward-step-is-not-a-wrap",
            [9000, 8998],
            expectedDeltasUs: [-2000]);

        Console.WriteLine(failed == 0 ? "RESULT clock checks passed" : $"RESULT {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static int Case(string name, long[] raw, long[] expectedDeltasUs)
    {
        var clock = new MillisecondCounter();
        var got = new long[raw.Length];
        for (int i = 0; i < raw.Length; i++) got[i] = clock.Next(raw[i]);

        for (int i = 0; i < expectedDeltasUs.Length; i++)
        {
            long actual = got[i + 1] - got[i];
            if (actual != expectedDeltasUs[i])
            {
                Console.WriteLine($"[FAIL] {name,-34} delta {i}: expected {expectedDeltasUs[i]}us, got {actual}us");
                return 1;
            }
        }

        Console.WriteLine($"[PASS] {name,-34} {raw.Length} readings, deltas as expected");
        return 0;
    }
}
