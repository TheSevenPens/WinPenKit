using WinPenKit.Diagnostics;

namespace WinPenKit.Wintab;

/// <summary>
/// Wintab digitizer hi-res session — tablet-native output with ScaleAxis
/// conversion to desktop coordinates, preserving sub-pixel precision.
/// Falls back to system-mode output if the hi-res context fails to open.
/// </summary>
internal sealed class WintabDigitizerSession : WintabSessionBase
{
    // Cached system mapping for ScaleAxis conversion.
    private int _mapInOrgX, _mapInOrgY;
    private int _mapInExtX, _mapInExtY;
    private int _mapSysOrgX, _mapSysOrgY;
    private int _mapSysExtX, _mapSysExtY;
    private bool _useScaleAxis = true;

    public override InputApi Api => InputApi.WintabDigitizer;

    /// <summary>
    /// Tablet-native while the hi-res context is open, screen pixels once it has fallen back
    /// -- the same condition PenCapabilities.HiRes reports, read from the same field.
    /// </summary>
    public override PenConventions Conventions => new(
        _useScaleAxis ? PenRawUnits.TabletNative : PenRawUnits.ScreenPixels,
        PenButtonEncoding.WintabEvent,
        PenCursorNumbering.DeviceAssigned,
        PenTimestampSource.DeviceTicks);

    public override PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt | PenCapabilities.Twist |
        PenCapabilities.ZHeight | PenCapabilities.Buttons | PenCapabilities.Eraser |
        PenCapabilities.GlobalCapture | PenCapabilities.Proximity |
        (_useScaleAxis ? PenCapabilities.HiRes : PenCapabilities.None);

    protected override string? OpenContext(IntPtr hwnd)
    {
        Log("=== OpenDigitizerHiRes ===");

        // Read system defaults for the mapping.
        if (!GetDefaultSystemContext(out var sysDefaults))
        {
            Log("FAIL: WTInfoA(WTI_DEFSYSCTX) returned 0");
            return "Failed to get system context defaults.";
        }

        LogContext("SysDefaults", sysDefaults);
        CacheSystemMapping(sysDefaults);

        // Open with tablet-native output range.
        if (!GetDefaultSystemContext(out var lc))
            return "Failed to get context for digitizer.";

        lc.lcOptions |= (uint)(CXO.SYSTEM | CXO.MESSAGES);
        ConfigurePacketData(ref lc);

        // Override output to tablet-native range.
        lc.lcOutOrgX = sysDefaults.lcInOrgX;
        lc.lcOutOrgY = sysDefaults.lcInOrgY;
        lc.lcOutExtX = sysDefaults.lcInExtX;
        lc.lcOutExtY = sysDefaults.lcInExtY;

        LogContext("BeforeOpen (HiRes)", lc);

        var hCtx = OpenWintabContext(hwnd, ref lc);
        if (hCtx == IntPtr.Zero)
        {
            Log("HiRes Open FAILED — falling back to screen pixels");
            return OpenFallback(hwnd);
        }

        RefreshContext(hCtx, ref lc);
        LogContext("AfterOpen (HiRes)", lc);

        SetContext(hCtx);
        _useScaleAxis = true;
        SetDebugInfo($"[DigitizerHiRes] Mapping In:{_mapInExtX},{_mapInExtY} -> " +
                     $"Sys:{_mapSysOrgX},{_mapSysOrgY}/{_mapSysExtX},{_mapSysExtY}  " +
                     $"Out:{lc.lcOutExtX},{lc.lcOutExtY}");
        return null;
    }

    /// <summary>
    /// Add whatever the driver will say about why, to a refusal that says nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Fallback context also failed to open" names no cause, and a pen that has stopped working
    /// looks exactly like a drawing bug, so the cause is what a caller needs. The driver knows one
    /// of them: whether it has any context left to give. Seen in the wild at 253 open against a
    /// stated maximum of 32, refusing every kind of context to every application, after a run of
    /// development in which processes had been killed rather than closed -- a context is leaked by
    /// any process that dies without calling WTClose.
    /// </para>
    /// <para>
    /// <b>Reported, not advised.</b> What to do about it, and whether to say anything to anyone,
    /// is the application's business. This is a library.
    /// </para>
    /// </remarks>
    private static string WhyNot(string refusal)
    {
        var table = WintabDiagnostics.ContextTable();
        if (table is not { } counts) return refusal;

        Log($"Context table: {counts}");

        return counts.IsFull
            ? $"{refusal} The driver has {counts} contexts open, so it has none left to give."
            : refusal;
    }

    private string? OpenFallback(IntPtr hwnd)
    {
        if (!GetDefaultSystemContext(out var lc))
            return "Failed to get fallback context.";

        lc.lcOptions |= (uint)(CXO.SYSTEM | CXO.MESSAGES);
        ConfigurePacketData(ref lc);
        if (lc.lcOutExtY > 0) lc.lcOutExtY = -lc.lcOutExtY;

        LogContext("Fallback BeforeOpen", lc);

        var hCtx = OpenWintabContext(hwnd, ref lc);
        if (hCtx == IntPtr.Zero)
        {
            Log("Fallback Open also FAILED");
            return WhyNot("Fallback context also failed to open.");
        }

        RefreshContext(hCtx, ref lc);
        LogContext("Fallback AfterOpen", lc);

        SetContext(hCtx);
        _useScaleAxis = false;
        SetDebugInfo("HiRes FAILED — screen-pixel fallback.");
        return null;
    }

    protected override (double desktopX, double desktopY) ConvertCoordinates(int pkX, int pkY)
    {
        if (!_useScaleAxis)
            return (pkX, pkY);

        double desktopX = ScaleAxis(pkX, _mapInOrgX, _mapInExtX, _mapSysOrgX, _mapSysExtX);
        double desktopY = ScaleAxis(pkY, _mapInOrgY, _mapInExtY, _mapSysOrgY, _mapSysExtY);
        return (desktopX, desktopY);
    }

    public override void RefreshMapping()
    {
        if (GetDefaultSystemContext(out var lc))
            CacheSystemMapping(lc);
    }

    private void CacheSystemMapping(in LogContext lc)
    {
        _mapInOrgX = lc.lcInOrgX;
        _mapInOrgY = lc.lcInOrgY;
        _mapInExtX = lc.lcInExtX;
        _mapInExtY = lc.lcInExtY;
        _mapSysOrgX = lc.lcSysOrgX;
        _mapSysOrgY = lc.lcSysOrgY;
        _mapSysExtX = lc.lcSysExtX;

        // Negate SysExtY: tablet origin is bottom-left (Y up),
        // screen origin is top-left (Y down).
        _mapSysExtY = -Math.Abs(lc.lcSysExtY);
    }
}
