using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// Shared base for Wintab-backed pen sessions. Owns the message pump,
/// packet queue, logging, and P/Invoke helpers. Subclasses implement
/// context creation and coordinate conversion.
/// </summary>
internal abstract class WintabSessionBase : IPenSession
{
    private WintabMessagePump? _pump;
    private IntPtr _hCtx;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;
    /// <summary>
    /// Raw <c>pkTime</c> and the system tick count read at the same instant, one call per
    /// packet, before any conversion. Diagnostics only.
    /// </summary>
    /// <remarks>
    /// The conversion above is the thing under question, so a probe watching its output could
    /// answer nothing about its input. <see cref="Diagnostics.WintabEpochProbe"/> uses this to
    /// establish what origin <c>pkTime</c> is counted from, which decides whether this session
    /// can anchor its clock the way the framework backends do instead of detecting wraps.
    /// </remarks>
    internal Action<uint, long>? RawTimeObserver { get; set; }

    /// <summary>
    /// The pump's hidden window, or zero before <see cref="Start"/>. Diagnostics only.
    /// </summary>
    /// <remarks>
    /// Wintab delivers WT_PACKET to the foreground application, and this window is never shown,
    /// so a host with no visible window of its own receives no packets at all -- which is
    /// indistinguishable from nobody drawing. The samples are unaffected because their own
    /// window holds the foreground. A console host has to borrow this one.
    /// </remarks>
    internal IntPtr PumpWindowHandle => _pump?.Hwnd ?? IntPtr.Zero;

    private uint _lastButtons;
    private uint _lastCursor;
    private string _debugInfo = "";

    // Default capture scope when CaptureRegion is not set: the app window the
    // consumer passed to Start (Unbounded if no handle was supplied).
    private IPenCaptureRegion _defaultRegion = PenCaptureRegion.Unbounded;

    // ── Public API ───────────────────────────────────────────────

    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public abstract InputApi Api { get; }
    public abstract PenCapabilities Capabilities { get; }
    public string DebugInfo => _debugInfo;
    public int MaxPressure { get; private set; }
    public IPenCaptureRegion? CaptureRegion { get; set; }

    /// <summary>The region that points are currently filtered against.</summary>
    private IPenCaptureRegion EffectiveRegion => CaptureRegion ?? _defaultRegion;

    public string? Start(IntPtr appWindowHandle = default)
    {
        // Wintab creates its own hidden pump window for WT_PACKET delivery, but
        // we keep the consumer's app window handle to scope capture to it by
        // default — otherwise Wintab would report points across the whole
        // desktop, unlike the window/control-scoped pointer backends.
        _defaultRegion = PenCaptureRegion.Window(appWindowHandle);

        if (!WintabNative.IsAvailable())
            return "Wintab not found. Is the tablet driver installed?";

        MaxPressure = QueryMaxPressure();

        // Before anything is opened, so the number is the one this process inherited.
        LogContexts("before opening");

        // Start the message pump first — we need the HWND for WTOpen.
        _pump = new WintabMessagePump(OnWintabMessage);

        var error = OpenContext(_pump.Hwnd);
        if (error != null)
        {
            _pump.Dispose();
            _pump = null;
            return error;
        }

        LogContexts("after opening");

        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        if (_hCtx != IntPtr.Zero)
        {
            WintabNative.WTClose(_hCtx);
            _hCtx = IntPtr.Zero;

            // The line whose absence is the interesting one. A session that reaches here has
            // given its context back; a process that is killed never writes this, and the count
            // the next run logs as "before opening" is the one it left behind.
            LogContexts("after closing");
        }

        _pump?.Dispose();
        _pump = null;
        IsRunning = false;
    }

    /// <summary>
    /// Write the driver's context counters to the log, with a note of what was happening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of these bracket a session: before the context is opened, after, and after it is
    /// closed. Together they say what this process cost the driver, and separately they say what
    /// it inherited -- a machine that has been used normally sits in the low single figures, and
    /// the "before opening" line is the whole of the evidence for how many contexts were already
    /// leaked when this run started.
    /// </para>
    /// <para>
    /// A context is leaked by any process that dies without calling <c>WTClose</c>: killed,
    /// crashed, or stopped from a debugger. On the driver this was measured against, the driver
    /// never takes it back. So when a log shows an "after opening" with no "after closing", the
    /// run it came from leaked one, and the next run's first line will be two higher. See
    /// <c>Docs/WINTAB-CONTEXT-LEAK.md</c>.
    /// </para>
    /// <para>
    /// Facts only. Whether a number is alarming, and what anybody should do about it, is not this
    /// library's business.
    /// </para>
    /// </remarks>
    private static void LogContexts(string when)
    {
        if (Diagnostics.WintabDiagnostics.ContextTable() is not { } table) return;

        Log($"Contexts {when}: {table}" +
            (table.AboveStatedMaximum ? "  (above the stated maximum)" : ""));
    }

    /// <summary>
    /// Left abstract so each context states its own. The digitizer's raw units depend on
    /// whether the hi-res context actually opened, which the base class cannot know.
    /// </summary>
    public abstract PenConventions Conventions { get; }

    public PenPoint[] DrainPoints()
    {
        _hasNewData = false;
        var list = new List<PenPoint>();
        while (_points.TryDequeue(out var pt))
            list.Add(pt);
        return [.. list];
    }

    public int DrainPoints(Span<PenPoint> buffer)
    {
        _hasNewData = false;
        int count = 0;
        while (count < buffer.Length && _points.TryDequeue(out var pt))
            buffer[count++] = pt;

        // A buffer smaller than the queue leaves points behind. Clearing the flag and
        // stopping there told a caller that polls HasNewData the queue was empty when it was
        // not, and if the pen had lifted nothing would set it again: those points waited for
        // a drain that happened for some other reason.
        //
        // Only ever set true here, never false. The false above still happens before the
        // drain, so a point enqueued by the pen thread mid-drain sets the flag itself and
        // this cannot overwrite it.
        if (!_points.IsEmpty) _hasNewData = true;

        return count;
    }

    public virtual void RefreshMapping() { }

    public void Dispose()
    {
        Stop();
        CloseLog();
    }

    // ── Abstract: subclass provides context creation + coord conversion ──

    /// <summary>
    /// Opens the Wintab context. Sets <see cref="SetContext"/> with the handle.
    /// Returns null on success, or an error string.
    /// </summary>
    protected abstract string? OpenContext(IntPtr hwnd);

    /// <summary>
    /// Converts raw packet X/Y to desktop coordinates.
    /// </summary>
    protected abstract (double desktopX, double desktopY) ConvertCoordinates(int pkX, int pkY);

    // ── Focus ─────────────────────────────────────────────────────

    /// <summary>
    /// Reclaim the top of the driver's overlap order after the application window is activated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wintab delivers packets to whichever context is on top of the overlap order. Losing focus
    /// to another application drops this context down that order, and nothing restores it
    /// automatically — so the first stroke after coming back is silently swallowed, while the
    /// second and every one after it draw. Reproduced across the Avalonia, WinForms and WPF
    /// samples, which is what pinned it to this shared class rather than to any framework.
    /// </para>
    /// <para>
    /// The pair of calls, and the order, follow Qt's <c>QWindowsTabletSupport::notifyActivate</c>,
    /// which is why Qt applications do not have this bug — Krita is clean on Wintab where Clip
    /// Studio Paint is not. <c>WTEnable</c> makes sure the context is on; <c>WTOverlap</c> puts it
    /// back on top.
    /// </para>
    /// <para>
    /// Best-effort by design. A failure here means the pen keeps behaving as it did before rather
    /// than anything breaking, so it is logged rather than thrown — and logged rather than
    /// swallowed, because a silent no-op is exactly the failure mode being fixed.
    /// </para>
    /// </remarks>
    public void OnActivated()
    {
        if (_hCtx == IntPtr.Zero) return;

        bool enabled = WintabNative.WTEnable(_hCtx, true);
        bool onTop = WintabNative.WTOverlap(_hCtx, true);
        if (!enabled || !onTop)
            Log($"OnActivated: WTEnable={enabled} WTOverlap={onTop} (ctx=0x{_hCtx.ToInt64():X})");
    }

    // ── Context helpers for subclasses ────────────────────────────

    protected void SetContext(IntPtr hCtx) => _hCtx = hCtx;
    protected void SetDebugInfo(string info) => _debugInfo = info;

    protected bool GetDefaultSystemContext(out LogContext lc)
    {
        using var buf = UnmanagedBuffer.Create<LogContext>();
        uint size = WintabNative.WTInfoA(WTI.DEFSYSCTX, 0, buf.Ptr);
        if (size == 0) { lc = default; return false; }
        lc = buf.MarshalOut<LogContext>();
        return true;
    }

    protected IntPtr OpenWintabContext(IntPtr hwnd, ref LogContext lc)
    {
        return WintabNative.WTOpenA(hwnd, ref lc, true);
    }

    protected void RefreshContext(IntPtr hCtx, ref LogContext lc)
    {
        WintabNative.WTGetA(hCtx, ref lc);
    }

    protected void ConfigurePacketData(ref LogContext lc)
    {
        lc.lcPktData = (uint)PK.ALL;
        lc.lcPktMode = (uint)PK.BUTTONS; // relative mode for buttons
        lc.lcMoveMask = (uint)PK.ALL;
        lc.lcBtnDnMask = 0xFFFFFFFF;
        lc.lcBtnUpMask = 0xFFFFFFFF;
    }

    // ── Packet handling (pump thread) ────────────────────────────

    private void OnWintabMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WintabMessages.WT_PACKET) return;
        if (_hCtx == IntPtr.Zero) return;

        try
        {
            uint serialNumber = (uint)wParam.ToInt64();
            using var buf = UnmanagedBuffer.Create<Packet>();

            if (!WintabNative.WTPacket(_hCtx, serialNumber, buf.Ptr))
                return;

            var pkt = buf.MarshalOut<Packet>();
            if (pkt.pkContext == IntPtr.Zero) return;

            // Before the capture region, deliberately. The blind spot this probe exists to
            // investigate is a gap in which the region discarded every packet, so a clock
            // diagnostic that could only see the packets the region kept would be blind to the
            // same thing. The tick is read here because the question is what pkTime reads
            // against the system clock at the moment the packet is in hand.
            RawTimeObserver?.Invoke(pkt.pkTime, Environment.TickCount64);

            var (desktopX, desktopY) = ConvertCoordinates(pkt.pkX, pkt.pkY);

            // Spatial scope: drop points outside the capture region so Wintab
            // matches the window/control-scoped pointer backends.
            if (!EffectiveRegion.Contains(desktopX, desktopY))
                return;

            // Log button/cursor transitions.
            if (pkt.pkButtons != _lastButtons || pkt.pkCursor != _lastCursor)
            {
                Log($"Button change: 0x{_lastButtons:X8} -> 0x{pkt.pkButtons:X8}  " +
                    $"Cursor: {_lastCursor} -> {pkt.pkCursor}  " +
                    $"Pressure: {pkt.pkNormalPressure}");
                _lastButtons = pkt.pkButtons;
                _lastCursor = pkt.pkCursor;
            }

            _points.Enqueue(new PenPoint(
                DesktopX: desktopX,
                DesktopY: desktopY,
                RawX: pkt.pkX,
                RawY: pkt.pkY,
                Pressure: pkt.pkNormalPressure,
                Azimuth: pkt.pkOrientation.orAzimuth / 10.0,
                Altitude: pkt.pkOrientation.orAltitude / 10.0,
                Twist: pkt.pkOrientation.orTwist / 10.0,
                TiltX: SphericalToTiltX(pkt.pkOrientation.orAzimuth, pkt.pkOrientation.orAltitude),
                TiltY: SphericalToTiltY(pkt.pkOrientation.orAzimuth, pkt.pkOrientation.orAltitude),
                Z: pkt.pkZ,
                Status: pkt.pkStatus,
                Buttons: pkt.pkButtons,
                Cursor: pkt.pkCursor,
                // lcPktData asks for PK_PKTBITS_ALL, so pkTime is filled in on every packet.
                // Wintab calls it milliseconds and documents no origin, but one was measured on
                // 13 Sep 2026: it is the GetTickCount64 epoch. Over 6217 packets spanning 41.7s
                // and a deliberate pause, pkTime advanced 41703ms against 41703ms of wall clock,
                // with the offset between the two staying inside a 40ms band.
                //
                // So this anchors rather than detecting a backward jump. Anchoring is stateless:
                // it recovers the wrap from the reading itself, so a wrap that happened while
                // the session was stopped, or across a gap where the capture region discarded
                // every packet, comes back correct. Detection could not see either.
                //
                // See WintabEpochProbe, and testdata/wintab-epoch-probe.csv for the readings.
                Source: Api,
                TimestampMicroseconds: PenTimestamp.FromSystemTicks(
                    pkt.pkTime, Environment.TickCount64)));

            _hasNewData = true;
        }
        catch (Exception ex)
        {
            Log($"Packet error: {ex.Message}");
        }
    }

    // ── ScaleAxis ────────────────────────────────────────────────

    protected static double ScaleAxis(int input, int inOrg, int inExt, int outOrg, int outExt)
    {
        if (inExt == 0) return outOrg;

        double dIn = input;
        double dInOrg = inOrg;
        double dInExt = inExt;
        double dOutOrg = outOrg;
        double dOutExt = outExt;

        if ((dOutExt >= 0) == (dInExt >= 0))
            return ((dIn - dInOrg) * Math.Abs(dOutExt) / Math.Abs(dInExt)) + dOutOrg;
        else
            return ((Math.Abs(dInExt) - (dIn - dInOrg)) * Math.Abs(dOutExt) / Math.Abs(dInExt)) + dOutOrg;
    }

    // ── Tilt conversion ────────────────────────────────────────────

    protected static double SphericalToTiltX(int azimuth, int altitude)
    {
        double tiltMag = 90.0 - altitude / 10.0; // degrees from vertical
        double azRad = azimuth / 10.0 * Math.PI / 180.0;
        return -tiltMag * Math.Sin(azRad);
    }

    protected static double SphericalToTiltY(int azimuth, int altitude)
    {
        double tiltMag = 90.0 - altitude / 10.0;
        double azRad = azimuth / 10.0 * Math.PI / 180.0;
        return tiltMag * Math.Cos(azRad);
    }

    // ── Queries ──────────────────────────────────────────────────

    private static int QueryMaxPressure()
    {
        using var buf = UnmanagedBuffer.Create<Axis>();
        uint size = WintabNative.WTInfoA(WTI.DEVICES, DVC.NPRESSURE, buf.Ptr);
        if (size == 0) return 0;
        return buf.MarshalOut<Axis>().axMax;
    }

    // ── Logging ──────────────────────────────────────────────────

    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "WinPenKit.log");
    private static StreamWriter? _logWriter;

    protected static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try
        {
            _logWriter ??= new StreamWriter(LogPath, append: false) { AutoFlush = true };
            _logWriter.WriteLine(line);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinPenKit] Log write failed: {ex.Message}");
        }
    }

    protected static void LogContext(string label, in LogContext lc)
    {
        Log($"{label}: Options=0x{lc.lcOptions:X8} Device={lc.lcDevice} PktData=0x{lc.lcPktData:X8}");
        Log($"  InOrg=({lc.lcInOrgX},{lc.lcInOrgY}) InExt=({lc.lcInExtX},{lc.lcInExtY})");
        Log($"  OutOrg=({lc.lcOutOrgX},{lc.lcOutOrgY}) OutExt=({lc.lcOutExtX},{lc.lcOutExtY})");
        Log($"  SysOrg=({lc.lcSysOrgX},{lc.lcSysOrgY}) SysExt=({lc.lcSysExtX},{lc.lcSysExtY})");
    }

    private static void CloseLog()
    {
        _logWriter?.Dispose();
        _logWriter = null;
    }
}
