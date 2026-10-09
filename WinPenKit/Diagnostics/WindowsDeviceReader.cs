using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinPenKit.Diagnostics;

// SetupAPI is independent of Wintab and requires no PowerShell/WMI process or native WinPenKit DLL.
internal static class WindowsDeviceReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceData { internal uint Size; internal Guid ClassGuid; internal uint DevInst; internal UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id) { internal readonly Guid Format = format; internal readonly uint Id = id; }

    private static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DriverFormat = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly PropertyKey Instance = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    private static readonly PropertyKey Container = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey BusName = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    internal static IReadOnlyList<WindowsDeviceInfo>? Read()
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x04); // PRESENT | ALLCLASSES
        if (set == new IntPtr(-1)) return null;
        try
        {
            var nodes = new List<WindowsDeviceInfo>();
            for (uint index = 0; ; ++index)
            {
                var device = new DeviceData { Size = (uint)Marshal.SizeOf<DeviceData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device))
                    return Marshal.GetLastWin32Error() == 259 ? nodes.AsReadOnly() : null;
                byte[]? Property(PropertyKey key, uint type) => ReadProperty((byte[]? buffer) =>
                {
                    bool ok = SetupDiGetDevicePropertyW(set, ref device, in key, out uint actualType,
                        buffer, (uint)(buffer?.Length ?? 0), out uint size, 0);
                    return (ok, Marshal.GetLastWin32Error(), actualType, size);
                }, type);
                string? Text(PropertyKey key) => DecodeString(Property(key, 0x12));
                string? instance = Text(Instance);
                // An unreadable instance could hide a competing match; do not claim uniqueness.
                if (string.IsNullOrEmpty(instance)) return null;
                byte[]? containerBytes = Property(Container, 0x0d);
                Guid? container = containerBytes is { Length: 16 } ? new Guid(containerBytes) : null;
                byte[]? caps = Property(new(DeviceFormat, 17), 0x07);
                bool? unique = caps is { Length: 4 } ? (BitConverter.ToUInt32(caps) & 0x10) != 0 : null;
                var ids = DecodeStringList(Property(new(DeviceFormat, 3), 0x2012));
                var (vendor, product, revision) = WindowsDeviceMatcher.UsbIds(ids);
                string? date = null;
                byte[]? dateBytes = Property(new(DriverFormat, 2), 0x10);
                if (dateBytes is { Length: 8 })
                {
                    long fileTime = BitConverter.ToInt64(dateBytes);
                    try { if (fileTime > 0) date = DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
                    catch (ArgumentOutOfRangeException) { }
                }
                nodes.Add(new(instance, Text(new(DeviceFormat, 14)), Text(new(DeviceFormat, 2)), Text(BusName), Text(new(DeviceFormat, 13)),
                    container, ids, unique, vendor, product, revision,
                    Text(new(DriverFormat, 9)), Text(new(DriverFormat, 3)), date));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Size can change during a hot-plug. Retry with the updated size, but bound work/storage.
    internal static byte[]? ReadProperty(Func<byte[]?, (bool Ok, int Error, uint Type, uint Size)> get, uint expectedType)
    {
        byte[]? buffer = null;
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            var response = get(buffer);
            if (response.Ok)
                return buffer is not null && response.Type == expectedType && response.Size <= buffer.Length
                    ? buffer.AsSpan(0, (int)response.Size).ToArray() : null;
            if (response.Error != 122 || response.Size == 0 || response.Size > 1024 * 1024) return null;
            buffer = new byte[(int)response.Size];
        }
        return null;
    }

    internal static string? DecodeString(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[^1] != 0 || bytes[^2] != 0) return null;
        string value = Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
        return value.Contains('\0') ? null : value;
    }

    internal static IReadOnlyList<string>? DecodeStringList(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4 || bytes.Length % 2 != 0
            || bytes[^1] != 0 || bytes[^2] != 0 || bytes[^3] != 0 || bytes[^4] != 0) return null;
        string value = Encoding.Unicode.GetString(bytes, 0, bytes.Length - 4);
        if (value.Length == 0) return Array.Empty<string>();
        var values = value.Split('\0');
        return values.Any(v => v.Length == 0) ? null : Array.AsReadOnly(values);
    }

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceData device);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceData device, in PropertyKey key,
        out uint type, [Out] byte[]? buffer, uint capacity, out uint required, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
