using System.Runtime.InteropServices;
using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// Establishes what origin Wintab's <c>pkTime</c> is counted from, by reading it against
/// <c>GetTickCount64</c> at the moment each packet arrives.
/// </summary>
/// <remarks>
/// <para><b>Why this needs its own probe.</b> <see cref="StrokeRecorder"/> writes times relative
/// to the first point, by design — a recording carries no machine uptime and is not meant to.
/// So no recording can answer this question, however many are taken. The value has to be read
/// before conversion and against a clock with a known origin, which is what
/// <c>WintabSessionBase.RawTimeObserver</c> exists for.</para>
/// <para><b>What turned on the answer, and what the answer was.</b> Wintab was the last backend
/// using <see cref="DeviceTickCounter"/>, which detects the 49.7-day wrap by watching for a
/// backward jump. Detection cannot see a wrap that happens while the session is stopped, or
/// across a gap where every packet was filtered out by a capture region; the difference across
/// such a gap was then wrong by 49.7 days. Anchoring has no such gap but needs a known origin,
/// and <c>pkTime</c> has none documented.</para>
/// <para>Run on a Wacom DTH246 on 13 Sep 2026, this probe found <c>pkTime</c> on the
/// <c>GetTickCount64</c> epoch: over 6217 packets spanning 41.7s and a deliberate pause, it
/// advanced 41703ms against 41703ms of wall clock, with the offset between them inside a 40ms
/// band. Both Wintab sessions now anchor, and that gap is closed. The readings are kept in
/// <c>testdata/wintab-epoch-probe.csv</c>; the probe stays because the answer is a fact about
/// one driver on one machine, and another tablet can be asked the same question.</para>
/// <para><b>Reading the offset.</b> Every observation overestimates it. The packet is stamped by
/// the driver at some instant and this code reads the tick count afterwards, so each sample
/// carries that packet's delivery latency and never less. The <b>minimum</b> over many packets
/// is the closest estimate of the true offset, which is why the report leads with it. The same
/// reasoning applies to the wall-clock calibration described in <c>Docs/HOW_TO_USE.md</c>.</para>
/// <para><b>The idle check matters as much as the offset.</b> A counter that advances only while
/// packets flow could still show a small offset on a continuous stroke and would be useless to
/// anchor against. Pausing mid-run separates the two: a free-running clock holds its offset
/// across the pause, a packet-driven one falls behind by the length of it.</para>
/// </remarks>
public static class WintabEpochProbe
{
    /// <summary>
    /// Below this, the report declines to render a verdict rather than render a weak one. At
    /// 180 Hz it is about a second of drawing, so it rules out a run where the pen never landed
    /// and not much else.
    /// </summary>
    private const int MinPackets = 200;

    private const int SW_SHOW = 5;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colorref);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int GWL_STYLE = -16;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_VISIBLE = 0x10000000;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Keeps the probe's window filled for the length of the run, on a background thread.
    /// </summary>
    /// <remarks>
    /// The pump's window procedure handles no WM_PAINT and its class has no background brush, so
    /// anything drawn into it is lost the moment something else overlaps it. Repainting on a
    /// timer is not how a window should work and is not worth fixing properly: this exists so a
    /// person can see where to put the pen for one diagnostic run.
    /// </remarks>
    private static void PaintLoop(IntPtr hwnd, TimeSpan duration)
    {
        var thread = new Thread(() =>
        {
            IntPtr brush = CreateSolidBrush(0x00E8D8C0); // BGR: a pale blue-grey
            var until = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < until)
            {
                IntPtr dc = GetDC(hwnd);
                if (dc != IntPtr.Zero)
                {
                    if (GetClientRect(hwnd, out RECT r)) FillRect(dc, ref r, brush);
                    ReleaseDC(hwnd, dc);
                }
                Thread.Sleep(250);
            }
            DeleteObject(brush);
        })
        { IsBackground = true, Name = "WinPenKit.EpochProbe.Paint" };
        thread.Start();
    }

    /// <summary>
    /// Opens a Wintab session, collects for <paramref name="duration"/>, and reports. It ends on
    /// a timer rather than on a keypress so that it runs the same way from a console, a script
    /// or a harness with no stdin attached.
    /// </summary>
    /// <param name="windowAt">
    /// Where to put the probe's window, in the coordinate space this process sees. It must land
    /// on the monitor being drawn on: pen contact activates whatever window is under the pen, so
    /// a window on another monitor loses the foreground the moment the pen touches down, and
    /// with it the packets. This process is DPI-unaware, so these are virtualised coordinates
    /// and will not match a per-monitor-aware tool's idea of where a monitor is. Null centres
    /// the window on the primary monitor, which is correct only for a single-monitor machine.
    /// </param>
    public static int Run(TextWriter output, TimeSpan duration, (int X, int Y)? windowAt = null)
    {
        var apis = PenSessionFactory.GetAvailableApis();
        var api = apis.Contains(InputApi.WintabDigitizer) ? InputApi.WintabDigitizer
                : apis.Contains(InputApi.WintabSystem) ? InputApi.WintabSystem
                : (InputApi?)null;

        output.WriteLine("WINTAB EPOCH PROBE");

        if (api is null)
        {
            output.WriteLine("[SKIP] wintab/available        no Wintab context offered; is the tablet attached?");
            output.WriteLine("RESULT skipped, needs a tablet");
            return 2;
        }

        using var session = PenSessionFactory.Create(api.Value);

        // The observer lives on the concrete Wintab session, not on IPenSession: it is a
        // diagnostic on one backend's internals and has no meaning for the others.
        if (session is not WintabSessionBase wintab)
        {
            output.WriteLine("[FAIL] wintab/session-type     factory returned a non-Wintab session");
            output.WriteLine("RESULT 0/1 passed");
            return 1;
        }

        var samples = new List<(uint Raw, long Tick)>();
        wintab.RawTimeObserver = (raw, tick) =>
        {
            lock (samples) samples.Add((raw, tick));
        };

        string? error = session.Start();
        if (error != null)
        {
            output.WriteLine($"[FAIL] wintab/start            {error}");
            output.WriteLine("RESULT 0/1 passed");
            return 1;
        }

        // Wintab delivers to the foreground application. A console host has no window of its
        // own, so without this the probe collects nothing and the report is indistinguishable
        // from one where nobody drew -- which is exactly how this was first misread.
        IntPtr pumpWindow = wintab.PumpWindowHandle;
        bool shown = false;
        if (pumpWindow != IntPtr.Zero)
        {
            SetWindowTextW(pumpWindow, "WinPenKit - Wintab epoch probe - draw here, then pause");

            // The pump creates its window with style 0 and no background brush, because it was
            // only ever meant to receive messages. Shown as-is it is a rectangle that nothing
            // ever paints and that has no frame to locate it by: present in the taskbar,
            // invisible on every monitor. Give it a caption and a border so it can be found.
            SetWindowLongPtrW(pumpWindow, GWL_STYLE, (IntPtr)(WS_OVERLAPPEDWINDOW | WS_VISIBLE));

            var at = windowAt ?? (GetSystemMetrics(SM_CXSCREEN) / 4, GetSystemMetrics(SM_CYSCREEN) / 4);
            SetWindowPos(pumpWindow, IntPtr.Zero, at.X, at.Y, 1200, 800,
                         SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            ShowWindow(pumpWindow, SW_SHOW);
            shown = SetForegroundWindow(pumpWindow);

            // One fill, so the client area is a visible surface rather than whatever was behind
            // it. There is no WM_PAINT handler to keep it filled, so it is repainted on a timer
            // for the life of the run -- crude, and enough for a diagnostic to be aimed at.
            PaintLoop(pumpWindow, duration);
        }

        output.WriteLine($"[INFO] api                     {api}");
        output.WriteLine($"[INFO] collecting for          {duration.TotalSeconds:F0}s");
        if (pumpWindow != IntPtr.Zero && GetWindowRect(pumpWindow, out RECT wr))
            output.WriteLine($"[INFO] window rect             {wr.Left},{wr.Top} to {wr.Right},{wr.Bottom}"
                           + (shown ? "" : "  (foreground NOT taken -- expect zero packets)"));
        else
            output.WriteLine("[INFO] window rect             none -- expect zero packets");
        output.WriteLine();
        output.WriteLine("  Draw on the tablet, PAUSE for about five seconds without touching it,");
        output.WriteLine("  then draw again. The pause is the part that matters: it separates a");
        output.WriteLine("  free-running clock from one that only advances when packets arrive.");
        output.WriteLine();
        output.Flush();

        Thread.Sleep(duration);

        List<(uint Raw, long Tick)> taken;
        lock (samples) taken = [.. samples];

        return Report(output, taken);
    }

    /// <summary>
    /// Replays recorded readings through the conversion a Wintab session actually performs, and
    /// checks the result is a usable clock.
    /// </summary>
    /// <remarks>
    /// <para>The anchoring change lives in the packet handler, which no self test reaches: it
    /// needs a tablet and a person. This covers the arithmetic on that path against real
    /// readings instead, which is the part that could be wrong. It does not cover the wiring --
    /// that the handler calls this function at all, with these arguments -- and saying otherwise
    /// would be the overclaim this repository keeps catching.</para>
    /// <para>Two properties are checked. The output never decreases, which is the contract
    /// <c>PenPoint.TimestampMicroseconds</c> states. And each step equals the step in the raw
    /// readings, multiplied up, which says the anchoring neither invented a wrap nor missed
    /// one across the whole run.</para>
    /// </remarks>
    public static int VerifyAnchoring(TextWriter output, IReadOnlyList<(uint Raw, long Tick)> samples)
    {
        if (samples.Count < 2)
        {
            output.WriteLine("[FAIL] anchor/samples           need at least two readings");
            return 1;
        }

        long backward = 0, mismatched = 0, worstUs = 0;
        long previous = PenTimestamp.FromSystemTicks(samples[0].Raw, samples[0].Tick);

        for (int i = 1; i < samples.Count; i++)
        {
            long now = PenTimestamp.FromSystemTicks(samples[i].Raw, samples[i].Tick);
            if (now < previous) backward++;

            long expected = ((long)samples[i].Raw - samples[i - 1].Raw) * 1000L;
            long actual = now - previous;
            if (actual != expected)
            {
                mismatched++;
                if (Math.Abs(actual - expected) > Math.Abs(worstUs)) worstUs = actual - expected;
            }
            previous = now;
        }

        int failed = 0;
        if (backward == 0)
            output.WriteLine($"[PASS] anchor/never-decreases   {samples.Count} readings, no backward step");
        else { output.WriteLine($"[FAIL] anchor/never-decreases   {backward} backward steps"); failed++; }

        if (mismatched == 0)
            output.WriteLine($"[PASS] anchor/steps-match-raw   every step equals the raw step x1000");
        else { output.WriteLine($"[FAIL] anchor/steps-match-raw   {mismatched} steps differ, worst {worstUs}us"); failed++; }

        return failed;
    }

    /// <summary>
    /// The analysis, over samples already collected. Public so it can be exercised against
    /// synthetic input: a probe whose verdict has only ever been produced by one real run is
    /// not yet known to be able to produce the other verdict.
    /// </summary>
    public static int Report(TextWriter output, IReadOnlyList<(uint Raw, long Tick)> samples)
    {
        int passed = 0, total = 0;

        void Check(bool ok, string id, string detail)
        {
            total++;
            if (ok) passed++;
            output.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {id,-28} {detail}");
        }

        if (samples.Count < MinPackets)
        {
            output.WriteLine($"[FAIL] wintab/packets           {samples.Count}, need at least {MinPackets}");
            output.WriteLine("RESULT 0/1 passed -- draw for longer");
            return 1;
        }

        // The difference is taken in 32-bit unsigned arithmetic, so a pkTime that has wrapped
        // past the low half of TickCount64 still subtracts correctly, and then read back as
        // SIGNED before widening. Both halves matter. Without the wrap-safe subtraction a
        // reading across the 49.7-day boundary is nonsense; without the signed cast a genuinely
        // negative offset comes back as about 4.29 billion.
        //
        // Negative offsets are ordinary here, which is what makes the second half easy to miss.
        // GetTickCount64 advances in steps of about 15.6 ms while pkTime is finer, so a packet
        // stamped just after a tick reads ahead of it by a millisecond or two. The first version
        // of this code computed an unsigned difference and then tested it against a tolerance of
        // -50 ms, a comparison nothing could ever satisfy: the first real run reported a spread
        // of 4294967295 ms and declared the clock unanchorable. An unsigned value cannot carry a
        // negative offset, and giving it one to carry is the same fault as reading MaxPressure
        // 32767 as a level count.
        var offsets = new long[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            offsets[i] = unchecked((int)((uint)samples[i].Tick - samples[i].Raw));

        long min = offsets.Min();
        long max = offsets.Max();
        var sorted = offsets.Order().ToArray();
        long median = sorted[sorted.Length / 2];

        uint rawFirst = samples[0].Raw, rawLast = samples[^1].Raw;
        long tickFirst = samples[0].Tick, tickLast = samples[^1].Tick;

        output.WriteLine($"[INFO] packets                 {samples.Count}");
        output.WriteLine($"[INFO] pkTime                  {rawFirst} .. {rawLast}  (span {(long)rawLast - rawFirst} ms)");
        output.WriteLine($"[INFO] GetTickCount64          {tickFirst} .. {tickLast}  (span {tickLast - tickFirst} ms)");
        output.WriteLine($"[INFO] offset tick-pkTime      min {min} ms, median {median} ms, max {max} ms");
        output.WriteLine($"[INFO] pkTime advance           {(long)rawLast - rawFirst} ms against {tickLast - tickFirst} ms of wall clock");

        // Same epoch means the offset is a delivery latency: small, and never far below zero.
        // GetTickCount64 advances in steps of about 15.6 ms, so a packet stamped just after a
        // tick can read slightly negative. Anything beyond a couple of seconds is a different
        // origin -- a counter started at context open, at driver load, or at device attach.
        Check(min >= -50 && min <= 2000, "wintab/epoch-is-tickcount",
              $"minimum offset {min} ms; same epoch requires a small one, and a few ms either "
            + "side of zero is expected");

        // A free-running counter holds its offset. One that advances only while packets arrive
        // falls behind by the length of any pause, so the spread grows to match it.
        long spread = max - min;
        Check(spread <= 2000, "wintab/free-running",
              $"offset spread {spread} ms across the run, pause included");

        // Anchoring assumes a single origin for the whole session. Two origins would show as a
        // step in the offset, which the spread catches, but a backward pkTime would break the
        // conversion outright and is worth naming separately.
        bool monotonic = true;
        for (int i = 1; i < samples.Count && monotonic; i++)
            if (samples[i].Raw < samples[i - 1].Raw) monotonic = false;
        Check(monotonic, "wintab/monotonic", monotonic ? "pkTime never decreased" : "pkTime stepped backward");

        output.WriteLine();
        bool anchorable = passed == total;
        output.WriteLine(anchorable
            ? "VERDICT pkTime is counted on the GetTickCount64 epoch. Wintab can anchor its\n"
            + "        clock the way the framework backends do, and DeviceTickCounter's blind\n"
            + "        spot -- a wrap while stopped, or across a filtered gap -- closes."
            : "VERDICT pkTime is NOT on the GetTickCount64 epoch, or does not free-run.\n"
            + "        Wintab must keep detecting wraps rather than anchoring, and the blind\n"
            + "        spot documented on DeviceTickCounter stands.");

        output.WriteLine($"RESULT {passed}/{total} passed");
        return passed == total ? 0 : 1;
    }
}
