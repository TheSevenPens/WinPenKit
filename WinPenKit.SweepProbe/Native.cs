using System.Runtime.InteropServices;

namespace WinPenKit.SweepProbe;

/// <summary>The few Win32 and Wintab calls the probe makes itself, so it reports the system and the
/// driver and not what a WinPenKit session chose to keep.</summary>
internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDevices(ref uint count, [Out] PointerDeviceInfo[]? devices);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDeviceRects(IntPtr device, out Rect deviceRect, out Rect displayRect);

    [DllImport("Wintab32.dll", CharSet = CharSet.Ansi, EntryPoint = "WTInfoA")]
    private static extern uint WTInfoA(uint category, uint index, IntPtr output);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PointerDeviceInfo
    {
        public uint Orientation;
        public IntPtr Device;
        public int Type;
        public IntPtr Monitor;
        public uint StartingCursorId;
        public ushort MaxContacts;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string Product;
    }

    /// <summary>0 unaware, 1 system, 2 per-monitor. Anything but 2 means the coordinates below are scaled.</summary>
    internal static int DpiAwareness() => GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

    /// <summary>Every pointer device Windows knows, with the rectangles it maps each onto.</summary>
    internal static IEnumerable<string> PointerDevices()
    {
        uint count = 0;
        GetPointerDevices(ref count, null);

        var all = new PointerDeviceInfo[count];
        GetPointerDevices(ref count, all);

        foreach (var device in all)
        {
            var has = GetPointerDeviceRects(device.Device, out var on, out var display);

            yield return has
                ? $"{TypeName(device.Type)} '{device.Product}': device rect {on.Width} x {on.Height}, display rect {display.Width} x {display.Height}"
                : $"{TypeName(device.Type)} '{device.Product}': no rects";
        }
    }

    private static string TypeName(int type) => type switch
    {
        1 => "integrated pen",
        2 => "external pen",
        3 => "touch",
        4 => "touch pad",
        _ => $"type {type}",
    };

    /// <summary>One of the driver's default contexts: the tablet range, and the desktop range it maps to.</summary>
    internal static string WintabContext(uint category, string name)
    {
        var buffer = Marshal.AllocHGlobal(512);

        try
        {
            for (var each = 0; each < 512; each++) Marshal.WriteByte(buffer, each, 0);

            if (WTInfoA(category, 0, buffer) == 0) return $"{name}: none";

            // LOGCONTEXTA: a 40-byte name, eleven uints, then InOrg, InExt, OutOrg, OutExt (three ints
            // each), three sensitivities, SysMode, SysOrg, SysExt.
            int At(int offset) => Marshal.ReadInt32(buffer, offset);

            return $"{name}: in ({At(96)} x {At(100)}), out ({At(120)} x {At(124)}), "
                + $"sys origin ({At(148)}, {At(152)}) size ({At(156)} x {At(160)})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The size of one device axis, from WTI_DEVICES: counts, unit and resolution.</summary>
    internal static string WintabAxis(uint index, string name)
    {
        var buffer = Marshal.AllocHGlobal(64);

        try
        {
            if (WTInfoA(100, index, buffer) == 0) return $"{name}: none";

            var (min, max) = (Marshal.ReadInt32(buffer, 0), Marshal.ReadInt32(buffer, 4));
            var units = (uint)Marshal.ReadInt32(buffer, 8);
            var resolution = (uint)Marshal.ReadInt32(buffer, 12) / 65536.0;

            return $"{name}: {min}..{max}, units {units} (1 inch, 2 cm), {resolution} counts per unit";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
