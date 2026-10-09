using System.Text;
using WinPenKit.Diagnostics;

namespace WinPenKit.TestConsole;

internal static class WindowsDeviceInfoSelfTest
{
    internal static int Run()
    {
        int failed = 0;
        void Check(string name, bool passed) { Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] Windows metadata: {name}"); if (!passed) ++failed; }
        var container = Guid.NewGuid();
        WindowsDeviceInfo Node(string id, Guid? group = null, bool? unique = true) =>
            new(id, null, null, null, null, group, null, unique, null, null, null, null, null, null);
        var root = Node(@"USB\VID_056A&PID_03FD\SERIAL-A", container);
        var child = Node(@"HID\VID_056A&PID_03FD&MI_00\CHILD", container) with { DriverProvider = "Wacom", DriverVersion = "4.0.0.4" };
        var other = Node(@"USB\VID_056A&PID_03FD\SERIAL-B", Guid.NewGuid());
        var nodes = new[] { root, child, other };
        var result = WindowsDeviceMatcher.Match("serial-a", nodes);
        Check("whole serial, related container and per-node driver", result.Status == WindowsDeviceMatchStatus.MatchedUsbSerial
            && result.Candidates.Count == 1 && result.ContainerDevices.Count == 2 && result.ContainerDevices[1].DriverVersion == "4.0.0.4");
        Check("case-insensitive exact instance", WindowsDeviceMatcher.Match(child.InstanceId.ToLowerInvariant(), nodes).Status == WindowsDeviceMatchStatus.MatchedInstanceId);
        Check("no substring match", WindowsDeviceMatcher.Match("SERIAL", nodes).Status == WindowsDeviceMatchStatus.NotFound);
        Check("hardware ID is not instance ID", WindowsDeviceMatcher.Match(@"USB\VID_056A&PID_03FD", nodes).Status == WindowsDeviceMatchStatus.NotFound);
        var duplicate = other with { InstanceId = @"USB\VID_1234&PID_ABCD\SERIAL-A" };
        result = WindowsDeviceMatcher.Match("SERIAL-A", [root, child, duplicate]);
        Check("duplicate serial stays ambiguous", result.Status == WindowsDeviceMatchStatus.Ambiguous && result.Candidates.Count == 2 && result.ContainerDevices.Count == 0);
        Check("exact instance takes priority", WindowsDeviceMatcher.Match(root.InstanceId, [root, duplicate]).Status == WindowsDeviceMatchStatus.MatchedInstanceId);
        Check("USB interface suffix is not serial", WindowsDeviceMatcher.Match("SERIAL-A", [Node(@"USB\VID_056A&PID_03FD&MI_00\SERIAL-A")]).Status == WindowsDeviceMatchStatus.NotFound);
        Check("generated/nonunique instance is not serial", WindowsDeviceMatcher.Match("SERIAL-A", [root with { HasUniqueInstanceId = false }]).Status == WindowsDeviceMatchStatus.NotFound);
        Check("missing uniqueness capability is not serial", WindowsDeviceMatcher.Match("SERIAL-A", [root with { HasUniqueInstanceId = null }]).Status == WindowsDeviceMatchStatus.NotFound);
        Check("missing container does not group unrelated nodes", WindowsDeviceMatcher.Match("SERIAL-A", [root with { ContainerId = null }, other with { ContainerId = null }]).ContainerDevices.Count == 1);
        Check("zero container does not group unrelated nodes", WindowsDeviceMatcher.Match("SERIAL-A", [root with { ContainerId = Guid.Empty }, other with { ContainerId = Guid.Empty }]).ContainerDevices.Count == 1);
        Check("missing identifier", WintabDiagnostics.QueryWindowsDevice(null).Status == WindowsDeviceMatchStatus.MissingIdentifier);
        var usb = WindowsDeviceMatcher.UsbIds([@"USB\VID_056A&PID_03FD", @"USB\VID_056A&PID_03FD&REV_0104&MI_00"]);
        Check("USB hardware identifiers and revision", usb == ((ushort?)0x056a, (ushort?)0x03fd, (ushort?)0x0104));
        Check("malformed hardware IDs ignored", WindowsDeviceMatcher.UsbIds([@"USB\VID_056A&PID_ZZZZ"]).Vendor is null);
        Check("conflicting hardware IDs unavailable", WindowsDeviceMatcher.UsbIds([@"USB\VID_056A&PID_03FD", @"USB\VID_056A&PID_0001"]).Vendor is null);
        Check("MULTI_SZ decode", WindowsDeviceReader.DecodeStringList(Encoding.Unicode.GetBytes("one\0two\0\0")) is { Count: 2 });
        Check("unterminated/odd strings rejected", WindowsDeviceReader.DecodeString([65, 0]) is null && WindowsDeviceReader.DecodeString([65, 0, 0]) is null);
        Check("bad MULTI_SZ rejected", WindowsDeviceReader.DecodeStringList(Encoding.Unicode.GetBytes("one\0")) is null);
        int calls = 0;
        var data = WindowsDeviceReader.ReadProperty(buffer =>
        {
            ++calls;
            if (buffer is null) return (false, 122, 0x12u, 2u);
            if (buffer.Length == 2) return (false, 122, 0x12u, 4u);
            Encoding.Unicode.GetBytes("A\0").CopyTo(buffer, 0);
            return (true, 0, 0x12u, 4u);
        }, 0x12);
        Check("growing property retried safely", calls == 3 && WindowsDeviceReader.DecodeString(data) == "A");
        Check("wrong property type unavailable", WindowsDeviceReader.ReadProperty(b => b is null ? (false, 122, 7u, 4u) : (true, 0, 7u, 4u), 0x12) is null);
        Check("oversized property rejected", WindowsDeviceReader.ReadProperty(_ => (false, 122, 0x12u, uint.MaxValue), 0x12) is null);
        Check("property disappears", WindowsDeviceReader.ReadProperty(b => b is null ? (false, 122, 0x12u, 4u) : (false, 1168, 0u, 0u), 0x12) is null);
        Console.WriteLine($"RESULT Windows metadata {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
