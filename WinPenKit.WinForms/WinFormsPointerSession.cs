using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinPenKit.WinForms;

/// <summary>
/// WinForms pen input session. Intercepts WM_POINTER messages via
/// <see cref="Application.AddMessageFilter"/> — a global message filter
/// that sees all messages before they reach any window proc.
///
/// <para>This avoids the crash caused by <c>NativeWindow.AssignHandle</c>
/// on a Form's HWND (which conflicts with WinForms' own NativeWindow).</para>
/// </summary>
public sealed class WinFormsPointerSession : IPenSession, IMessageFilter
{
    private readonly Control _control;
    private readonly ConcurrentQueue<PenPoint> _points = new();
    private volatile bool _hasNewData;

    public InputApi Api => InputApi.WinFormsPointer;

    public PenCapabilities Capabilities =>
        PenCapabilities.Pressure | PenCapabilities.Tilt |
        PenCapabilities.Buttons | PenCapabilities.Eraser;

    public int MaxPressure => 1024;
    public bool IsRunning { get; private set; }
    public bool HasNewData => _hasNewData;
    public string DebugInfo => $"[WinForms Pointer] Control={_control.GetType().Name}";
    public IPenCaptureRegion? CaptureRegion { get; set; }

    /// <summary>
    /// Creates a WinForms pointer session.
    /// </summary>
    /// <param name="control">The control used for coordinate reference
    /// (typically the Form). Messages are captured application-wide.</param>
    public WinFormsPointerSession(Control control)
    {
        _control = control;
    }

    public string? Start(IntPtr appWindowHandle = default)
    {
        if (!PointerApi.IsAvailable())
            return "WM_POINTER API not available on this system.";

        Application.AddMessageFilter(this);
        IsRunning = true;
        System.Diagnostics.Debug.WriteLine("[WinFormsPointer] Message filter installed");
        return null;
    }

    public void Stop()
    {
        if (IsRunning)
        {
            Application.RemoveMessageFilter(this);
            System.Diagnostics.Debug.WriteLine("[WinFormsPointer] Message filter removed");
        }
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

    public void RefreshMapping() { }
    public void Dispose() => Stop();

    // ── IMessageFilter ───────────────────────────────────────────

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is not (PointerApi.WM_POINTERUPDATE
                       or PointerApi.WM_POINTERDOWN
                       or PointerApi.WM_POINTERUP))
            return false; // Not a pointer message — let WinForms handle it.

        uint pointerId = (uint)((long)m.WParam & 0xFFFF);

        if (!PointerApi.GetPointerType(pointerId, out uint pointerType) ||
            pointerType != PointerApi.PT_PEN)
            return false; // Not pen input.

        // Use history for coalesced UPDATE events (count > 1 only).
        if (m.Msg == PointerApi.WM_POINTERUPDATE)
        {
            var history = new PointerApi.POINTER_PEN_INFO[64];
            uint count = 64;
            if (PointerApi.GetPointerPenInfoHistory(pointerId, ref count, history) && count > 1)
            {
                for (int i = (int)count - 1; i >= 0; i--)
                    EnqueuePenInfo(history[i]);
                return false; // Let WinForms also process the message.
            }
        }

        // Single point.
        if (PointerApi.GetPointerPenInfo(pointerId, out var penInfo))
            EnqueuePenInfo(penInfo);

        return false; // Always let WinForms process the message too.
    }

    // ── PenPoint creation ────────────────────────────────────────

    private void EnqueuePenInfo(PointerApi.POINTER_PEN_INFO penInfo)
    {
        double desktopX = penInfo.pointerInfo.ptPixelLocationRaw.X;
        double desktopY = penInfo.pointerInfo.ptPixelLocationRaw.Y;

        // Spatial scope: drop points outside an explicit capture region.
        if (CaptureRegion is { } region && !region.Contains(desktopX, desktopY))
            return;

        uint pressure = (penInfo.penMask & PointerApi.PEN_MASK_PRESSURE) != 0
            ? penInfo.pressure : 0;

        double tiltX = (penInfo.penMask & PointerApi.PEN_MASK_TILT_X) != 0
            ? penInfo.tiltX : 0.0;
        double tiltY = (penInfo.penMask & PointerApi.PEN_MASK_TILT_Y) != 0
            ? penInfo.tiltY : 0.0;

        TiltToSpherical(tiltX, tiltY, out double azimuth, out double altitude);

        double twist = (penInfo.penMask & PointerApi.PEN_MASK_ROTATION) != 0
            ? penInfo.rotation : 0.0;

        uint buttons = 0;
        if ((penInfo.penFlags & PointerApi.PEN_FLAG_BARREL) != 0) buttons |= 0x0001;
        if ((penInfo.penFlags & PointerApi.PEN_FLAG_ERASER) != 0) buttons |= 0x0002;

        uint cursor = (penInfo.penFlags & PointerApi.PEN_FLAG_INVERTED) != 0
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
            Z: 0,
            Status: 0,
            Buttons: buttons,
            Cursor: cursor,
            Source: InputApi.WinFormsPointer));

        _hasNewData = true;
    }

    private static void TiltToSpherical(double tiltX, double tiltY,
        out double azimuth, out double altitude)
    {
        double mag = Math.Sqrt(tiltX * tiltX + tiltY * tiltY);

        altitude = Math.Clamp(90.0 - mag, 0.0, 90.0);

        if (mag > 0.5)
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

// ── P/Invoke declarations ────────────────────────────────────────

internal static class PointerApi
{
    public const int WM_POINTERUPDATE = 0x0245;
    public const int WM_POINTERDOWN = 0x0246;
    public const int WM_POINTERUP = 0x0247;

    public const uint PT_PEN = 3;

    public const uint PEN_FLAG_BARREL = 0x00000001;
    public const uint PEN_FLAG_INVERTED = 0x00000002;
    public const uint PEN_FLAG_ERASER = 0x00000004;

    public const uint PEN_MASK_PRESSURE = 0x00000001;
    public const uint PEN_MASK_ROTATION = 0x00000002;
    public const uint PEN_MASK_TILT_X = 0x00000004;
    public const uint PEN_MASK_TILT_Y = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
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
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPointerPenInfoHistory(
        uint pointerId, ref uint entriesCount,
        [In, Out] POINTER_PEN_INFO[]? penInfos);

    public static bool IsAvailable()
    {
        try
        {
            GetPointerType(0, out _);
            return true;
        }
        catch { return false; }
    }
}
