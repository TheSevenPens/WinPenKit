using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// P/Invoke declarations for Wintab32.dll.
/// Self-contained — no dependency on the WintabDN project.
/// Only the functions used by the session logic.
/// </summary>
internal static partial class WintabNative
{
    /// <summary>
    /// Returns true if Wintab32.dll is present and responds to WTInfoA.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            return WTInfoA(0, 0, IntPtr.Zero) > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // ── Query ────────────────────────────────────────────────────

    [LibraryImport("Wintab32.dll", EntryPoint = "WTInfoA")]
    internal static partial uint WTInfoA(uint wCategory, uint nIndex, IntPtr lpOutput);

    // ── Context lifecycle ────────────────────────────────────────

    [DllImport("Wintab32.dll", CharSet = CharSet.Ansi)]
    internal static extern IntPtr WTOpenA(IntPtr hWnd, ref LogContext logContext, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("Wintab32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTClose(IntPtr hCtx);

    [DllImport("Wintab32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTEnable(IntPtr hCtx, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("Wintab32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTOverlap(IntPtr hCtx, [MarshalAs(UnmanagedType.Bool)] bool toTop);

    // ── Context query ────────────────────────────────────────────

    [DllImport("Wintab32.dll", CharSet = CharSet.Ansi, EntryPoint = "WTGetA")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTGetA(IntPtr hCtx, ref LogContext logContext);

    // ── Packet retrieval ─────────────────────────────────────────

    [DllImport("Wintab32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTPacket(IntPtr hCtx, uint serialNumber, IntPtr pktBuf);
}
