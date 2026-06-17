namespace WinPenKit.Wintab;

/// <summary>
/// Wintab system context session — screen-pixel output.
/// The simplest Wintab path: pkX/pkY are physical screen pixels.
/// </summary>
internal sealed class WintabSystemSession : WintabSessionBase
{
    public override InputApi Api => InputApi.WintabSystem;

    public override PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt | PenCapabilities.Twist |
        PenCapabilities.ZHeight | PenCapabilities.Buttons | PenCapabilities.Eraser |
        PenCapabilities.GlobalCapture;

    protected override string? OpenContext(IntPtr hwnd)
    {
        Log("=== OpenSystemContext ===");

        if (!GetDefaultSystemContext(out var lc))
        {
            Log("FAIL: WTInfoA(WTI_DEFSYSCTX) returned 0");
            return "Failed to get system context defaults.";
        }

        lc.lcOptions |= (uint)(CXO.SYSTEM | CXO.MESSAGES);
        ConfigurePacketData(ref lc);
        if (lc.lcOutExtY > 0) lc.lcOutExtY = -lc.lcOutExtY;

        LogContext("Before open", lc);

        var hCtx = OpenWintabContext(hwnd, ref lc);
        if (hCtx == IntPtr.Zero)
        {
            Log("System Open FAILED");
            return "Failed to open system context.";
        }

        RefreshContext(hCtx, ref lc);
        LogContext("After open", lc);

        SetContext(hCtx);
        SetDebugInfo($"[System] Out:{lc.lcOutExtX},{lc.lcOutExtY}  " +
                     $"Sys:{lc.lcSysOrgX},{lc.lcSysOrgY}/{lc.lcSysExtX},{lc.lcSysExtY}");
        return null;
    }

    protected override (double desktopX, double desktopY) ConvertCoordinates(int pkX, int pkY)
    {
        // System mode: pkX/pkY are already physical screen pixels.
        return (pkX, pkY);
    }
}
