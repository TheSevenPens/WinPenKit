using System.Globalization;
using System.Text.RegularExpressions;

namespace WinPenKit.Diagnostics;

/// <summary>How a raw Wintab PnP identifier was correlated with present Windows devices.</summary>
public enum WindowsDeviceMatchStatus
{
    MissingIdentifier = 0,
    NotFound = 1,
    Ambiguous = 2,
    MatchedInstanceId = 3,
    MatchedUsbSerial = 4,
    Unavailable = 5,
}

/// <summary>A present Windows device node. Each node has its own installed driver.</summary>
/// <remarks>Nullable fields were unavailable. HardwareIds is null if unavailable. USB revision
/// is bcdDevice, not a promised firmware version. DriverVersion is the device-node INF version,
/// not the Wacom installer version. DriverDate is the INF date (UTC yyyy-MM-dd), not install time.</remarks>
public sealed record WindowsDeviceInfo(
    string InstanceId, string? FriendlyName, string? DeviceDescription, string? BusReportedName, string? Manufacturer,
    Guid? ContainerId, IReadOnlyList<string>? HardwareIds, bool? HasUniqueInstanceId,
    ushort? UsbVendorId, ushort? UsbProductId, ushort? UsbDeviceRevision,
    string? DriverProvider, string? DriverVersion, string? DriverDate);

/// <summary>A best-effort Windows correlation snapshot.</summary>
/// <param name="Status">Explicit match outcome. Serial matches are correlations, not guaranteed hardware identities.</param>
/// <param name="Candidates">Matching nodes. One on success, multiple on ambiguity, otherwise empty.</param>
/// <param name="ContainerDevices">On success, present nodes sharing the matched node's nonempty
/// ContainerId, including the matched node. Without a container, only the matched node.
/// Empty for unsuccessful/ambiguous matches. Windows containers can include hubs and attached
/// peripherals; this grouping is not proof that every member belongs to the tablet.</param>
public sealed record WindowsDeviceLookupResult(WindowsDeviceMatchStatus Status,
    IReadOnlyList<WindowsDeviceInfo> Candidates, IReadOnlyList<WindowsDeviceInfo> ContainerDevices);

public static partial class WintabDiagnostics
{
    /// <summary>Optionally resolves a raw PnP identifier (for example WintabDeviceInfo.PlugAndPlayId)
    /// against present Windows device nodes. Does not load Wintab or open a pen context.</summary>
    /// <remarks>Synchronous SetupAPI enumeration; call off the input/render thread. No cache is
    /// kept. Exact instance IDs take priority over whole USB serial matches. Serial matching
    /// requires a USB device root with CM_DEVCAP_UNIQUEID, and never matches hardware IDs,
    /// substrings or names. Missing properties and hot-plug can prevent correlation.</remarks>
    public static WindowsDeviceLookupResult QueryWindowsDevice(string? plugAndPlayId)
    {
        if (string.IsNullOrWhiteSpace(plugAndPlayId))
            return WindowsDeviceMatcher.Empty(WindowsDeviceMatchStatus.MissingIdentifier);
        try
        {
            var nodes = WindowsDeviceReader.Read();
            return nodes is null ? WindowsDeviceMatcher.Empty(WindowsDeviceMatchStatus.Unavailable)
                : WindowsDeviceMatcher.Match(plugAndPlayId, nodes);
        }
        catch (DllNotFoundException) { return WindowsDeviceMatcher.Empty(WindowsDeviceMatchStatus.Unavailable); }
        catch (EntryPointNotFoundException) { return WindowsDeviceMatcher.Empty(WindowsDeviceMatchStatus.Unavailable); }
    }
}

internal static class WindowsDeviceMatcher
{
    private static readonly Regex UsbRoot = new(@"\AUSB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\([^\\]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UsbHardware = new(@"\AUSB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&REV_([0-9A-F]{4}))?(?:&MI_[0-9A-F]{2})?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static WindowsDeviceLookupResult Empty(WindowsDeviceMatchStatus status) => new(status, [], []);

    internal static WindowsDeviceLookupResult Match(string? identifier, IReadOnlyList<WindowsDeviceInfo> nodes)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return Empty(WindowsDeviceMatchStatus.MissingIdentifier);
        var candidates = nodes.Where(n => string.Equals(n.InstanceId, identifier, StringComparison.OrdinalIgnoreCase)).ToArray();
        var status = WindowsDeviceMatchStatus.MatchedInstanceId;
        if (candidates.Length == 0)
        {
            candidates = nodes.Where(n => n.HasUniqueInstanceId == true
                && UsbRoot.Match(n.InstanceId) is { Success: true } match
                && string.Equals(match.Groups[1].Value, identifier, StringComparison.OrdinalIgnoreCase)).ToArray();
            status = WindowsDeviceMatchStatus.MatchedUsbSerial;
        }
        if (candidates.Length == 0) return Empty(WindowsDeviceMatchStatus.NotFound);
        if (candidates.Length > 1) return new(WindowsDeviceMatchStatus.Ambiguous, Array.AsReadOnly(candidates), []);
        var selected = candidates[0];
        var related = selected.ContainerId is { } id && id != Guid.Empty
            ? nodes.Where(n => n.ContainerId == id).ToArray() : candidates;
        return new(status, Array.AsReadOnly(candidates), Array.AsReadOnly(related));
    }

    internal static (ushort? Vendor, ushort? Product, ushort? Revision) UsbIds(IReadOnlyList<string>? hardwareIds)
    {
        ushort? vendor = null, product = null, revision = null;
        foreach (var id in hardwareIds ?? [])
        {
            var match = UsbHardware.Match(id);
            if (!match.Success) continue;
            ushort v = ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ushort p = ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (vendor.HasValue && (vendor != v || product != p)) return (null, null, null);
            vendor = v; product = p;
            if (match.Groups[3].Success)
            {
                ushort r = ushort.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (revision.HasValue && revision != r) return (null, null, null);
                revision = r;
            }
        }
        return (vendor, product, revision);
    }
}
