namespace WinPenKit;

/// <summary>
/// Unified pen input session interface. Each implementation wraps a specific
/// input API (Wintab, WM_POINTER, etc.) and produces <see cref="PenPoint"/>
/// records in desktop coordinates.
///
/// <para><b>Lifecycle:</b> Create via <see cref="PenSessionFactory"/>, call
/// <see cref="Start"/>, poll with <see cref="DrainPoints()"/> on a render
/// timer, call <see cref="Stop"/> or <see cref="IDisposable.Dispose"/> when
/// done.</para>
///
/// <para><b>Threading:</b> Implementations may use background threads for
/// packet capture. <see cref="DrainPoints()"/> is always thread-safe.
/// All other members should be called from the thread that created the
/// session.</para>
/// </summary>
public interface IPenSession : IDisposable
{
    // ── Lifecycle ────────────────────────────────────────────────

    /// <summary>
    /// Opens the input context and begins producing <see cref="PenPoint"/>
    /// records. Returns null on success, or an error string on failure.
    /// </summary>
    /// <param name="appWindowHandle">The application window handle. Required
    /// for WM_POINTER sessions (the session subclasses this window to intercept
    /// pointer messages). Pass <see cref="IntPtr.Zero"/> for Wintab sessions
    /// (they create their own hidden pump window).</param>
    string? Start(IntPtr appWindowHandle = default);

    /// <summary>Closes the input context and stops producing points.</summary>
    void Stop();

    /// <summary>Whether the session is actively producing points.</summary>
    bool IsRunning { get; }

    // ── Output (polled by consumer) ─────────────────────────────

    /// <summary>Whether new data is available since the last drain.</summary>
    bool HasNewData { get; }

    /// <summary>
    /// Drains all accumulated <see cref="PenPoint"/> records.
    /// Returns an empty array if nothing is queued. Thread-safe.
    /// </summary>
    PenPoint[] DrainPoints();

    /// <summary>
    /// Drains up to <paramref name="buffer"/>.Length points into the buffer.
    /// Returns the number of points written. Thread-safe, zero-allocation.
    /// </summary>
    int DrainPoints(Span<PenPoint> buffer);

    // ── Properties ──────────────────────────────────────────────

    /// <summary>
    /// Maximum raw pressure value the input device can report.
    /// Normalize with: <c>(float)point.Pressure / session.MaxPressure</c>.
    /// </summary>
    int MaxPressure { get; }

    /// <summary>Which input API this session uses.</summary>
    InputApi Api { get; }

    /// <summary>Which pen data features this session supports.</summary>
    PenCapabilities Capabilities { get; }

    /// <summary>Diagnostic info about the session configuration.</summary>
    string DebugInfo { get; }

    // ── Capture region ──────────────────────────────────────────

    /// <summary>
    /// Constrains which pen points are reported by their desktop (physical
    /// screen-pixel) position. Points outside the region are dropped before
    /// they are queued.
    ///
    /// <para><c>null</c> (the default) means <b>window-scoped</b>: the session
    /// reports points only within the application window passed to
    /// <see cref="Start"/>. (Framework pointer sessions are already scoped to
    /// their control, so <c>null</c> leaves that natural scope unchanged.)</para>
    ///
    /// <para>Set <see cref="PenCaptureRegion.Unbounded"/> for desktop-wide
    /// capture — honored only by backends advertising
    /// <see cref="PenCapabilities.GlobalCapture"/>. Set a custom region (e.g.
    /// a control's live bounds) to scope capture to part of the window, so that
    /// every backend behaves identically.</para>
    ///
    /// <para>May be set before or after <see cref="Start"/>; it takes effect on
    /// the next point.</para>
    /// </summary>
    IPenCaptureRegion? CaptureRegion { get; set; }

    // ── Mapping ─────────────────────────────────────────────────

    /// <summary>
    /// Re-reads coordinate mapping from the driver. Call on display
    /// configuration changes (monitor hot-plug, DPI change, tablet remap).
    /// </summary>
    void RefreshMapping();
}
