using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinPenKit.Pointer;

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
    private IPenCaptureRegion _defaultRegion = PenCaptureRegion.Unbounded;

    // Must prevent GC of the delegate while the subclass is active.
    private PointerNative.SubclassProc? _subclassDelegate;
    private const nuint SUBCLASS_ID = 0xAE5E5510;

    // ── IPenSession ──────────────────────────────────────────────

    public InputApi Api => InputApi.WmPointer;

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser |
        (_hiRes ? PenCapabilities.HiRes : PenCapabilities.None);

    // ── Sub-pixel positions ──────────────────────────────────────

    // POINTER_INFO carries the position twice: ptPixelLocationRaw in whole screen pixels, and
    // ptHimetricLocationRaw in 0.01mm units - about 7x finer on a typical display. Both arrive in
    // every message; reading the pixel one throws the precision away on arrival.
    //
    // What that costs is not subtle. Measured on a Wacom over Windows Ink, the median turn between
    // consecutive segments was 11.31 degrees from the pixel field and 2.54 from the himetric one.
    // 11.31 is atan(1/5): at the ~2px steps a tablet reports, an integer grid offers only a handful
    // of directions and the path zigzags between them instead of following the pen.
    //
    // Not a mode, and deliberately not a fourth InputApi. There is nothing to configure and no
    // trade to make - same message, same rate, same struct. A caller that only wants whole pixels
    // writes Math.Round and gets exactly what the pixel field would have said; measured over 372
    // samples that matched 372/372 on both axes.
    private IntPtr _rectsFor = IntPtr.Zero;
    private RECT _deviceRect, _displayRect;
    private bool _hiRes;

    /// <summary>
    /// The pen position in physical screen pixels, sub-pixel where the device allows it.
    /// </summary>
    /// <remarks>
    /// The HIMETRIC value is expressed in the device's own rect, so this is a normalization
    /// between the device rect and the display rect rather than a conversion from 0.01mm to
    /// pixels. Falls back to whole pixels if the rects cannot be had, which is also the only
    /// thing that clears <see cref="PenCapabilities.HiRes"/>.
    /// </remarks>
    private (double X, double Y) ResolvePosition(in POINTER_INFO info)
    {
        if (info.sourceDevice != _rectsFor)
        {
            _rectsFor = info.sourceDevice;
            _hiRes = info.sourceDevice != IntPtr.Zero
                && PointerNative.GetPointerDeviceRects(info.sourceDevice, out _deviceRect, out _displayRect)
                && _deviceRect.Width > 0 && _deviceRect.Height > 0;
        }

        if (!_hiRes)
            return (info.ptPixelLocationRaw.X, info.ptPixelLocationRaw.Y);

        return (
            _displayRect.Left + (double)(info.ptHimetricLocationRaw.X - _deviceRect.Left)
                / _deviceRect.Width * _displayRect.Width,
            _displayRect.Top + (double)(info.ptHimetricLocationRaw.Y - _deviceRect.Top)
                / _deviceRect.Height * _displayRect.Height);
    }

    public int MaxPressure => 1024; // WM_POINTER fixed range
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => _debugInfo;
    public IPenCaptureRegion? CaptureRegion { get; set; }

    public string? Start(IntPtr appWindowHandle = default)
    {
        if (!PointerNative.IsAvailable())
            return "WM_POINTER API not available on this system.";

        if (appWindowHandle == IntPtr.Zero)
            return "WM_POINTER requires an application window handle.";

        _appHwnd = appWindowHandle;
        _defaultRegion = PenCaptureRegion.Window(appWindowHandle);

        // Pin the delegate to prevent GC while the subclass is active.
        _subclassDelegate = SubclassProc;

        if (!PointerNative.SetWindowSubclass(_appHwnd, _subclassDelegate, SUBCLASS_ID, 0))
            return "Failed to subclass application window.";

        IsRunning = true;
        _debugInfo = $"[WM_POINTER] Subclassed hwnd=0x{_appHwnd:X}";
        Debug.WriteLine($"[WinPenKit] WM_POINTER started, hwnd=0x{_appHwnd:X}");
        return null;
    }

    public void Stop()
    {
        if (_appHwnd != IntPtr.Zero && IsRunning && _subclassDelegate != null)
        {
            PointerNative.RemoveWindowSubclass(_appHwnd, _subclassDelegate, SUBCLASS_ID);
            Debug.WriteLine("[WinPenKit] WM_POINTER stopped");
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
                OnPointerMessage(uMsg, wParam);
        }

        if (uMsg == PointerNative.WM_NCDESTROY && _subclassDelegate != null)
            PointerNative.RemoveWindowSubclass(hWnd, _subclassDelegate, SUBCLASS_ID);

        return PointerNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Message handler ──────────────────────────────────────────

    private void OnPointerMessage(uint uMsg, IntPtr wParam)
    {
        uint pointerId = PointerNative.GET_POINTERID_WPARAM(wParam);

        // Only handle pen input.
        if (!PointerNative.GetPointerType(pointerId, out uint pointerType)) return;
        if (pointerType != PointerNative.PT_PEN) return;

        // Windows coalesces updates that arrive faster than the message loop drains them.
        // Taking only the newest sampled a fast stroke at the message rate rather than the
        // device rate, and dropped the rest -- while WinFormsPointerSession and the native
        // WM_POINTER session, reading the same API, kept them. Same message, same device,
        // different stroke.
        //
        // History is newest first, so it is replayed in reverse. Only for UPDATE: down and up
        // are single events and asking for their history returns the one point again.
        if (uMsg == PointerNative.WM_POINTERUPDATE)
        {
            var history = new POINTER_PEN_INFO[64];
            uint count = 64;
            if (PointerNative.GetPointerPenInfoHistory(pointerId, ref count, history) && count > 1)
            {
                for (int i = (int)count - 1; i >= 0; i--)
                    EnqueuePenInfo(history[i]);
                return;
            }
        }

        if (PointerNative.GetPointerPenInfo(pointerId, out var penInfo))
            EnqueuePenInfo(penInfo);
    }

    private void EnqueuePenInfo(POINTER_PEN_INFO penInfo)
    {
        var (desktopX, desktopY) = ResolvePosition(penInfo.pointerInfo);

        // Spatial scope: drop points outside the capture region.
        if (!(CaptureRegion ?? _defaultRegion).Contains(desktopX, desktopY))
            return;

        // Pressure (0-1024).
        uint pressure = (penInfo.penMask & PointerNative.PEN_MASK_PRESSURE) != 0
            ? penInfo.pressure : 0;

        // Native TiltX/TiltY in degrees from driver.
        double tiltX = (penInfo.penMask & PointerNative.PEN_MASK_TILT_X) != 0
            ? penInfo.tiltX : 0.0;
        double tiltY = (penInfo.penMask & PointerNative.PEN_MASK_TILT_Y) != 0
            ? penInfo.tiltY : 0.0;

        // Convert TiltX/TiltY → Azimuth/Altitude (degrees).
        TiltToSpherical(tiltX, tiltY, out double azimuth, out double altitude);

        // Twist in degrees.
        double twist = (penInfo.penMask & PointerNative.PEN_MASK_ROTATION) != 0
            ? penInfo.rotation : 0.0;

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
            // ptHimetricLocationRaw, not ptPixelLocationRaw. Both are unfiltered, and only this
            // one is device-native: hundredths of a millimetre straight off the digitizer.
            // The pixel field has already been mapped to the screen grid, so reporting it as
            // "raw" describes the same space DesktopX is in, at lower resolution.
            RawX: penInfo.pointerInfo.ptHimetricLocationRaw.X,
            RawY: penInfo.pointerInfo.ptHimetricLocationRaw.Y,
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
    // Input: TiltX/TiltY in degrees (-90 to +90).
    // Output: Azimuth (0-360), Altitude (0-90), degrees.

    private static void TiltToSpherical(double tiltX, double tiltY,
        out double azimuth, out double altitude)
    {
        double mag = Math.Sqrt(tiltX * tiltX + tiltY * tiltY);

        altitude = Math.Clamp(90.0 - mag, 0.0, 90.0);

        if (mag > 0.5) // degrees threshold
        {
            double rad = Math.Atan2(-tiltX, tiltY);
            double deg = rad * 180.0 / Math.PI;
            azimuth = ((deg % 360.0) + 360.0) % 360.0;
        }
        else
        {
            azimuth = 0.0;
        }
    }
}
