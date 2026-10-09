using System.Runtime.InteropServices;
using System.Text;
using WinPenKit.Diagnostics;

namespace WinPenKit.TestConsole;

// Tests the actual metadata reader with synthetic driver responses. No DLL or tablet needed.
internal static class WintabInfoSelfTest
{
    internal static int Run()
    {
        int failed = 0;
        void Check(string name, bool passed)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed) ++failed;
        }
        var fields = new Dictionary<(uint, uint), byte[]>();
        uint Query(uint category, uint index, IntPtr output)
        {
            if (category == 0) return 4096;
            if (!fields.TryGetValue((category, index), out var value)) return 0;
            if (output != IntPtr.Zero) Marshal.Copy(value, 0, output, value.Length);
            return (uint)value.Length;
        }
        static byte[] Text(string value) => Encoding.Unicode.GetBytes(value + "\0");
        fields[(1, 1)] = Text("Wacom テスト");
        fields[(1, 2)] = BitConverter.GetBytes((ushort)0x0104);
        fields[(1, 3)] = BitConverter.GetBytes((ushort)0x0610);
        fields[(1, 4)] = BitConverter.GetBytes(2u);
        fields[(100, 1)] = Text("Tablet A");
        fields[(100, 19)] = Text("RAW-PNP-A");
        fields[(101, 1)] = Text("Tablet B");
        var info = WintabInfoReader.Read(Query)!;
        Check("Unicode description", info.Identification == "Wacom テスト");
        Check("WORD versions use high/low bytes", info.SpecificationVersion == new Version(1, 4)
            && info.ImplementationVersion == new Version(6, 16));
        Check("all device indices and raw PnP IDs", info.Devices is { Count: 2 }
            && info.Devices[0].PlugAndPlayId == "RAW-PNP-A"
            && info.Devices[1].DeviceIndex == 1 && info.Devices[1].Name == "Tablet B"
            && info.Devices[1].PlugAndPlayId is null);
        Check("snapshot survives later queries", WintabInfoReader.Read(Query) != null
            && info.Devices![0].Name == "Tablet A");

        fields[(1, 2)] = new byte[] { 4 }; // incorrect scalar width
        fields.Remove((1, 3));
        fields[(100, 1)] = Encoding.Unicode.GetBytes("unterminated");
        fields[(100, 19)] = new byte[] { 65, 0, 0 }; // odd UTF-16 byte count
        fields[(101, 1)] = Text("");
        info = WintabInfoReader.Read(Query)!;
        Check("unavailable and malformed versions", info.SpecificationVersion is null && info.ImplementationVersion is null);
        Check("malformed strings unavailable; empty preserved", info.Devices![0].Name is null
            && info.Devices[0].PlugAndPlayId is null && info.Devices[1].Name == "");
        fields[(1, 4)] = BitConverter.GetBytes(0u);
        Check("zero devices", WintabInfoReader.Read(Query)!.Devices is { Count: 0 });
        fields.Remove((1, 4));
        Check("missing count distinct from zero", WintabInfoReader.Read(Query)!.Devices is null);
        fields[(1, 4)] = BitConverter.GetBytes(uint.MaxValue);
        Check("invalid count rejected", WintabInfoReader.Read(Query)!.Devices is null);
        Check("unresponsive driver", WintabInfoReader.Read((_, _, _) => 0) is null);
        Check("oversized category rejected", WintabInfoReader.Read((_, _, _) => uint.MaxValue) is null);
        Check("oversized field rejected", WintabInfoReader.Read((c, _, _) => c == 0 ? 4096u : uint.MaxValue)!.Identification is null);
        Check("driver disappears between size and data", WintabInfoReader.Read((c, _, p) =>
            c == 0 ? 4096u : p == IntPtr.Zero ? 4u : 0u)!.Identification is null);
        Check("oversized return rejected", WintabInfoReader.Read((c, _, p) =>
            c == 0 ? 4096u : p == IntPtr.Zero ? 4u : 4097u)!.Identification is null);
        Console.WriteLine($"RESULT {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
