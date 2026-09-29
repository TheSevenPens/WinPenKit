using System.Runtime.InteropServices;

namespace WinPenKit.MappingWizard;

/// <summary>A monitor's scaling as Windows stores it: steps relative to the recommended one.</summary>
/// <param name="Device">The GDI device name, e.g. \\.\DISPLAY2.</param>
/// <param name="Min">The lowest step allowed, relative to the recommended (0) step.</param>
/// <param name="Current">The current step, relative to the recommended one.</param>
/// <param name="Max">The highest step allowed, relative to the recommended one.</param>
internal readonly record struct ScalingState(string Device, int Min, int Current, int Max)
{
    /// <summary>
    /// The recommended scaling's position in <see cref="Scaling.Percentages"/>. Windows puts the
    /// lowest allowed step at 100%, so the recommendation is as many places up as that step is
    /// below it.
    /// </summary>
    public int RecommendedIndex => -Min;

    public int CurrentPercent => Scaling.Percentages[Math.Clamp(RecommendedIndex + Current, 0, Scaling.Percentages.Length - 1)];

    public IEnumerable<int> Allowed =>
        Enumerable.Range(RecommendedIndex + Min, Max - Min + 1)
                  .Where(i => i >= 0 && i < Scaling.Percentages.Length)
                  .Select(i => Scaling.Percentages[i]);

    /// <summary>The relative step for a percentage, or null if the monitor doesn't offer it.</summary>
    public int? StepFor(int percent)
    {
        int index = Array.IndexOf(Scaling.Percentages, percent);
        if (index < 0) return null;
        int step = index - RecommendedIndex;
        return step >= Min && step <= Max ? step : null;
    }
}

/// <summary>
/// Reads and sets each monitor's scaling, the way Settings > Display does.
/// </summary>
/// <remarks>
/// <para><b>Undocumented, and the only way.</b> Windows has no public API for changing a
/// monitor's scaling. Settings uses two display-config requests with negative type numbers, -3
/// to read and -4 to set, which several open-source tools rely on. They are what this uses. If
/// a Windows update breaks them, reading fails and the wizard goes back to asking the person to
/// change scaling by hand.</para>
/// <para><b>Stored relative, which is why resolution moves it.</b> Windows keeps a monitor's
/// scaling as steps above or below the scaling it recommends, and the recommendation depends on
/// the resolution. Setting the resolution after the scaling therefore changes the scaling, so
/// the wizard always sets the resolution first.</para>
/// <para><b>Takes effect at once, but not everywhere.</b> Windows fixes the system DPI at
/// sign-in from the primary monitor. Changing the primary monitor's scaling here does not change
/// that, exactly as when it is done in Settings; the wizard records it for each step.</para>
/// </remarks>
internal static class Scaling
{
    /// <summary>The scalings Windows offers, in order. Relative steps index into this.</summary>
    public static readonly int[] Percentages = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Every active monitor's scaling, keyed by GDI device name. Null if unreadable.</summary>
    public static Dictionary<string, ScalingState>? Read()
    {
        var sources = ActiveSources();
        if (sources is null) return null;

        var result = new Dictionary<string, ScalingState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (adapter, id, device) in sources)
        {
            var get = new DPI_SCALE_GET
            {
                header = new DEVICE_INFO_HEADER { type = GET_DPI_SCALE, size = Marshal.SizeOf<DPI_SCALE_GET>(), adapterId = adapter, id = id },
            };
            if (DisplayConfigGetDeviceInfo(ref get) != 0) return null;
            result[device] = new ScalingState(device, get.minScaleRel, get.curScaleRel, get.maxScaleRel);
        }
        return result;
    }

    /// <summary>Sets a monitor's scaling to a percentage it allows. Returns an error, or null.</summary>
    public static string? Set(string device, int percent)
    {
        var sources = ActiveSources();
        var source = sources?.FirstOrDefault(s => string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.Value.Device is null) return $"{device} was not found.";

        var states = Read();
        if (states is null || !states.TryGetValue(device, out var state)) return "Could not read the current scaling.";
        if (state.StepFor(percent) is not { } step) return $"{device} doesn't offer {percent}%.";

        return SetStep(source.Value.Adapter, source.Value.Id, step);
    }

    /// <summary>Puts a monitor back to a relative step read earlier.</summary>
    public static string? Restore(ScalingState earlier)
    {
        var source = ActiveSources()?.FirstOrDefault(s => string.Equals(s.Device, earlier.Device, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.Value.Device is null) return $"{earlier.Device} was not found.";
        return SetStep(source.Value.Adapter, source.Value.Id, earlier.Current);
    }

    private static string? SetStep(LUID adapter, uint id, int step)
    {
        var set = new DPI_SCALE_SET
        {
            header = new DEVICE_INFO_HEADER { type = SET_DPI_SCALE, size = Marshal.SizeOf<DPI_SCALE_SET>(), adapterId = adapter, id = id },
            scaleRel = step,
        };
        int result = DisplayConfigSetDeviceInfo(ref set);
        return result == 0 ? null : $"Windows refused the change (code {result}).";
    }

    /// <summary>The source (adapter and id) behind each active display, with its GDI name.</summary>
    private static List<(LUID Adapter, uint Id, string Device)>? ActiveSources()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return null;

        var paths = new PATH_INFO[pathCount];
        var modes = new MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;

        var list = new List<(LUID, uint, string)>();
        for (int i = 0; i < pathCount; i++)
        {
            var name = new SOURCE_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = GET_SOURCE_NAME, size = Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceAdapterId, id = paths[i].sourceId,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 && !list.Any(s => s.Item3 == name.viewGdiDeviceName))
                list.Add((paths[i].sourceAdapterId, paths[i].sourceId, name.viewGdiDeviceName));
        }
        return list;
    }

    // ── Win32 ────────────────────────────────────────────────────

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int GET_SOURCE_NAME = 1;
    private const int GET_DPI_SCALE = -3;
    private const int SET_DPI_SCALE = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public LUID sourceAdapterId;
        public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
        public LUID targetAdapterId;
        public uint targetId, targetModeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint targetStatusFlags;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] union;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_GET
    {
        public DEVICE_INFO_HEADER header;
        public int minScaleRel, curScaleRel, maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_SET
    {
        public DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH_INFO[] pathArray, ref uint modes, [Out] MODE_INFO[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_SCALE_GET info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SCALE_SET info);
}
