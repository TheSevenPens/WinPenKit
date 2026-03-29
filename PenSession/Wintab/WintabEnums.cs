namespace PenSession.Wintab;

// ── Wintab constants and enums ──────────────────────────────────
// Only the values used by the session logic. Self-contained — no
// dependency on WintabDN.

/// <summary>Wintab event messages.</summary>
internal static class WintabMessages
{
    public const uint WT_PACKET     = 0x7FF0;
    public const uint WT_CTXOPEN    = 0x7FF1;
    public const uint WT_CTXCLOSE   = 0x7FF2;
    public const uint WT_PROXIMITY  = 0x7FF5;
    public const uint WT_INFOCHANGE = 0x7FF6;
    public const uint WT_CSRCHANGE  = 0x7FF7;
}

/// <summary>WTInfo category indices.</summary>
internal static class WTI
{
    public const uint DEFSYSCTX = 4;
    public const uint DEVICES   = 100;
}

/// <summary>WTI_DEVICES sub-indices.</summary>
internal static class DVC
{
    public const uint NPRESSURE   = 15;
    public const uint ORIENTATION = 17;
}

/// <summary>Context option flags.</summary>
[Flags]
internal enum CXO : uint
{
    SYSTEM   = 0x0001,
    PEN      = 0x0002,
    MESSAGES = 0x0004,
}

/// <summary>Packet data bits — must match the WintabPacket struct field order.</summary>
[Flags]
internal enum PK : uint
{
    CONTEXT          = 0x0001,
    STATUS           = 0x0002,
    TIME             = 0x0004,
    CHANGED          = 0x0008,
    SERIAL_NUMBER    = 0x0010,
    CURSOR           = 0x0020,
    BUTTONS          = 0x0040,
    X                = 0x0080,
    Y                = 0x0100,
    Z                = 0x0200,
    NORMAL_PRESSURE  = 0x0400,
    TANGENT_PRESSURE = 0x0800,
    ORIENTATION      = 0x1000,
    ALL              = 0x1FFF,
}
