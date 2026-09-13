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
    /// <remarks>
    /// <para><b>This is a range, not a count of distinguishable levels.</b> Normalising by it
    /// is correct and is what it is for. Reading it as "the device resolves this many pressure
    /// values" is not, and the gap can be large: a Wacom DTH246 over Wintab reports 32767 here
    /// and resolves 8192 levels, in steps of 4. Measured 12 Sep 2026 from
    /// <c>testdata/wintab-digitizer-stroke-1.75x.csv</c>, where 99.8% of the gaps between
    /// consecutive distinct pressures are multiples of 4.</para>
    /// <para>Nothing in this library reports granularity, because no driver declares it.
    /// Wintab's <c>AXIS</c> carries <c>axUnits</c> and <c>axResolution</c>, and for
    /// <c>DVC_NPRESSURE</c> this driver returns <c>TU_NONE</c> and 0 — while populating both
    /// meaningfully for the X and Y axes. Granularity can only be observed from a captured
    /// stream, never asked for.</para>
    /// <para>Where the value comes from also varies, and the number alone does not say which.
    /// The Wintab sessions query the device through <c>WTInfoA(WTI_DEVICES, DVC_NPRESSURE)</c>.
    /// The WM_POINTER and framework sessions declare 1024, which is the API's fixed range
    /// rather than anything the device was asked about.</para>
    /// <para>See issue 94.</para>
    /// </remarks>
    int MaxPressure { get; }

    /// <summary>Which input API this session uses.</summary>
    InputApi Api { get; }

    /// <summary>Which pen data features this session supports.</summary>
    PenCapabilities Capabilities { get; }

    /// <summary>
    /// What this session's points mean, for the fields whose meaning depends on the backend.
    /// </summary>
    /// <remarks>
    /// Deliberately without a default implementation: a new backend that does not say what
    /// its raw units, button encoding and cursor numbering are will not compile. The next
    /// divergence should be a build error rather than something a reader finds later.
    /// </remarks>
    PenConventions Conventions { get; }

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

    // ── Focus ───────────────────────────────────────────────────

    /// <summary>
    /// Tell the session its application window has just been activated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only Wintab needs this, and it needs it badly. Wintab contexts sit in an overlap order and
    /// the driver delivers packets to whichever is on top; when another application takes focus,
    /// yours drops down that order and nothing puts it back on its own. The visible symptom is
    /// that the first stroke after returning to the app is silently lost, while every stroke after
    /// it draws normally.
    /// </para>
    /// <para>
    /// The pointer-based sessions need no equivalent — Windows routes their input by window — so
    /// this is a default no-op rather than something every implementation has to answer.
    /// </para>
    /// <para>
    /// Call it from the real window's activation event. A Wintab context is bound to a hidden
    /// message window that never sees <c>WM_ACTIVATE</c> itself, so the notification has to come
    /// from the application. Qt does the same thing: its context is on a dummy window, and its
    /// <c>QtWindows::ActivateWindowEvent</c> handler forwards to the tablet support object.
    /// </para>
    /// </remarks>
    void OnActivated() { }
}
