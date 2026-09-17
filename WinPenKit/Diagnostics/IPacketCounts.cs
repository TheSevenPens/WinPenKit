namespace WinPenKit.Diagnostics;

/// <summary>
/// How many packets a session was given, against how many it passed on.
/// </summary>
/// <remarks>
/// <para>
/// A diagnostic and nothing else. It exists because an application cannot otherwise tell the
/// difference between a device that has stopped reporting and a session that is discarding what
/// it reports: both look like silence, and the two have entirely different fixes.
/// </para>
/// <para>
/// The case that prompted it: a tablet stopped reporting a hovering pen for several seconds
/// before every stroke, reliably, and resumed four milliseconds after the pen lifted. The
/// application could prove it had kept every point it was handed, and could not see whether
/// the points had been handed over at all.
/// </para>
/// <para>
/// Implemented only where the counts mean something. Ask for it with a type test rather than
/// assuming a session has it, and treat its absence as "this backend cannot say" rather than
/// as zero.
/// </para>
/// </remarks>
public interface IPacketCounts
{
    /// <summary>
    /// Packets received from the driver, before the session has judged any of them.
    /// </summary>
    /// <remarks>
    /// Counted at the earliest point the packet exists as data, ahead of the capture region
    /// and everything after it, because a count taken later can only ever agree with what
    /// survived.
    /// </remarks>
    long PacketsFromDriver { get; }

    /// <summary>Packets dropped for arriving outside the capture region.</summary>
    long PacketsOutsideCaptureRegion { get; }

    /// <summary>Points handed on to the application.</summary>
    long PointsDelivered { get; }
}
