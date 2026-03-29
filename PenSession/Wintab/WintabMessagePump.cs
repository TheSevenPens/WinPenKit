using System.Runtime.InteropServices;

namespace PenSession.Wintab;

/// <summary>
/// Instance-based hidden Win32 window + message loop for receiving
/// Wintab WT_PACKET messages on a background thread.
///
/// <para>Unlike the WintabDN static MessageEvents, this is per-session:
/// each session owns its own pump with clean lifecycle (create → run → dispose).</para>
///
/// <para>The pump creates a hidden top-level window (not HWND_MESSAGE —
/// the Wacom driver doesn't deliver WT_PACKET to message-only windows).</para>
/// </summary>
internal sealed class WintabMessagePump : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Action<uint, IntPtr, IntPtr> _onMessage;
    private IntPtr _hwnd;
    private volatile bool _disposed;

    // Must prevent GC of the delegate while the window exists.
    private readonly WndProcDelegate _wndProcDelegate;
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>The HWND of the hidden window. Valid after construction.</summary>
    public IntPtr Hwnd
    {
        get
        {
            _ready.Wait();
            return _hwnd;
        }
    }

    /// <summary>
    /// Creates and starts the message pump on a background thread.
    /// <paramref name="onMessage"/> is called on the pump thread for every
    /// message in the WT_* range (0x7FF0–0x7FFF).
    /// Parameters: (uint msg, IntPtr wParam, IntPtr lParam).
    /// </summary>
    public WintabMessagePump(Action<uint, IntPtr, IntPtr> onMessage)
    {
        _onMessage = onMessage;
        _wndProcDelegate = WndProc;

        _thread = new Thread(PumpThreadFunc)
        {
            Name = "PenSession.WintabMessagePump",
            IsBackground = true
        };
        _thread.Start();

        // Wait for the window to be created before returning.
        _ready.Wait();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (_thread.IsAlive)
            _thread.Join(timeout: TimeSpan.FromSeconds(2));

        _ready.Dispose();
    }

    // ── Pump thread ──────────────────────────────────────────────

    private void PumpThreadFunc()
    {
        var hInstance = GetModuleHandleW(null);
        var className = "PenSession_Pump_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className
        };

        RegisterClassExW(ref wc);

        // Hidden top-level window — NOT HWND_MESSAGE.
        // The Wacom driver doesn't deliver WT_PACKET to message-only windows.
        _hwnd = CreateWindowExW(0, className, "", WS_OVERLAPPED,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _ready.Set();

        if (_hwnd == IntPtr.Zero) return;

        // Standard message loop. Exits when WM_QUIT is posted.
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WT_* messages are in the range 0x7FF0–0x7FFF.
        if (msg >= 0x7FF0 && msg <= 0x7FFF)
        {
            _onMessage(msg, wParam, lParam);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Win32 interop ────────────────────────────────────────────

    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
