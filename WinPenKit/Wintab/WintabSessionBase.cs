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
    private uint _lastButtons;
    private uint _lastCursor;
    private string _debugInfo = "";

    // ── Public API ───────────────────────────────────────────────

    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public abstract InputApi Api { get; }
    public abstract PenCapabilities Capabilities { get; }
    public string DebugInfo => _debugInfo;
    public int MaxPressure { get; private set; }

    public string? Start(IntPtr appWindowHandle = default)
    {
        // Wintab sessions ignore the app window handle — they create their own pump window.
        if (!WintabNative.IsAvailable())
            return "Wintab not found. Is the tablet driver installed?";

        MaxPressure = QueryMaxPressure();

        // Start the message pump first — we need the HWND for WTOpen.
        _pump = new WintabMessagePump(OnWintabMessage);

        var error = OpenContext(_pump.Hwnd);
        if (error != null)
        {
            _pump.Dispose();
            _pump = null;
            return error;
        }

        IsRunning = true;
        return null;
    }

    public void Stop()
    {
        if (_hCtx != IntPtr.Zero)
        {
            WintabNative.WTClose(_hCtx);
            _hCtx = IntPtr.Zero;
        }

        _pump?.Dispose();
        _pump = null;
        IsRunning = false;
    }

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

            var (desktopX, desktopY) = ConvertCoordinates(pkt.pkX, pkt.pkY);

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
                Source: Api));

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
