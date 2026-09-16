using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// Shared base for Wintab-backed pen sessions. Owns the message pump,
/// packet queue, logging, and P/Invoke helpers. Subclasses implement
/// context creation and coordinate conversion.
/// </summary>
internal abstract class WintabSessionBase : IPenSession, Diagnostics.IPacketCounts
{
    private WintabMessagePump? _pump;
    private IntPtr _hCtx;

    /// <summary>Paces <see cref="KeepContextAlive"/>, which must not run at the drain rate.</summary>
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private long _nextContextCheckMs;
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

    private long _fromDriver;
    private long _outsideRegion;
    private long _delivered;

    /// <inheritdoc />
    public long PacketsFromDriver => Interlocked.Read(ref _fromDriver);

    /// <inheritdoc />
    public long PacketsOutsideCaptureRegion => Interlocked.Read(ref _outsideRegion);

    /// <inheritdoc />
    public long PointsDelivered => Interlocked.Read(ref _delivered);

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

    /// <summary>
    /// Notice that the driver has taken the context away, and get another one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context does not only end when it is closed. Restarting the tablet service invalidates
    /// every context that is open at the time, and the application holding one is told nothing at
    /// all: measured, a real application kept running with its status line still naming the
    /// driver, wrote nothing to its log, and simply stopped receiving pen input. That is
    /// indistinguishable from a broken renderer, which is the failure this whole area keeps
    /// producing. See <c>Docs/WINTAB-CONTEXT-LEAK.md</c>.
    /// </para>
    /// <para>
    /// <c>WTGetA</c> returns false for a handle the driver no longer knows, so asking is cheap and
    /// unambiguous. Asking from here because this is what a consumer already calls on its frame
    /// timer; asking at that rate would be sixty pointless round trips a second, so it is paced to
    /// one.
    /// </para>
    /// <para>
    /// <b>It reopens rather than only reporting.</b> A new context opens perfectly well once the
    /// service is back -- that was measured too -- so the pen can simply come back, within a
    /// second, with no help from the application. When the reopen fails the interval backs off to
    /// five seconds: a driver that is refusing takes about 90 ms to say so, and doing that once a
    /// second on the thread that draws would be a visible stutter.
    /// </para>
    /// </remarks>
    private void KeepContextAlive()
    {
        if (_pump is null) return;
        if (_sinceStart.ElapsedMilliseconds < _nextContextCheckMs) return;

        _nextContextCheckMs = _sinceStart.ElapsedMilliseconds + 1000;

        if (_hCtx != IntPtr.Zero)
        {
            var probe = default(LogContext);
            if (WintabNative.WTGetA(_hCtx, ref probe)) return;

            Log($"Context 0x{_hCtx.ToInt64():X} is no longer known to the driver -- it was taken " +
                "away rather than closed, which is what restarting the tablet service does.");

            _hCtx = IntPtr.Zero;
            IsRunning = false;
            LogContexts("after losing the context");
        }

        if (OpenContext(_pump.Hwnd) is { } error)
        {
            Log($"Could not reopen the context: {error}");
            _nextContextCheckMs = _sinceStart.ElapsedMilliseconds + 5000;
            return;
        }

        IsRunning = true;
        Log("Context reopened; the pen should work again.");
        LogContexts("after reopening");
    }

    public PenPoint[] DrainPoints()
    {
        KeepContextAlive();

        _hasNewData = false;
        var list = new List<PenPoint>();
        while (_points.TryDequeue(out var pt))
            list.Add(pt);
        return [.. list];
    }

    public int DrainPoints(Span<PenPoint> buffer)
    {
        KeepContextAlive();

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
        FlushLog();
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

            // Counted here, before anything has judged the packet. A count taken further down
            // can only agree with what survived, which is exactly the question it would be
            // asked to answer. Interlocked because this runs on the pump thread and is read
            // from whichever thread asks.
            Interlocked.Increment(ref _fromDriver);

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
            {
                Interlocked.Increment(ref _outsideRegion);

                return;
            }

            // Log button/cursor transitions.
            if (pkt.pkButtons != _lastButtons || pkt.pkCursor != _lastCursor)
            {
                Log($"Button change: 0x{_lastButtons:X8} -> 0x{pkt.pkButtons:X8}  " +
                    $"Cursor: {_lastCursor} -> {pkt.pkCursor}  " +
                    $"Pressure: {pkt.pkNormalPressure}");
                _lastButtons = pkt.pkButtons;
                _lastCursor = pkt.pkCursor;
            }

            Interlocked.Increment(ref _delivered);

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

    /// <summary>Where this process writes its Wintab log.</summary>
    /// <remarks>
    /// <para>
    /// <b>One file per process.</b> It used to be one file for every application using the
    /// library, which failed in two ways at once. A second application could not write to it at
    /// all -- the first holds the file, the second's writer throws, and the exception went to
    /// Debug.WriteLine where nobody saw it -- so running two pen applications, which is exactly
    /// what diagnosing a driver involves, silently logged only one of them. And the file was
    /// named after nothing, so an appending version of it could not have said which application
    /// or which run a line came from.
    /// </para>
    /// <para>
    /// The process id is in the name, so both problems go away together and every line keeps the
    /// shape it had. A log with no "after closing" line is a run that was killed, and the file
    /// name says which process it was.
    /// </para>
    /// </remarks>
    internal static string LogPath { get; } = Path.Combine(
        Path.GetTempPath(), $"WinPenKit.{Environment.ProcessId}.log");

    private static StreamWriter? _logWriter;
    private static bool _logOpened;

    protected static void Log(string message)
    {
        // Opened before the line is stamped, not after. The other way round, the first message
        // carried a time from before the file's own first line and the log was not monotonic --
        // which is a small thing that costs somebody an hour when they notice it in a log they
        // are already suspicious of.
        if (!_logOpened)
        {
            _logOpened = true;
            _logWriter = OpenLog();
        }

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);

        try
        {
            _logWriter?.WriteLine(line);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinPenKit] Log write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Start this process's log, once, and say what and when it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Truncating rather than appending, because a process id is reused: a file left by an
    /// earlier process with the same id is not this process's log and must not be read as one.
    /// Truncated <b>once</b>, though -- the writer is then kept for the life of the process, so
    /// that a session being disposed and another started does not wipe what came before it. That
    /// was the second fault here: closing the writer on Dispose meant every change of pen API
    /// threw away the log of everything that had happened first.
    /// </para>
    /// <para>
    /// The first line carries the date and the application, which the per-line timestamps do not.
    /// It is an ordinary log line and not a banner, so anything reading the file line by line
    /// needs no special case for it.
    /// </para>
    /// </remarks>
    private static StreamWriter? OpenLog()
    {
        try
        {
            PruneOldLogs();

            var writer = new StreamWriter(LogPath, append: false) { AutoFlush = true };

            string who = Process.GetCurrentProcess().ProcessName;
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Log start: {who} " +
                             $"pid {Environment.ProcessId}, {DateTime.Now:yyyy-MM-dd}");

            return writer;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinPenKit] Could not open {LogPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drop logs left by processes that ran more than a week ago.</summary>
    /// <remarks>
    /// A file per process is a file per run, and nothing would ever remove them. A week is long
    /// enough to still have the log of the run that went wrong on Friday and short enough that
    /// the temporary directory does not fill with them. Best effort: a file still held open by a
    /// living process cannot be deleted, and that is the right outcome anyway.
    /// </remarks>
    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);

            // The name this used to write to, before there was one file per process. Nothing
            // writes it any more, so it would sit there forever being read by mistake.
            var legacy = Path.Combine(Path.GetTempPath(), "WinPenKit.log");
            try { File.Delete(legacy); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            foreach (var old in Directory.EnumerateFiles(Path.GetTempPath(), "WinPenKit.*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(old) < cutoff) File.Delete(old);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinPenKit] Could not prune old logs: {ex.Message}");
        }
    }

    protected static void LogContext(string label, in LogContext lc)
    {
        Log($"{label}: Options=0x{lc.lcOptions:X8} Device={lc.lcDevice} PktData=0x{lc.lcPktData:X8}");
        Log($"  InOrg=({lc.lcInOrgX},{lc.lcInOrgY}) InExt=({lc.lcInExtX},{lc.lcInExtY})");
        Log($"  OutOrg=({lc.lcOutOrgX},{lc.lcOutOrgY}) OutExt=({lc.lcOutExtX},{lc.lcOutExtY})");
        Log($"  SysOrg=({lc.lcSysOrgX},{lc.lcSysOrgY}) SysExt=({lc.lcSysExtX},{lc.lcSysExtY})");
    }

    /// <summary>Flush what has been written, without closing the file.</summary>
    /// <remarks>
    /// Closing it here is what made a change of pen API wipe the log: the next write reopened the
    /// file, and reopening truncates. The writer is left open for the life of the process
    /// instead, which also means a process that is killed leaves a complete file behind rather
    /// than a half-written one.
    /// </remarks>
    private static void FlushLog() => _logWriter?.Flush();
}
