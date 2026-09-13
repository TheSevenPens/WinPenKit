using WinPenKit.Wintab;

namespace WinPenKit.Diagnostics;

/// <summary>
/// Collects raw <c>pkTime</c> readings from a running Wintab session, paired with the system
/// tick count read at the same instant, for <see cref="WintabEpochProbe.Report"/> to analyse.
/// </summary>
/// <remarks>
/// <para>This exists because the probe needs a host with a visible window and
/// <see cref="WintabEpochProbe.Run"/> is not one. Wintab delivers WT_PACKET to the foreground
/// application, so a console host receives nothing: it has no window to hold the foreground,
/// and pen contact hands the foreground to whatever window is under the pen. Attaching to a
/// sample that already owns a window the person is drawing in avoids the problem rather than
/// working around it.</para>
/// <para>Attaching does not disturb the session. The observer runs on the pump thread after the
/// packet has been read and before the capture region is consulted, appends to a list, and
/// returns.</para>
/// </remarks>
public sealed class WintabEpochSampler
{
    private readonly List<(uint Raw, long Tick)> _samples = [];

    private WintabEpochSampler() { }

    /// <summary>
    /// Attaches to <paramref name="session"/> if it is a Wintab session, otherwise returns null.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing: a host that offers several backends will call this
    /// on whichever one the user picked, and "this backend has no pkTime" is an ordinary answer
    /// rather than a fault. The caller reports the skip.
    /// </remarks>
    public static WintabEpochSampler? TryAttach(IPenSession session)
    {
        if (session is not WintabSessionBase wintab) return null;

        var sampler = new WintabEpochSampler();
        wintab.RawTimeObserver = (raw, tick) =>
        {
            lock (sampler._samples) sampler._samples.Add((raw, tick));
        };
        return sampler;
    }

    /// <summary>Everything collected so far, as a snapshot.</summary>
    public IReadOnlyList<(uint Raw, long Tick)> Samples
    {
        get { lock (_samples) return [.. _samples]; }
    }

    /// <summary>Writes the verdict. Zero when pkTime can be anchored, non-zero otherwise.</summary>
    public int Report(TextWriter output) => WintabEpochProbe.Report(output, Samples);

    /// <summary>
    /// Writes every reading as CSV, so the analysis can be redone without drawing again.
    /// </summary>
    /// <remarks>
    /// Worth the file on its own. The first run of this probe reported the wrong verdict through
    /// an arithmetic fault in the analysis, and with only the summary kept, fixing it meant
    /// asking for the whole measurement a second time. A probe that needs a person and a tablet
    /// should hand back its raw readings.
    /// </remarks>
    public void Dump(TextWriter output)
    {
        output.WriteLine("# Raw Wintab pkTime against GetTickCount64, one row per packet, before");
        output.WriteLine("# any conversion. Both columns are milliseconds. pkTime is a uint and");
        output.WriteLine("# wraps every 49.7 days; the tick count is 64-bit and does not.");
        output.WriteLine("pkTime,tickCount64");
        foreach (var (raw, tick) in Samples)
            output.WriteLine($"{raw},{tick}");
    }
}
