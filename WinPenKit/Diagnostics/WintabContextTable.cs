using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// What the Wintab driver says about its own supply of contexts.
/// </summary>
/// <param name="Open">Contexts open across every application, as the driver counts them.</param>
/// <param name="Maximum">The most the driver says it will have open at once.</param>
public readonly record struct WintabContextTable(uint Open, uint Maximum)
{
    /// <summary>True when the driver has no context left to give anyone.</summary>
    /// <remarks>
    /// Written as "at or over" rather than "equal to", because the count seen in the wild was
    /// 253 against a maximum of 32. A driver that has lost count of its own contexts is still a
    /// driver that will not open another one.
    /// </remarks>
    public bool IsFull => Maximum > 0 && Open >= Maximum;

    public override string ToString() => $"{Open} of a maximum of {Maximum}";
}

/// <summary>
/// Questions that can be put to the Wintab driver without opening anything.
/// </summary>
/// <remarks>
/// <para>
/// Here because of a day lost to the answer. Both applications stopped taking pen input, and what
/// the session could say about it was "Fallback context also failed to open" -- which names no
/// cause, so the drawing code was searched instead. The driver knew: it had 253 contexts open
/// against a stated maximum of 32, and was refusing every kind of context to everyone.
/// </para>
/// <para>
/// A context is leaked by any process that dies without calling <c>WTClose</c> -- killed from a
/// task manager, crashed, or stopped from a debugger -- and the driver does not appear to reclaim
/// them. Over a couple of days of development that is enough to fill the table.
/// </para>
/// <para>
/// <b>This reports and does not advise.</b> What to do about a full table, and whether to say
/// anything to anyone, belongs to the application.
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
