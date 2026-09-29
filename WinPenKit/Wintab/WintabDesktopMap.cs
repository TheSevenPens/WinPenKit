using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// Moves a position from the desktop the Wintab driver describes onto the desktop in physical
/// pixels, which is what <see cref="PenPoint.DesktopX"/> promises.
/// </summary>
/// <remarks>
/// <para>
/// <b>The driver's desktop is not always the real one.</b> Measured on 28 Sep 2026 with a Wacom
/// driver, a 3840x2160 monitor at 250% above a 2560x1600 one at 225%: the real desktop is
/// 3840x3760, and the driver reports its screen as 3840x4178. The answer was the same whatever
/// DPI awareness the calling process had, so it cannot be fixed by choosing a different one.
/// </para>
/// <para>
/// <b>What the driver actually sends is one scale, not what it reports.</b> Compared against the
/// cursor across both monitors and both kinds of context, every position was the physical one
/// times 250/225 -- 0.900 back again on each axis, on each monitor -- and a system context sent X
/// values up to 4241, past the 3840 the driver claims as its width. So the reported width cannot
/// be trusted and the height can: 3760 / 4178 is exactly the factor measured. A per-monitor
/// remap, which assumed the driver saw the desktop as a system-DPI-aware process does, was right
/// for the lower monitor and left the upper one 10% off; it is gone.
/// </para>
/// <para>
/// Taken as physical pixels, those positions put the pen about 11% further from the desktop
/// origin than it was, the error growing toward the right and bottom. Windows Ink was not
/// affected, because Windows maps its positions itself; Clip Studio Paint shows the same offset.
/// </para>
/// <para>
/// <b>Recognised, not assumed.</b> The scale applies only when the driver's screen is not the
/// physical desktop. A driver that reports physical pixels passes through unchanged, and so does
/// a height ratio too far from 1 to be a scaling difference -- a guess applied to a driver that
/// was right would be the same bug in the other direction. The rule comes from one machine; a
/// layout where the height ratio is not the driver's scale would need measuring the same way.
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

    // Tolerance, in pixels, when comparing the driver's rectangle with the physical desktop.
    private const int Slack = 2;

    // Scaling steps are 25% apart at most, and the widest mixed setup is on the order of 100%
    // against 350%. A ratio outside this is not a scaling difference.
    private const double MinScale = 0.25, MaxScale = 4.0;

    private readonly Box _driver;
    private readonly Box _physical;
    private readonly double _scale;

    public static WintabDesktopMap Identity { get; } = new(default, default, 1, "identity");

    /// <summary>What was decided and why, for the session log.</summary>
    public string Description { get; }

    public bool IsIdentity => _scale == 1;

    private WintabDesktopMap(Box driver, Box physical, double scale, string description)
    {
        _driver = driver;
        _physical = physical;
        _scale = scale;
        Description = description;
    }

    /// <summary>
    /// Builds the map for the driver's screen rectangle, from this machine's monitors.
    /// </summary>
    public static WintabDesktopMap ForDriverScreen(int orgX, int orgY, int extX, int extY)
    {
        var physical = ReadPhysicalDesktop();
        if (physical is null)
            return new(default, default, 1, "identity: could not read the monitor layout");

        var driver = new Box(orgX, orgY, orgX + Math.Abs(extX), orgY + Math.Abs(extY));
        return FromLayout(driver, physical.Value);
    }

    /// <summary>The decision itself, from a layout supplied rather than read. Testable.</summary>
    internal static WintabDesktopMap FromLayout(Box driver, Box physical)
    {
        if (Near(driver, physical))
            return new(driver, physical, 1, $"identity: the driver's screen {driver} is the physical desktop");

        if (driver.Height <= 0 || physical.Height <= 0)
            return new(driver, physical, 1, $"identity: the driver's screen {driver} has no height");

        double scale = (double)physical.Height / driver.Height;
        if (scale < MinScale || scale > MaxScale)
            return new(driver, physical, 1,
                $"identity: the driver's screen {driver} and the physical desktop {physical} " +
                $"differ by {scale:F3}, which is not a scaling difference");

        return new(driver, physical, scale,
            $"scaled: the driver's screen {driver} is not the physical desktop {physical}; " +
            $"positions scaled by {scale:F4}, the ratio of their heights");
    }

    /// <summary>A position on the driver's desktop, as physical pixels.</summary>
    public (double X, double Y) ToPhysical(double x, double y)
    {
        if (_scale == 1) return (x, y);
        return (_physical.Left + (x - _driver.Left) * _scale,
                _physical.Top + (y - _driver.Top) * _scale);
    }

    private static bool Near(Box a, Box b)
        => Math.Abs(a.Left - b.Left) <= Slack && Math.Abs(a.Top - b.Top) <= Slack
        && Math.Abs(a.Right - b.Right) <= Slack && Math.Abs(a.Bottom - b.Bottom) <= Slack;

    // ── Reading the layout ───────────────────────────────────────

    /// <summary>
    /// The union of the monitors in physical pixels, read with this thread made per-monitor
    /// aware for the call so the answer does not depend on the host's own DPI awareness.
    /// </summary>
    private static Box? ReadPhysicalDesktop()
    {
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero) return null;

        try
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            MonitorEnumProc callback = (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(h, ref info))
                {
                    l = Math.Min(l, info.rcMonitor.left); t = Math.Min(t, info.rcMonitor.top);
                    r = Math.Max(r, info.rcMonitor.right); b = Math.Max(b, info.rcMonitor.bottom);
                }
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || l > r)
                return null;
            GC.KeepAlive(callback);
            return new Box(l, t, r, b);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

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
}
