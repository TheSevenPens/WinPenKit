using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WintabContexts;

/// <summary>
/// Everything this tool asks the driver, and the one thing it asks Windows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here opens a context.</b> Every call is <c>WTInfoA</c>, which only reads. A tool for
/// counting contexts that took one of its own would be adding to the number it is reporting, and
/// on a screen recording that is the difference between a demonstration and a confusion.
/// </para>
/// <para>
/// The counts come from WinPenKit's own <c>WintabDiagnostics</c> rather than from a second copy
/// here, so that what this window shows and what the library reports in a log cannot drift apart.
/// The descriptive strings are read here, because a library has no reason to expose them.
/// </para>
/// </remarks>
internal static class Wintab
{
    private const uint WTI_INTERFACE = 1, WTI_STATUS = 2, WTI_DEVICES = 100;
    private const uint IFC_WINTABID = 1, IFC_SPECVERSION = 2, IFC_IMPLVERSION = 3, IFC_NDEVICES = 4;
    private const uint STA_SYSCTXS = 2;
    private const uint DVC_NAME = 1;

    [DllImport("Wintab32.dll", CharSet = CharSet.Ansi)]
    private static extern uint WTInfoA(uint category, uint index, StringBuilder output);

    [DllImport("Wintab32.dll")]
    private static extern uint WTInfoA(uint category, uint index, out uint output);

    /// <summary>Whether Wintab is installed at all.</summary>
    /// <remarks>
    /// A machine with no tablet driver, or with a driver that does not provide Wintab, throws on
    /// the first call rather than returning anything. That is a normal answer for this tool to
    /// give, not an error to show a stack trace for.
    /// </remarks>
    public static bool IsPresent
    {
        get
        {
            try
            {
                return Text(WTI_INTERFACE, IFC_WINTABID) is not null;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>The name the implementation gives itself, e.g. "Wintab Digitizer Services".</summary>
    public static string Implementation => Text(WTI_INTERFACE, IFC_WINTABID) ?? "not reported";

    public static string SpecVersion => Version(IFC_SPECVERSION);

    public static string ImplVersion => Version(IFC_IMPLVERSION);

    /// <summary>System contexts open, which is a subset of the total.</summary>
    public static string SystemContexts => Number(WTI_STATUS, STA_SYSCTXS)?.ToString() ?? "not reported";

    /// <summary>Every device the driver lists, by the name it gives each one.</summary>
    /// <remarks>
    /// Asked of Wintab rather than of Windows, so it works for any vendor without this tool
    /// knowing any of their names. The answer is often generic -- Wacom says "WACOM Tablet"
    /// whatever is plugged in -- which is worth seeing rather than papering over.
    /// </remarks>
    public static string Devices
    {
        get
        {
            uint count = Number(WTI_INTERFACE, IFC_NDEVICES) ?? 0;
            if (count == 0) return "none reported";

            var names = new List<string>();
            for (uint i = 0; i < count; i++)
                names.Add(Text(WTI_DEVICES + i, DVC_NAME) ?? $"device {i}");

            return string.Join(", ", names);
        }
    }

    /// <summary>What Windows knows about the file, which is where the vendor's name is.</summary>
    /// <remarks>
    /// Every vendor installs its implementation as <c>Wintab32.dll</c> in the system directory, so
    /// this one file identifies whose driver is answering: a Huion machine has Huion's DLL under
    /// the same name. It is the single most useful field when the question is which vendor leaks.
    /// </remarks>
    public static FileVersionInfo? Library
    {
        get
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wintab32.dll");

            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path) : null;
        }
    }

    private static string Version(uint index) =>
        Number(WTI_INTERFACE, index) is { } v ? $"{v >> 8}.{v & 0xFF}" : "not reported";

    /// <summary>
    /// A number the driver reports, or null when it does not implement that question.
    /// </summary>
    /// <remarks>
    /// Null is not zero, and the difference matters. <c>WTInfoA</c> returns the number of bytes it
    /// wrote, so a question a driver does not answer writes nothing -- and a tool that printed 0
    /// for that would tell someone with an untested vendor's tablet that no contexts were open,
    /// which is exactly the wrong conclusion to hand them.
    /// </remarks>
    private static uint? Number(uint category, uint index)
    {
        try
        {
            return WTInfoA(category, index, out uint value) == 0 ? null : value;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static string? Text(uint category, uint index)
    {
        var buffer = new StringBuilder(256);
        return WTInfoA(category, index, buffer) == 0 ? null : buffer.ToString();
    }
}
