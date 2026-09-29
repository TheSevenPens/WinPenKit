using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// Checks where the Wintab sessions put the pen against where Windows puts the cursor, on every
/// monitor and in every Wintab mode.
/// </summary>
/// <remarks>
/// <para><b>Why this needs its own probe.</b> The driver describes its screen with numbers that
/// can be wrong. On 28 Sep 2026, on a desktop of a 250% monitor above a 225% one, a Huion (V20) driver
/// reported its screen as 3840x4178 against a real 3840x3760, sent X values past the width it
/// reported, and scaled every position by 250/225 -- so the pen drew up to ~400 px from the nib
/// while Windows Ink was exact. Nothing the session reads from the driver can reveal that. The
/// cursor can: while the pen is in pen mode the driver moves the cursor itself, and Windows puts
/// the cursor on the physical desktop correctly. So the cursor is ground truth, and this compares
/// against it.</para>
/// <para><b>What a run needs.</b> The tablet in pen (absolute) mode, and the pen hovered -- not
/// tapped -- slowly over every monitor, corners included, in each mode. A tap activates whatever
/// window is under the nib, and Wintab delivers packets only to the foreground application. In
/// mouse mode the cursor is not ground truth and the verdict means nothing.</para>
/// <para><b>Reading the numbers.</b> The cursor is read when packets are drained, a little after
/// the driver stamped them, so a moving pen trails the cursor by a few pixels in the direction of
/// travel. Hovering in every direction cancels that out of the <b>mean</b>, which is what the
/// verdict is judged on; the mean of the absolute differences keeps it, and is reported only to
/// show how much of the difference is that lag. The fit per axis -- cursor as a linear function of
/// the raw packet value -- is the driver's actual mapping, and is what to compare when a
/// layout fails.</para>
/// <para>Kept, like <see cref="WintabEpochProbe"/>, because the answer is a fact about one driver
/// on one layout. The run that found the problem is in
/// <c>testdata/wintab-mapping-probe-mixed-dpi.csv</c>: it fails on all four mode and monitor
/// pairs. That driver turned out to be Huion's (V20); Wacom's passed every configuration, and
/// WinPenKit takes Wacom as the reference and does not correct for other drivers (issue #132).</para>
/// </remarks>
public static class WintabMappingProbe
{
    /// <summary>One packet, with the cursor read beside it.</summary>
    /// <param name="Monitor">Index into the monitor list of the monitor under the cursor, or -1.</param>
    public readonly record struct Sample(
        InputApi Mode, int RawX, int RawY, double DesktopX, double DesktopY,
        int CursorX, int CursorY, int Monitor);

    /// <summary>A monitor in physical pixels, with its scaling.</summary>
    public readonly record struct MonitorInfo(
        string Device, int Left, int Top, int Right, int Bottom, uint Dpi, bool Primary)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>Samples needed on a monitor, in a mode, for a verdict on it.</summary>
    private const int MinSamples = 100;

    /// <summary>The largest mean difference, per axis, that passes.</summary>
    private const double MaxMeanError = 3.0;

    /// <summary>How long to wait for the pen in each mode before giving up on it.</summary>
    private static readonly TimeSpan PenWait = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs each available Wintab mode in turn, collecting for <paramref name="perMode"/> from the
    /// first packet, then reports. Writes every sample to <paramref name="csvPath"/> if given.
    /// </summary>
    /// <remarks>
    /// The calling thread is made per-monitor aware for the run, so the cursor and the monitor
    /// list are in physical pixels -- the space <see cref="PenPoint.DesktopX"/> promises. The host
    /// process should be made per-monitor aware too, before it creates any window, so the probe's
    /// window sits where it says it does.
    /// </remarks>
    public static int Run(TextWriter output, TimeSpan perMode, string? csvPath = null)
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        output.WriteLine("WINTAB MAPPING PROBE");

        var apis = PenSessionFactory.GetAvailableApis();
        var modes = new[] { InputApi.WintabSystem, InputApi.WintabDigitizer }.Where(apis.Contains).ToArray();
        if (modes.Length == 0)
        {
            output.WriteLine("[SKIP] wintab/available        no Wintab context offered; is the tablet attached?");
            output.WriteLine("RESULT skipped, needs a tablet");
            return 2;
        }

        var monitors = ReadMonitors();
        WriteSetup(output, monitors);

        output.WriteLine();
        output.WriteLine("  For each mode: hover the pen -- do not tap -- slowly over EVERY monitor,");
        output.WriteLine("  corners included, moving in every direction. Each mode starts counting");
        output.WriteLine($"  when the pen first appears and runs {perMode.TotalSeconds:F0}s. Lift the pen away");
        output.WriteLine("  and bring it back when the next mode is announced.");
        output.WriteLine();
        output.Flush();

        var samples = new List<Sample>();

        foreach (var mode in modes)
        {
            using var session = PenSessionFactory.Create(mode);
            if (session is not WintabSessionBase wintab)
                continue;

            string? error = session.Start();
            if (error != null)
            {
                output.WriteLine($"[FAIL] {mode}/start  {error}");
                continue;
            }

            using var window = ProbeWindow.Show(wintab.PumpWindowHandle, monitors,
                $"WinPenKit - Wintab mapping probe - {mode} - hover over every monitor");

            output.WriteLine($"[INFO] {mode}: waiting for the pen...");
            output.Flush();

            var waitUntil = DateTime.UtcNow + PenWait;
            DateTime? until = null;
            int before = samples.Count;

            while (DateTime.UtcNow < (until ?? waitUntil))
            {
                var points = session.DrainPoints();
                if (points.Length > 0)
                {
                    GetCursorPos(out POINT c);
                    int monitor = monitors.FindIndex(m => m.Contains(c.X, c.Y));
                    foreach (var pt in points)
                        samples.Add(new Sample(mode, pt.RawX, pt.RawY, pt.DesktopX, pt.DesktopY, c.X, c.Y, monitor));

                    if (until is null)
                    {
                        until = DateTime.UtcNow + perMode;
                        output.WriteLine($"[INFO] {mode}: pen seen, collecting for {perMode.TotalSeconds:F0}s");
                        output.Flush();
                    }
                }
                Thread.Sleep(2);
            }

            if (until is null)
                output.WriteLine($"[INFO] {mode}: no pen within {PenWait.TotalMinutes:F0} minutes, skipped");
            else
                output.WriteLine($"[INFO] {mode}: {samples.Count - before} packets");
        }

        if (csvPath != null)
        {
            WriteCsv(csvPath, samples);
            output.WriteLine($"[INFO] samples written to      {csvPath}");
        }

        output.WriteLine();

        return Report(output, monitors, samples);
    }

    /// <summary>
    /// The analysis, over samples already collected. Public so it can be run against a saved
    /// CSV, and against synthetic input.
    /// </summary>
    public static int Report(TextWriter output, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<Sample> samples)
    {
        int passed = 0, failed = 0, missing = 0;

        foreach (var mode in samples.Select(s => s.Mode).Distinct())
        {
            for (int m = 0; m < monitors.Count; m++)
            {
                var mon = monitors[m];
                var set = samples.Where(s => s.Mode == mode && s.Monitor == m).ToList();
                string id = $"{mode}/monitor{m + 1}";

                if (set.Count < MinSamples)
                {
                    output.WriteLine($"[MISS] {id,-28} {set.Count} samples, need {MinSamples} -- hover over {mon.Device} too");
                    missing++;
                    continue;
                }

                double meanDx = set.Average(s => s.DesktopX - s.CursorX);
                double meanDy = set.Average(s => s.DesktopY - s.CursorY);
                double absDx = set.Average(s => Math.Abs(s.DesktopX - s.CursorX));
                double absDy = set.Average(s => Math.Abs(s.DesktopY - s.CursorY));
                var (ax, bx) = Fit(set.Select(s => ((double)s.RawX, (double)s.CursorX)));
                var (ay, by) = Fit(set.Select(s => ((double)s.RawY, (double)s.CursorY)));

                // How much of the monitor the pen covered. A mean over the middle of a monitor
                // passes a scale error that only shows toward its far edges.
                double coverX = (set.Max(s => s.CursorX) - set.Min(s => s.CursorX) + 1) / (double)mon.Width;
                double coverY = (set.Max(s => s.CursorY) - set.Min(s => s.CursorY) + 1) / (double)mon.Height;

                bool ok = Math.Abs(meanDx) <= MaxMeanError && Math.Abs(meanDy) <= MaxMeanError;
                if (ok) passed++; else failed++;

                output.WriteLine(Invariant(
                    $"[{(ok ? "PASS" : "FAIL")}] {id,-28} mean error ({meanDx:F1}, {meanDy:F1}) px over {set.Count} samples"));
                output.WriteLine(Invariant(
                    $"       mean |error| ({absDx:F1}, {absDy:F1}) px -- includes cursor lag while moving"));
                output.WriteLine(Invariant(
                    $"       driver's mapping: cursorX = {ax:F6} * rawX + {bx:F1},  cursorY = {ay:F6} * rawY + {by:F1}"));
                output.WriteLine(Invariant(
                    $"       covered {coverX:P0} x {coverY:P0} of the monitor{(coverX < 0.8 || coverY < 0.8 ? " -- reach the edges for a sharper verdict" : "")}"));
            }
        }

        output.WriteLine();
        if (passed + failed == 0)
        {
            output.WriteLine("RESULT nothing to judge -- no monitor had enough samples");
            return 1;
        }

        output.WriteLine(failed == 0
            ? "VERDICT the pen lands under the cursor on every monitor measured."
            : "VERDICT the pen does NOT land under the cursor. Compare the driver's mapping\n"
            + "        above with the monitor layout to see what rule the driver follows.");
        output.WriteLine($"RESULT {passed}/{passed + failed} passed{(missing > 0 ? $", {missing} not measured" : "")}");
        return failed == 0 && missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// This machine's monitors, in physical pixels if the caller is per-monitor aware. A saved
    /// CSV numbers its monitors in this order, so a later report is only meaningful on the same
    /// layout it was collected on.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> CurrentMonitors() => ReadMonitors();

    /// <summary>Reads samples written by <see cref="Run"/> back, for a later report.</summary>
    public static List<Sample> ReadCsv(string path)
    {
        var result = new List<Sample>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 8 || !Enum.TryParse<InputApi>(p[0], out var mode)) continue;
            result.Add(new Sample(mode,
                int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture),
                double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture),
                int.Parse(p[5], CultureInfo.InvariantCulture), int.Parse(p[6], CultureInfo.InvariantCulture),
                int.Parse(p[7], CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static void WriteCsv(string path, IReadOnlyList<Sample> samples)
    {
        var sb = new StringBuilder("mode,rawX,rawY,desktopX,desktopY,cursorX,cursorY,monitor\n");
        foreach (var s in samples)
            sb.AppendLine(Invariant($"{s.Mode},{s.RawX},{s.RawY},{s.DesktopX:F2},{s.DesktopY:F2},{s.CursorX},{s.CursorY},{s.Monitor}"));
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteSetup(TextWriter output, IReadOnlyList<MonitorInfo> monitors)
    {
        output.WriteLine($"[INFO] tablet                  {WintabDiagnostics.DeviceName() ?? "(no name)"}");
        output.WriteLine($"[INFO] system DPI              {GetDpiForSystem()} ({GetDpiForSystem() * 100 / 96}%)");
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            string primary = m.Primary ? ", primary" : "";
            output.WriteLine(Invariant(
                $"[INFO] monitor{i + 1}                {m.Device} {m.Width}x{m.Height} at ({m.Left},{m.Top}), {m.Dpi} DPI ({m.Dpi * 100 / 96}%){primary}"));
        }
        output.WriteLine($"[INFO] driver's screen         {WintabDiagnostics.DriverScreen() ?? "(unreadable)"}");
    }

    private static (double Slope, double Intercept) Fit(IEnumerable<(double X, double Y)> points)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in points) { n++; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double d = n * sxx - sx * sx;
        if (d == 0) return (0, n > 0 ? sy / n : 0);
        double slope = (n * sxy - sx * sy) / d;
        return (slope, (sy - slope * sx) / n);
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static List<MonitorInfo> ReadMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc callback = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref info))
            {
                GetDpiForMonitor(h, 0, out uint dpi, out _);
                list.Add(new MonitorInfo(info.szDevice, info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right, info.rcMonitor.Bottom, dpi, (info.dwFlags & 1) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    // ── The window packets are delivered to ──────────────────────

    /// <summary>
    /// Shows the session's hidden pump window so the probe holds the foreground, as
    /// <see cref="WintabEpochProbe"/> does, and hides it again on dispose.
    /// </summary>
    private sealed class ProbeWindow : IDisposable
    {
        private readonly IntPtr _hwnd;

        private ProbeWindow(IntPtr hwnd) => _hwnd = hwnd;

        public static ProbeWindow Show(IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors, string title)
        {
            if (hwnd == IntPtr.Zero) return new ProbeWindow(hwnd);

            SetWindowTextW(hwnd, title);
            SetWindowLongPtrW(hwnd, GWL_STYLE, (IntPtr)(WS_OVERLAPPEDWINDOW | WS_VISIBLE));

            // Small, and in a corner of the primary monitor, so it covers as little as possible
            // of what the pen has to reach.
            var primary = monitors.FirstOrDefault(m => m.Primary);
            SetWindowPos(hwnd, HWND_TOPMOST, primary.Left + 40, primary.Top + 40, 900, 200,
                         SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
            return new ProbeWindow(hwnd);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
        }
    }

    // ── Win32 ────────────────────────────────────────────────────

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int GWL_STYLE = -16;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_VISIBLE = 0x10000000;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
}
