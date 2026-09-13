using System.Runtime.InteropServices;
using System.Text;

namespace WinPenKit.Diagnostics;

/// <summary>
/// Machine-checkable acceptance checks for a pen application, and the report format they
/// print in.
/// </summary>
/// <remarks>
/// <para>The checks are grouped into levels, ordered so that <b>the first failure is the root
/// cause</b>. Level 0 is the environment, level 1 the drawing surface; neither needs a tablet,
/// a pen, or a person, which is what makes them worth automating. Levels 2 and 3 cover the pen
/// stream and the coordinate conversion and require input, so they are not part of a
/// launch-time self test.</para>
/// <para>Deliberately stops at level 1. Whether a stroke is antialiased, or varies with
/// pressure, is measurable from a captured bitmap in principle - but image-derived metrics
/// have a poor record at this: one such metric reported a canvas as clean when it was being
/// resampled, because it happened to scan the axis that had no error. Checks that hold are
/// automated; judgements about how a stroke looks belong to a person with a pen.</para>
/// <para>The output is line-oriented and stable so an agent or a script can parse it, and the
/// process exit code is 0 only when every check passes.</para>
/// </remarks>
public sealed class SelfTest
{
    private readonly List<(string Id, bool Pass, string Detail)> _results = [];

    /// <summary>The application being tested, printed in the header.</summary>
    public string AppName { get; init; } = "unknown";

    /// <summary>Records one check.</summary>
    /// <param name="id">Stable identifier, <c>L&lt;level&gt;.&lt;name&gt;</c>.</param>
    /// <param name="pass">Whether the check passed.</param>
    /// <param name="detail">The measurement, whether it passed or failed. A passing check
    /// that prints nothing is indistinguishable from a check that was never run.</param>
    public void Check(string id, bool pass, string detail) => _results.Add((id, pass, detail));

    /// <summary>Records a check that could not run, which counts as a failure.</summary>
    public void Skip(string id, string why) => _results.Add((id, false, $"could not run: {why}"));

    public bool AllPassed => _results.Count > 0 && _results.TrueForAll(r => r.Pass);

    /// <summary>The report, as printed.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SELFTEST {AppName}");

        int width = 0;
        foreach (var r in _results)
            if (r.Id.Length > width) width = r.Id.Length;

        int passed = 0;
        foreach (var (id, pass, detail) in _results)
        {
            if (pass) passed++;
            sb.AppendLine($"[{(pass ? "PASS" : "FAIL")}] {id.PadRight(width)}  {detail}");
        }

        sb.AppendLine($"RESULT {passed}/{_results.Count} passed");
        return sb.ToString();
    }

    // ── Level 0: environment ─────────────────────────────────────
    // None of this needs a pen. All of it invalidates everything downstream when wrong.

    /// <summary>
    /// Per-Monitor V2 awareness. Anything less and Win32 hands back virtualized coordinates
    /// that do not match what the pen reports, so every later measurement is of the wrong
    /// thing. A DPI-unaware process reading a window rect on a 216 DPI monitor sees it scaled
    /// by 96/216 and has no way to tell.
    /// </summary>
    public void CheckDpiAwareness()
    {
        const string id = "L0.dpi-awareness";
        try
        {
            if (GetThreadDpiAwarenessContext() is var ctx && ctx != IntPtr.Zero)
            {
                bool v2 = AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                string name =
                    v2 ? "PerMonitorV2"
                    : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) ? "PerMonitor (not V2)"
                    : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE) ? "System"
                    : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_UNAWARE) ? "Unaware"
                    : "unrecognised";
                Check(id, v2, name);
                return;
            }
            Skip(id, "GetThreadDpiAwarenessContext returned null");
        }
        catch (EntryPointNotFoundException)
        {
            Skip(id, "API unavailable (pre-Windows 10 1607)");
        }
    }

    /// <summary>
    /// Whether the client area sits entirely within one monitor's work area. Injected and
    /// real pen input are delivered by absolute screen position, so any part of the window
    /// hanging off its monitor - or under the taskbar - receives nothing. The window is still
    /// running and still painting, which makes it read as a bug in whatever is being tested.
    /// </summary>
    /// <param name="hwnd">The application window.</param>
    public void CheckWindowPlacement(IntPtr hwnd)
    {
        const string id = "L0.window-placement";
        if (hwnd == IntPtr.Zero) { Skip(id, "no window handle"); return; }

        if (!GetClientRect(hwnd, out RECT client)) { Skip(id, "GetClientRect failed"); return; }

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { Skip(id, "ClientToScreen failed"); return; }

        int w = client.Right - client.Left, h = client.Bottom - client.Top;

        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) { Skip(id, "GetMonitorInfo failed"); return; }

        var wa = mi.rcWork;
        bool inside = origin.X >= wa.Left && origin.Y >= wa.Top &&
                      origin.X + w <= wa.Right && origin.Y + h <= wa.Bottom;

        Check(id, inside,
            $"client {w}x{h} at {origin.X},{origin.Y}; work area " +
            $"{wa.Right - wa.Left}x{wa.Bottom - wa.Top} at {wa.Left},{wa.Top}" +
            (inside ? "" : "  <- input outside the work area is silently discarded"));
    }

    /// <summary>Records the display scale. Never fails; everything below is relative to it.</summary>
    public void ReportScale(double scale) =>
        Check("L0.scale", scale > 0, $"{scale:F2}x");

    // ── Level 1: surface ─────────────────────────────────────────
    // Still no pen needed. These are the checks that catch a canvas which is quietly rendering
    // at a fraction of the display's resolution, or landing between pixels.

    /// <summary>
    /// Whether the backing bitmap is the client area in <b>physical pixels</b>. A surface
    /// sized from a logical measurement is magnified to fit, and at 1.75x that is a canvas
    /// drawn at 57% of the display's resolution. No amount of coordinate precision survives
    /// it, and nothing about the coordinates looks wrong.
    /// </summary>
    /// <param name="bitmapWidth">Backing bitmap width in pixels.</param>
    /// <param name="bitmapHeight">Backing bitmap height in pixels.</param>
    /// <param name="logicalWidth">Canvas width in the framework's layout units.</param>
    /// <param name="logicalHeight">Canvas height in the framework's layout units.</param>
    /// <param name="scale">The ratio of the framework's layout unit to a device pixel -
    /// which is not always the display scale. WPF lays out in DIPs, so at 1.75x this is 1.75;
    /// a Per-Monitor V2 WinForms app lays out in physical pixels already, so it is 1.0 on the
    /// same display. Conflating the two makes this check pass on a broken surface.</param>
    public void CheckSurfacePhysical(
        int bitmapWidth, int bitmapHeight,
        double logicalWidth, double logicalHeight, double scale)
    {
        int wantW = (int)Math.Ceiling(logicalWidth * scale);
        int wantH = (int)Math.Ceiling(logicalHeight * scale);
        bool ok = bitmapWidth == wantW && bitmapHeight == wantH;

        Check("L1.surface-physical", ok,
            $"bitmap {bitmapWidth}x{bitmapHeight}, expected {wantW}x{wantH} " +
            $"(= ceil({logicalWidth:F0}x{logicalHeight:F0} logical x {scale:F2}))" +
            (ok ? "" : $"  <- rendering at {100.0 * bitmapWidth / wantW:F0}% of display resolution"));
    }

    /// <summary>
    /// Whether the surface lands on a whole device pixel. A fractional offset makes the
    /// framework resample the entire surface to draw it between pixel rows, softening every
    /// edge at once - while the coordinates and the resolution both still measure correct.
    /// </summary>
    /// <remarks>
    /// Both axes are reported separately because the error is routinely one-dimensional: a
    /// canvas measured here was aligned horizontally and 0.64px out vertically, so a check
    /// that scanned across a near-vertical stroke found nothing wrong.
    /// </remarks>
    public void CheckSurfaceAlignment(double originPxX, double originPxY)
    {
        double fx = Math.Abs(originPxX - Math.Round(originPxX));
        double fy = Math.Abs(originPxY - Math.Round(originPxY));
        bool ok = fx < 0.01 && fy < 0.01;

        string which = ok ? "" :
            "  <- fractional on " +
            (fx >= 0.01 && fy >= 0.01 ? "both axes" : fx >= 0.01 ? "x" : "y") +
            "; the whole surface is resampled to draw it";

        Check("L1.surface-alignment", ok, $"origin {originPxX:F2},{originPxY:F2}px{which}");
    }

    /// <summary>
    /// Whether the host the surface is presented in covers the same device pixels the surface
    /// holds. A correctly sized bitmap given a differently sized rect is scaled back off the
    /// pixel grid, which undoes the point of sizing it physically.
    /// </summary>
    /// <remarks>
    /// <para><b>This measures the host's size, not the rate the surface is sampled at.</b>
    /// Callers supply <paramref name="presentedPxWidth"/> from their host's layout size, and a
    /// host can cover exactly the right number of device pixels while drawing only part of the
    /// surface across them.</para>
    /// <para>That is not hypothetical. In issue 70 a 2700px bitmap sat in a host covering
    /// 2700 device pixels -- this check matched to the pixel and passed -- while the framework
    /// rendered the top-left 1200x600 pixels of it stretched across the whole host. Strokes
    /// landed 2.25 times too far from the canvas origin, and the check reported a pass before
    /// the fix and after it.</para>
    /// <para>What it does catch is a surface whose host is the wrong size, which is the fault
    /// in issue 37: a canvas sized in logical units and magnified to fit. That is worth
    /// keeping. It is simply a narrower claim than the name suggests.</para>
    /// <para>The sampling rate is measured by <see cref="PresentationProbe"/>, which reports
    /// as <c>L1.presentation-sampling</c>. It works in pixels rather than layout, and the
    /// application has to drive it across two frames, so it is a separate thing rather than a
    /// wider version of this one.</para>
    /// </remarks>
    public void CheckPresentation1To1(int bitmapWidth, int bitmapHeight,
                                      double presentedPxWidth, double presentedPxHeight)
    {
        bool ok = Math.Abs(presentedPxWidth - bitmapWidth) < 0.5 &&
                  Math.Abs(presentedPxHeight - bitmapHeight) < 0.5;

        Check("L1.presentation-1to1", ok,
            $"bitmap {bitmapWidth}x{bitmapHeight} presented at " +
            $"{presentedPxWidth:F1}x{presentedPxHeight:F1} device px" +
            (ok ? "" : "  <- magnified or shrunk on the way to the screen"));
    }

    // ── Console plumbing ─────────────────────────────────────────

    /// <summary>
    /// Where <see cref="Emit"/> left the report when it could not print it. Null when the
    /// report was printed, and null when writing the file also failed.
    /// </summary>
    /// <remarks>
    /// A caller that has a way to show the path -- a window title, a dialog, its own log --
    /// is the only thing that can put it in front of the person who launched the process.
    /// The library cannot: the one channel it has is the console, and this property only has
    /// a value in the case where the console did not work.
    /// </remarks>
    public string? ReportPath { get; private set; }

    /// <summary>
    /// Prints the report and returns the exit code: 0 when everything passed.
    /// </summary>
    /// <remarks>
    /// A GUI subsystem process has no console of its own, so this attaches to the parent's
    /// when there is one. Without that, running the self test from a terminal produces a
    /// silent success and nothing to read.
    /// </remarks>
    public int Emit()
    {
        bool attached = AttachConsole(ATTACH_PARENT_PROCESS);
        string text = Format();

        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // No console and no redirect. Fall through to the file below.
            attached = false;
        }

        // Measured on Windows 11, one build, three launch contexts:
        //
        //   context                    attached  IsOutputRedirected  stdout handle
        //   no console, no redirect    false     true                0
        //   stdout redirected to file  true      true                valid
        //   started from a console     true      false               valid
        //
        // So IsOutputRedirected is true in the case this branch exists for, and the guard it
        // used to have -- IsOutputRedirected == false -- was satisfied only in the third row,
        // where printing had already worked. The file was never written in the one case that
        // needed it. .NET reports an absent stdout as redirected, because the handle is not a
        // character device; only the handle itself separates "sent somewhere" from "sent
        // nowhere".
        IntPtr stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        bool nothingVisible = !attached
            && (stdout == IntPtr.Zero || stdout == new IntPtr(-1));

        if (nothingVisible)
        {
            // Nothing would have been visible. Leave it somewhere findable and say where.
            //
            // Saying where cannot go to the console: this branch runs precisely because the
            // console did not work. It goes to the debugger output instead, which DebugView
            // shows with no debugger attached, and to ReportPath, which is the only way a
            // caller with a window can put the path in front of a person.
            string path = Path.Combine(Path.GetTempPath(), $"selftest-{AppName}.txt");
            try
            {
                File.WriteAllText(path, text);
                ReportPath = path;
                OutputDebugString($"[{AppName}] self test report written to {path}" + Environment.NewLine);
            }
            catch (IOException ex)
            {
                // The last resort failed too. Announcing that is the difference between a
                // report that is hard to find and a run that produced nothing at all.
                OutputDebugString(
                    $"[{AppName}] self test could not write {path}: {ex.Message}" + Environment.NewLine);
            }
        }

        return AllPassed ? 0 : 1;
    }

    /// <summary>Whether the command line asks for a self test.</summary>
    public static bool Requested(string[] args) =>
        Array.Exists(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));

    // ── P/Invoke ─────────────────────────────────────────────────

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    // Not Debug.WriteLine: that is compiled out of a Release build, and a Release build is
    // what a sample ships as and what this branch exists for.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugString(string lpOutputString);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
