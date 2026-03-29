using System.Collections.Concurrent;
using System.Diagnostics;

namespace PenSession.Pointer;

/// <summary>
/// WM_POINTER pen input session. Subclasses the app's window to intercept
/// WM_POINTERUPDATE/DOWN/UP messages and produces <see cref="PenPoint"/>
/// records in desktop coordinates.
///
/// <para>Unlike Wintab sessions which create their own hidden pump window,
/// this session requires the consumer's window handle — WM_POINTER messages
/// are delivered to the window under the pointer.</para>
/// </summary>
internal sealed class WmPointerSession : IPenSession
{
    private IntPtr _appHwnd;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;
    private string _debugInfo = "";

    // Must prevent GC of the delegate while the subclass is active.
    private PointerNative.SubclassProc? _subclassDelegate;
    private const nuint SUBCLASS_ID = 0xAE5E5510;

    // ── IPenSession ──────────────────────────────────────────────

    public InputApi Api => InputApi.WmPointer;

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024; // WM_POINTER fixed range
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => _debugInfo;

    public string? Start(IntPtr appWindowHandle = default)
    {
        if (!PointerNative.IsAvailable())
            return "WM_POINTER API not available on this system.";

        if (appWindowHandle == IntPtr.Zero)
            return "WM_POINTER requires an application window handle.";

        _appHwnd = appWindowHandle;

        // Pin the delegate to prevent GC while the subclass is active.
        _subclassDelegate = SubclassProc;

        if (!PointerNative.SetWindowSubclass(_appHwnd, _subclassDelegate, SUBCLASS_ID, 0))
            return "Failed to subclass application window.";

        IsRunning = true;
        _debugInfo = $"[WM_POINTER] Subclassed hwnd=0x{_appHwnd:X}";
        Debug.WriteLine($"[PenSession] WM_POINTER started, hwnd=0x{_appHwnd:X}");
        return null;
    }

    public void Stop()
    {
        if (_appHwnd != IntPtr.Zero && IsRunning && _subclassDelegate != null)
        {
            PointerNative.RemoveWindowSubclass(_appHwnd, _subclassDelegate, SUBCLASS_ID);
            Debug.WriteLine("[PenSession] WM_POINTER stopped");
        }
        IsRunning = false;
        _appHwnd = IntPtr.Zero;
        _subclassDelegate = null;
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

    public void RefreshMapping() { } // no mapping needed
    public void Dispose() => Stop();

    // ── Subclass proc ────────────────────────────────────────────

    private IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg is PointerNative.WM_POINTERUPDATE
                  or PointerNative.WM_POINTERDOWN
                  or PointerNative.WM_POINTERUP)
        {
            if (IsRunning)
                OnPointerMessage(wParam);
        }

        if (uMsg == PointerNative.WM_NCDESTROY && _subclassDelegate != null)
            PointerNative.RemoveWindowSubclass(hWnd, _subclassDelegate, SUBCLASS_ID);

        return PointerNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Message handler ──────────────────────────────────────────

    private void OnPointerMessage(IntPtr wParam)
    {
        uint pointerId = PointerNative.GET_POINTERID_WPARAM(wParam);

        // Only handle pen input.
        if (!PointerNative.GetPointerType(pointerId, out uint pointerType)) return;
        if (pointerType != PointerNative.PT_PEN) return;

        if (!PointerNative.GetPointerPenInfo(pointerId, out var penInfo)) return;

        double desktopX = penInfo.pointerInfo.ptPixelLocationRaw.X;
        double desktopY = penInfo.pointerInfo.ptPixelLocationRaw.Y;

        // Pressure (0-1024).
        uint pressure = (penInfo.penMask & PointerNative.PEN_MASK_PRESSURE) != 0
            ? penInfo.pressure : 0;

        // Native TiltX/TiltY (degrees from driver → tenths of degree).
        int tiltX = (penInfo.penMask & PointerNative.PEN_MASK_TILT_X) != 0
            ? penInfo.tiltX * 10 : 0;
        int tiltY = (penInfo.penMask & PointerNative.PEN_MASK_TILT_Y) != 0
            ? penInfo.tiltY * 10 : 0;

        // Convert TiltX/TiltY → Azimuth/Altitude (tenths of degree).
        TiltToSpherical(tiltX, tiltY, out int azimuth, out int altitude);

        // Twist (degrees → tenths of degree).
        int twist = (penInfo.penMask & PointerNative.PEN_MASK_ROTATION) != 0
            ? (int)penInfo.rotation * 10 : 0;

        // Buttons.
        uint buttons = 0;
        if ((penInfo.penFlags & PointerNative.PEN_FLAG_BARREL) != 0) buttons |= 0x0001;
        if ((penInfo.penFlags & PointerNative.PEN_FLAG_ERASER) != 0) buttons |= 0x0002;

        // Cursor: eraser via inverted flag.
        uint cursor = (penInfo.penFlags & PointerNative.PEN_FLAG_INVERTED) != 0
            ? PenCursorType.Eraser : PenCursorType.PenTip;

        _points.Enqueue(new PenPoint(
            DesktopX: desktopX,
            DesktopY: desktopY,
            RawX: penInfo.pointerInfo.ptPixelLocationRaw.X,
            RawY: penInfo.pointerInfo.ptPixelLocationRaw.Y,
            Pressure: pressure,
            Azimuth: azimuth,
            Altitude: altitude,
            Twist: twist,
            TiltX: tiltX,
            TiltY: tiltY,
            Z: 0, // WM_POINTER does not report Z height
            Status: 0,
            Buttons: buttons,
            Cursor: cursor,
            Source: InputApi.WmPointer));

        _hasNewData = true;
    }

    // ── Tilt conversion ──────────────────────────────────────────
    //
    // Input: TiltX/TiltY in tenths of degree (-900 to +900).
    // Output: Azimuth (0-3600), Altitude (0-900), tenths of degree.

    private static void TiltToSpherical(int tiltX, int tiltY,
        out int azimuth, out int altitude)
    {
        double tx = tiltX;
        double ty = tiltY;
        double mag = Math.Sqrt(tx * tx + ty * ty);

        altitude = Math.Clamp((int)(900.0 - mag), 0, 900);

        if (mag > 5.0) // ~0.5 degrees threshold
        {
            double rad = Math.Atan2(-tx, ty);
            int tenths = (int)(rad * 1800.0 / Math.PI);
            azimuth = ((tenths % 3600) + 3600) % 3600;
        }
        else
        {
            azimuth = 0;
        }
    }
}
