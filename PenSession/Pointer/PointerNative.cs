using System.Runtime.InteropServices;

namespace PenSession.Pointer;

/// <summary>
/// P/Invoke declarations for the WM_POINTER API (Windows 8+).
/// Functions are loaded dynamically to avoid hard failures on older systems.
/// </summary>
internal static class PointerNative
{
    // ── Availability ─────────────────────────────────────────────

    public static bool IsAvailable()
    {
        try
        {
            // GetPointerType exists on Windows 8+.
            GetPointerType(0, out _);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // ── Pointer API ──────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

    // ── Subclass API (comctl32) ──────────────────────────────────

    public delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // ── Constants ────────────────────────────────────────────────

    public const uint WM_POINTERUPDATE = 0x0245;
    public const uint WM_POINTERDOWN   = 0x0246;
    public const uint WM_POINTERUP     = 0x0247;
    public const uint WM_NCDESTROY     = 0x0082;

    public const uint PT_PEN = 3;

    public const uint PEN_FLAG_BARREL   = 0x00000001;
    public const uint PEN_FLAG_INVERTED = 0x00000002;
    public const uint PEN_FLAG_ERASER   = 0x00000004;

    public const uint PEN_MASK_PRESSURE = 0x00000001;
    public const uint PEN_MASK_ROTATION = 0x00000002;
    public const uint PEN_MASK_TILT_X   = 0x00000004;
    public const uint PEN_MASK_TILT_Y   = 0x00000008;

    public static ushort GET_POINTERID_WPARAM(IntPtr wParam) =>
        (ushort)((ulong)wParam & 0xFFFF);
}

// ── Structs ──────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint pointerType;
    public uint pointerId;
    public uint frameId;
    public uint pointerFlags;
    public IntPtr sourceDevice;
    public IntPtr hwndTarget;
    public POINT ptPixelLocation;
    public POINT ptHimetricLocation;
    public POINT ptPixelLocationRaw;
    public POINT ptHimetricLocationRaw;
    public uint dwTime;
    public uint historyCount;
    public int InputData;
    public uint dwKeyStates;
    public ulong PerformanceCount;
    public uint ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public uint penFlags;
    public uint penMask;
    public uint pressure;
    public uint rotation;
    public int tiltX;
    public int tiltY;
}
