using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>A device entry reported by the Wintab driver, not proof of a connected tablet.</summary>
/// <param name="DeviceIndex">Driver-local index used in WTI_DEVICES + index. Not a persistent ID.</param>
/// <param name="Name">Raw DVC_NAME description; may contain model/revision text. Null if unavailable.</param>
/// <param name="PlugAndPlayId">Raw DVC_PNPID; some Wacom devices return a tablet serial number here,
/// but that meaning is not guaranteed. Null if unavailable.</param>
public sealed record WintabDeviceInfo(uint DeviceIndex, string? Name, string? PlugAndPlayId);

/// <summary>A best-effort snapshot of public Wintab identification data.</summary>
/// <param name="Identification">Raw IFC_WINTABID description, or null if unavailable.</param>
/// <param name="SpecificationVersion">Wintab specification major/minor version, or null.</param>
/// <param name="ImplementationVersion">Wintab implementation major/minor version, or null.
/// This is not the full installed driver package version.</param>
/// <param name="Devices">Read-only device entries; null if the count is unavailable or invalid,
/// empty if the driver reports zero devices. Entries can include retained or virtual devices.</param>
/// <remarks>No standard dedicated fields exist for model number, firmware versions or Wacom
/// Center's Customized value. Device descriptions are preserved without parsing those values.</remarks>
public sealed record WintabInfo(string? Identification, Version? SpecificationVersion,
    Version? ImplementationVersion, IReadOnlyList<WintabDeviceInfo>? Devices);

public static partial class WintabDiagnostics
{
    /// <summary>Queries device identification and Wintab versions without opening a context.</summary>
    /// <returns>Null if the driver cannot be loaded or does not respond; otherwise a snapshot
    /// whose unsupported or malformed fields are null.</returns>
    /// <remarks>Works independently of both Wintab session modes. Refresh after device/driver
    /// changes. Multiple WTInfo calls are not an atomic snapshot. Requires the standard WTInfoW
    /// export; a driver exposing only WTInfoA is unavailable to this query.</remarks>
    public static WintabInfo? QueryInfo()
    {
        try { return WintabInfoReader.Read(WintabNative.WTInfoW); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }
}

// The delegate keeps decoding and optional-field behavior testable without a tablet driver.
internal sealed class WintabInfoReader(Func<uint, uint, IntPtr, uint> query, uint largestCategory)
{
    private const uint MaximumBytes = 1024 * 1024;

    internal static WintabInfo? Read(Func<uint, uint, IntPtr, uint> query)
    {
        uint largest = query(0, 0, IntPtr.Zero);
        if (largest == 0 || largest > MaximumBytes) return null;
        var reader = new WintabInfoReader(query, largest);
        string? identification = reader.Text(WTI.INTERFACE, IFC.WINTABID);
        Version? spec = reader.Version(IFC.SPECVERSION);
        Version? impl = reader.Version(IFC.IMPLVERSION);
        var countBytes = reader.Bytes(WTI.INTERFACE, IFC.NDEVICES);
        IReadOnlyList<WintabDeviceInfo>? devices = null;
        if (countBytes is { Length: sizeof(uint) })
        {
            uint count = BitConverter.ToUInt32(countBytes);
            // WTI_DEVICES occupies categories 100..199; 200 begins WTI_CURSORS.
            if (count <= 100)
            {
                var entries = new List<WintabDeviceInfo>((int)count);
                for (uint i = 0; i < count; ++i)
                    entries.Add(new(i, reader.Text(WTI.DEVICES + i, DVC.NAME),
                        reader.Text(WTI.DEVICES + i, DVC.PNPID)));
                devices = entries.AsReadOnly();
            }
        }
        return new(identification, spec, impl, devices);
    }

    private Version? Version(uint index)
    {
        var bytes = Bytes(WTI.INTERFACE, index);
        if (bytes is not { Length: sizeof(ushort) }) return null;
        ushort packed = BitConverter.ToUInt16(bytes);
        return new(packed >> 8, packed & 0xff);
    }

    private string? Text(uint category, uint index)
    {
        var bytes = Bytes(category, index);
        if (bytes is null || bytes.Length % 2 != 0) return null;
        // Search only within the returned length; never read past an unterminated string.
        for (int i = 0; i < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0)
                return System.Text.Encoding.Unicode.GetString(bytes, 0, i);
        return null;
    }

    private unsafe byte[]? Bytes(uint category, uint index)
    {
        uint required = query(category, index, IntPtr.Zero);
        if (required == 0 || required > MaximumBytes) return null;
        // Reserve at least the largest category, including for WORD/UINT values. WTInfo has
        // no capacity argument; it relies on a conforming driver honoring its reported sizes.
        byte[] buffer = new byte[(int)Math.Max(required, largestCategory)];
        Array.Fill(buffer, (byte)0xff);
        fixed (byte* pointer = buffer)
        {
            uint written = query(category, index, (IntPtr)pointer);
            if (written == 0 || written > buffer.Length) return null;
            return buffer.AsSpan(0, (int)written).ToArray();
        }
    }
}
