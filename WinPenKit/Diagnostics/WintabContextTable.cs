using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// What the Wintab driver says about its own supply of contexts.
/// </summary>
/// <param name="Open">Contexts open across every application, as the driver counts them.</param>
/// <param name="Maximum">The driver's reported supported count, not necessarily an enforced limit.</param>
public readonly record struct WintabContextTable(uint Open, uint Maximum)
{
    /// <summary>
    /// True when the driver reports more open than it says it supports.
    /// </summary>
    /// <remarks>
    /// <b>Not a capacity check, and not a reason for anything.</b> Measured on a Wacom driver
    /// that reports a maximum of 32: contexts were opened past that figure without complaint, all
    /// the way to 334, every one of them succeeding. Whatever <c>IFC_NCONTEXTS</c> is on that
    /// driver, it is not a ceiling.
    /// <para>
    /// A high count can include live contexts as well as contexts retained after process exit.
    /// It does not by itself prove leakage or rule out other resource limits. A manager can
    /// reclaim known leaked contexts on the tested Wacom driver; see Docs/WINTAB-CONTEXT-LEAK.md.
    /// </para>
    /// </remarks>
    public bool AboveStatedMaximum => Maximum > 0 && Open > Maximum;

    public override string ToString() => $"{Open} open, of a stated maximum of {Maximum}";
}

/// <summary>
/// Questions that can be put to the Wintab driver without opening anything.
/// </summary>
/// <remarks>
/// <para>
/// Here because of a day lost to the answer. Both applications stopped taking pen input, and what
/// the session could say about it was "Fallback context also failed to open" -- which names no
/// cause, so the drawing code was searched instead. Anything the driver will say about itself is
/// worth more than that.
/// </para>
/// <para>
/// <b>What the counts do and do not mean.</b> On the tested Wacom driver, contexts can remain
/// after a process exits without <c>WTClose</c>. High counts can also represent live contexts,
/// and virtual-device opens may contribute multiple counted contexts. Successful opens above
/// the reported 32 do not exclude other allocation limits. The counter alone cannot diagnose
/// a failed open. See Docs/WINTAB-INVESTIGATION-2026-09.md for independent measurements.
/// </para>
/// <para>
/// <b>This reports and does not advise.</b> What the numbers mean for a particular application,
/// and whether to say anything to anyone, belongs to the application.
/// </para>
/// </remarks>
public static class WintabDiagnostics
{
    /// <summary>
    /// Where this process writes its Wintab log, whether or not it has written anything yet.
    /// </summary>
    /// <remarks>
    /// One file per process, named with the process id. Worth being able to name from an
    /// application: it is the file to attach to a bug report about the pen.
    /// </remarks>
    public static string LogPath => Wintab.WintabSessionBase.LogPath;

    /// <summary>
    /// How many contexts the driver reports open and supported, or null if Wintab is
    /// not installed or will not say.
    /// </summary>
    public static WintabContextTable? ContextTable()
    {
        if (!WintabNative.IsAvailable()) return null;

        uint? maximum = Count(WTI.INTERFACE, IFC.NCONTEXTS);
        uint? open = Count(WTI.STATUS, STA.CONTEXTS);

        return maximum is { } max && open is { } now ? new WintabContextTable(now, max) : null;
    }

    private static uint? Count(uint category, uint index)
    {
        using var buf = UnmanagedBuffer.Create<uint>();
        return WintabNative.WTInfoA(category, index, buf.Ptr) == 0
            ? null
            : buf.MarshalOut<uint>();
    }
}
