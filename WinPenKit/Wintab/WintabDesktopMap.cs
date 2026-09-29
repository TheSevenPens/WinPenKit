using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// Moves a position from the desktop the Wintab driver describes onto the desktop in physical
/// pixels, which is what <see cref="PenPoint.DesktopX"/> promises.
/// </summary>
/// <remarks>
/// <para>
/// <b>The driver's desktop is not always the real one.</b> On a desktop whose monitors use
/// different display scaling, the Wacom driver sends positions that are not physical pixels, in
/// both the system and the digitizer context -- asking for raw tablet counts does not avoid it,
/// the counts are distorted by the same factor. Windows Ink is not affected, because Windows maps
/// its positions itself. Nothing the driver reports reveals the distortion: its screen
/// (<c>lcSysExt</c>) is the same whatever the caller's DPI awareness, and it sent positions past
/// the width it reports.
/// </para>
/// <para>
/// <b>The rule, as measured.</b> Across a 24-step session on 28 Sep 2026 (a 3840x2160 monitor
/// above a 2560x1600 one; testdata/mapping-wizard-2026-09-28), with the tablet mapped to a single
/// display the driver's positions were the physical ones multiplied by <i>primary monitor
/// scaling / lowest monitor scaling</i>, uniformly over the desktop. So the correction is the
/// inverse: <i>lowest / primary</i>. It passed every single-display and every equal-scaling step
/// within 1.1 px (20 of 24). When the primary monitor is the lowest-scaled one, the factor is 1:
/// the driver was already right, and a correction would be the bug. The "primary" is its current
/// scaling, not the system DPI Windows fixed at sign-in -- a stale system DPI made no difference.
/// </para>
/// <para>
/// <b>What it does not cover.</b> With the tablet mapped to all displays on a mixed-scaling
/// desktop, the driver rescales parts of the non-primary monitors and not others, and no model of
/// which yet fits the data (issue #132). This rule leaves those positions as wrong as they were.
/// WinTab does not report whether the tablet is mapped to one display or all of them, so the rule
/// cannot tell the two apart.
/// </para>
/// <para>
/// <b>Scoped, and switchable.</b> It applies only when the monitors' scalings differ and the
/// driver's screen is not the physical desktop -- a driver whose screen already matches is taken
/// at its word. <c>WINPENKIT_WINTAB_DESKTOP_MAP=off</c> in the environment turns it off for a
/// process, for a driver that turns out to follow another rule. Which map is active, and why, is
/// written to the Wintab log and to the session's <c>DebugInfo</c>.
/// </para>
/// <para>
/// <b>History.</b> The first version scaled by the physical desktop's height over the driver's.
/// On the layout it was measured on that equalled lowest / primary, by coincidence; with the
/// primary monitor scaled lowest it put the pen 420 to 590 px off, where the driver had been
/// right. Before that, a per-monitor remap assumed the driver saw a system-DPI-aware desktop, and
/// was right on one monitor only.
/// </para>
/// </remarks>
internal sealed class WintabDesktopMap
{
    internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    /// <summary>One monitor in physical pixels, with its effective DPI.</summary>
    internal readonly record struct Monitor(Box Bounds, uint Dpi, bool Primary);

    /// <summary>The environment variable that turns the correction off.</summary>
    public const string OverrideVariable = "WINPENKIT_WINTAB_DESKTOP_MAP";

    // Tolerance, in pixels, when comparing the driver's rectangle with the physical desktop.
    private const int Slack = 2;

    private readonly double _scale;

    public static WintabDesktopMap Identity { get; } = new(1, "identity");

    /// <summary>What was decided and why, for the session log.</summary>
    public string Description { get; }

    public bool IsIdentity => _scale == 1;

    private WintabDesktopMap(double scale, string description)
    {
        _scale = scale;
        Description = description;
    }

    /// <summary>
    /// Builds the map for the driver's screen rectangle, from this machine's monitors.
    /// </summary>
    public static WintabDesktopMap ForDriverScreen(int orgX, int orgY, int extX, int extY)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(OverrideVariable), "off", StringComparison.OrdinalIgnoreCase))
            return new(1, $"identity: turned off by {OverrideVariable}=off");

        var monitors = ReadMonitors();
        if (monitors is null || monitors.Count == 0)
            return new(1, "identity: could not read the monitor layout");

        var driver = new Box(orgX, orgY, orgX + Math.Abs(extX), orgY + Math.Abs(extY));
        return FromLayout(driver, monitors);
    }

    /// <summary>The decision itself, from a layout supplied rather than read. Testable.</summary>
    internal static WintabDesktopMap FromLayout(Box driver, IReadOnlyList<Monitor> monitors)
    {
        if (monitors.Count == 0) return new(1, "identity: no monitors");

        var physical = new Box(
            monitors.Min(m => m.Bounds.Left), monitors.Min(m => m.Bounds.Top),
            monitors.Max(m => m.Bounds.Right), monitors.Max(m => m.Bounds.Bottom));
        string scalings = string.Join("/", monitors.Select(m => $"{m.Dpi * 100 / 96}%{(m.Primary ? " (primary)" : "")}"));

        if (monitors.Select(m => m.Dpi).Distinct().Count() == 1)
            return new(1, $"identity: every monitor at the same scaling ({scalings})");

        if (Near(driver, physical))
            return new(1, $"identity: the driver's screen {driver} is the physical desktop");

        var primary = monitors.FirstOrDefault(m => m.Primary);
        if (primary.Dpi == 0)
            return new(1, "identity: no primary monitor found");

        uint lowest = monitors.Min(m => m.Dpi);
        double scale = (double)lowest / primary.Dpi;
        if (scale == 1)
            return new(1, $"identity: the primary monitor has the lowest scaling ({scalings}), " +
                          "where the driver's positions are already physical");

        return new(scale,
            $"scaled by {scale:F4}, lowest over primary scaling ({scalings}); the driver's screen {driver} " +
            $"is not the physical desktop {physical}. Correct for the tablet mapped to one display; " +
            "mapped to all displays, parts of the other monitors stay wrong (issue #132)");
    }

    /// <summary>A position on the driver's desktop, as physical pixels.</summary>
    /// <remarks>
    /// About the origin, which is the primary monitor's top-left corner: the driver's scaling was
    /// measured to be uniform about that point.
    /// </remarks>
    public (double X, double Y) ToPhysical(double x, double y)
        => _scale == 1 ? (x, y) : (x * _scale, y * _scale);

    private static bool Near(Box a, Box b)
        => Math.Abs(a.Left - b.Left) <= Slack && Math.Abs(a.Top - b.Top) <= Slack
        && Math.Abs(a.Right - b.Right) <= Slack && Math.Abs(a.Bottom - b.Bottom) <= Slack;

    // ── Reading the layout ───────────────────────────────────────

    /// <summary>
    /// The monitors in physical pixels with their effective DPI, read with this thread made
    /// per-monitor aware for the call so the answer does not depend on the host's own awareness.
    /// A DPI-unaware thread would be told 96 for every monitor.
    /// </summary>
    private static List<Monitor>? ReadMonitors()
    {
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero) return null;

        try
        {
            var list = new List<Monitor>();
            MonitorEnumProc callback = (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(h, ref info) && GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0)
                {
                    var r = info.rcMonitor;
                    list.Add(new Monitor(new Box(r.left, r.top, r.right, r.bottom), dpi, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                return null;
            GC.KeepAlive(callback);
            return list;
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
