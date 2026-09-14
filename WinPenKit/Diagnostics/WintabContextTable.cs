using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// What the Wintab driver says about its own supply of contexts.
/// </summary>
/// <param name="Open">Contexts open across every application, as the driver counts them.</param>
/// <param name="Maximum">The most the driver says it will have open at once.</param>
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
    /// What this is good for is noticing that the count is implausible, which says contexts have
    /// been leaked -- a process that dies without <c>WTClose</c> never gives its context back.
    /// That is worth knowing and worth reporting. It is not, on the evidence, a cause of anything.
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
/// <b>What the counts do and do not mean.</b> A context is leaked by any process that dies
/// without calling <c>WTClose</c> -- killed, crashed, or stopped from a debugger -- and the
/// driver never takes it back, so a high count is real evidence that this has been happening.
/// It is <b>not</b> evidence of why an open failed: contexts were opened deliberately past the
/// stated maximum of 32, up to 334, and every one of them succeeded. The failure being diagnosed
/// happened at 253 with the counter frozen, which is a driver that has stopped working rather
/// than a driver that has run out.
/// </para>
/// <para>
/// <b>This reports and does not advise.</b> What the numbers mean for a particular application,
/// and whether to say anything to anyone, belongs to the application.
/// </para>
/// </remarks>
public static class WintabDiagnostics
{
    /// <summary>
    /// How many contexts the driver has open and how many it will allow, or null if Wintab is
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
