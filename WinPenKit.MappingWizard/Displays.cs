using System.Runtime.InteropServices;

namespace WinPenKit.MappingWizard;

/// <summary>A display mode: resolution and refresh rate.</summary>
internal readonly record struct DisplayMode(int Width, int Height, int Frequency)
{
    public override string ToString() => $"{Width}x{Height}";
    public double Aspect => (double)Width / Height;
}

/// <summary>
/// A monitor as this process sees it, which is in physical pixels: the process is
/// per-monitor DPI aware.
/// </summary>
internal sealed record Monitor(
    int Number, string Device, Rectangle Bounds, uint Dpi, bool Primary,
    DisplayMode Current, DisplayMode Largest, IReadOnlyList<DisplayMode> Modes)
{
    public int ScalePercent => (int)Math.Round(Dpi * 100 / 96.0);

    /// <summary>
    /// The largest mode the display offers, which is its native resolution on every panel this
    /// was tried on. Windows exposes a monitor's preferred mode only through the display-config
    /// API; the largest mode is the same answer for ordinary monitors and much simpler to get.
    /// </summary>
    public DisplayMode Native => Largest;

    public bool AtNative => Current.Width == Native.Width && Current.Height == Native.Height;

    /// <summary>
    /// The mode to test "not native" with: the largest mode below native with the same shape,
    /// so the picture is scaled but not stretched. Null if the display offers none.
    /// </summary>
    public DisplayMode? LowerMode()
    {
        var sameShape = Modes
            .Where(m => m.Width < Native.Width && Math.Abs(m.Aspect - Native.Aspect) < 0.01)
            .OrderByDescending(m => m.Width * m.Height)
            .ToList();
        if (sameShape.Count > 0) return sameShape[0];

        return Modes.Where(m => m.Width < Native.Width)
                    .OrderByDescending(m => m.Width * m.Height)
                    .Cast<DisplayMode?>()
                    .FirstOrDefault();
    }

    public string Describe() =>
        $"monitor {Number} ({Device.TrimStart('\\', '.')}) {Current} at ({Bounds.X},{Bounds.Y}), " +
        $"{ScalePercent}% scaling{(Primary ? ", primary" : "")}" +
        $"{(AtNative ? ", native" : $", native is {Native}")}";
}

/// <summary>Reads the monitors, and changes a display's resolution.</summary>
internal static class Displays
{
    public static uint SystemDpi => GetDpiForSystem();

    /// <summary>
    /// Every attached monitor, numbered from 1 left to right, then top to bottom. Given the
    /// monitors read earlier, keeps their numbers, so that changing a resolution -- which can
    /// move monitors -- does not renumber them in the middle of a plan.
    /// </summary>
    public static List<Monitor> Read(IReadOnlyList<Monitor>? earlier = null)
    {
        var found = new List<(string Device, Rectangle Bounds, uint Dpi, bool Primary)>();
        MonitorEnumProc callback = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref info))
            {
                GetDpiForMonitor(h, 0, out uint dpi, out _);
                var b = info.rcMonitor;
                found.Add((info.szDevice, Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom), dpi,
                           (info.dwFlags & 1) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        int next = (earlier?.Count > 0 ? earlier.Max(m => m.Number) : 0) + 1;
        return found
            .OrderBy(f => f.Bounds.X).ThenBy(f => f.Bounds.Y)
            .Select(f =>
            {
                int number = earlier?.FirstOrDefault(e => e.Device == f.Device)?.Number ?? next++;
                var modes = Modes(f.Device);
                var current = CurrentMode(f.Device) ?? new DisplayMode(f.Bounds.Width, f.Bounds.Height, 0);
                var largest = modes.Count > 0 ? modes.MaxBy(m => m.Width * m.Height) : current;
                return new Monitor(number, f.Device, f.Bounds, f.Dpi, f.Primary, current, largest, modes);
            })
            .OrderBy(m => m.Number)
            .ToList();
    }

    /// <summary>The distinct resolutions a display offers, at its current refresh rate where it has one.</summary>
    private static List<DisplayMode> Modes(string device)
    {
        var list = new List<DisplayMode>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettingsW(device, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel >= 24)
                list.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
        }
        return list
            .GroupBy(m => (m.Width, m.Height))
            .Select(g => g.MaxBy(m => m.Frequency))
            .ToList();
    }

    private static DisplayMode? CurrentMode(string device)
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm)
            ? new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency)
            : null;
    }

    /// <summary>
    /// Changes a display's resolution for this session only -- nothing is written to the
    /// registry, so a restart restores the saved settings whatever happens here. The caller
    /// confirms with the person and reverts if they do not keep it.
    /// </summary>
    public static string? SetResolution(string device, DisplayMode mode)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm))
            return "Could not read the display's current settings.";

        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (mode.Frequency > 0)
        {
            dm.dmDisplayFrequency = (uint)mode.Frequency;
            dm.dmFields |= DM_DISPLAYFREQUENCY;
        }

        int result = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        return result == DISP_CHANGE_SUCCESSFUL ? null : $"Windows refused the change (code {result}).";
    }

    /// <summary>Puts a display back to its saved settings, undoing <see cref="SetResolution"/>.</summary>
    public static void Restore(string device)
        => ChangeDisplaySettingsExW(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    // ── Win32 ────────────────────────────────────────────────────

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
}
