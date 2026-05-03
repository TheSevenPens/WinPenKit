using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

// ── Wintab struct definitions ───────────────────────────────────
// Binary-compatible with Wintab32.dll. Layout must match exactly.
// Uses raw primitives (uint, int) instead of wrapper types (HCTX, WTPKT, FIX32).

/// <summary>
/// LOGCONTEXT — configures a Wintab context. 37 fields.
/// Passed to WTOpenA and WTGetA.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct LogContext
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)]
    public string lcName;
    public uint lcOptions;
    public uint lcStatus;
    public uint lcLocks;
    public uint lcMsgBase;
    public uint lcDevice;
    public uint lcPktRate;
    public uint lcPktData;   // WTPKT — packet data bits
    public uint lcPktMode;   // WTPKT — packet mode bits
    public uint lcMoveMask;  // WTPKT — movement mask
    public uint lcBtnDnMask;
    public uint lcBtnUpMask;
    public int lcInOrgX;
    public int lcInOrgY;
    public int lcInOrgZ;
    public int lcInExtX;
    public int lcInExtY;
    public int lcInExtZ;
    public int lcOutOrgX;
    public int lcOutOrgY;
    public int lcOutOrgZ;
    public int lcOutExtX;
    public int lcOutExtY;
    public int lcOutExtZ;
    public uint lcSensX;     // FIX32
    public uint lcSensY;     // FIX32
    public uint lcSensZ;     // FIX32
    public int lcSysMode;    // BOOL (4 bytes)
    public int lcSysOrgX;
    public int lcSysOrgY;
    public int lcSysExtX;
    public int lcSysExtY;
    public uint lcSysSensX;  // FIX32
    public uint lcSysSensY;  // FIX32
}

/// <summary>
/// Pen orientation (azimuth, altitude, twist).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Orientation
{
    public int orAzimuth;
    public int orAltitude;
    public int orTwist;
}

/// <summary>
/// Full packet with all standard data items.
/// Field order must match the PK_* bit order exactly.
///
/// IMPORTANT: pkContext is HCTX (a HANDLE), which is pointer-sized —
/// 8 bytes on x64, 4 bytes on x86. Using uint (always 4 bytes) causes
/// every subsequent field to read from the wrong offset on x64, producing
/// silently shifted data (e.g., Y contains X's value, Pressure contains Z).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Packet
{
    public IntPtr pkContext;         // HCTX — pointer-sized (8 bytes on x64, 4 on x86)
    public uint pkStatus;
    public uint pkTime;
    public uint pkChanged;           // WTPKT
    public uint pkSerialNumber;
    public uint pkCursor;
    public uint pkButtons;
    public int pkX;
    public int pkY;
    public int pkZ;
    public uint pkNormalPressure;
    public uint pkTangentPressure;
    public Orientation pkOrientation;
}

/// <summary>
/// AXIS — describes a data item's range and resolution.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Axis
{
    public int axMin;
    public int axMax;
    public uint axUnits;
    public uint axResolution;        // FIX32
}
