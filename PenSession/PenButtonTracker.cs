namespace PenSession;

/// <summary>
/// Tracks pen button state across a stream of <see cref="PenPoint"/>s,
/// hiding the encoding difference between Wintab (relative event-based)
/// and Windows Pointer / framework backends (absolute bitmask).
///
/// <para>Usage: create one tracker per session, feed every <see cref="PenPoint"/>
/// to <see cref="Update"/>, then read <see cref="IsTipDown"/>,
/// <see cref="IsBarrelDown"/>, <see cref="IsEraser"/>.</para>
///
/// <para><b>Per-button identity ceiling:</b> Wintab can distinguish individual
/// barrel buttons (Barrel1/2/3). The Windows Pointer family of APIs
/// (<see cref="InputApi.WmPointer"/>, <see cref="InputApi.WinUiPointer"/>,
/// <see cref="InputApi.WpfStylus"/>, <see cref="InputApi.WinFormsPointer"/>,
/// <see cref="InputApi.AvaloniaPointer"/>) only expose a single
/// <c>IsBarrelButtonPressed</c> flag — there is no per-button identity.
/// On those backends, <see cref="IsBarrelDown"/> reports the pressed state on
/// button 1 only; buttons 2 and 3 always read false.</para>
///
/// <para><b>Tip:</b> tip-down is derived from <see cref="PenPoint.Pressure"/>
/// being non-zero, which works uniformly across every backend.</para>
///
/// <para>Not thread-safe — call from a single thread (typically the UI render tick).</para>
/// </summary>
public sealed class PenButtonTracker
{
    // Bitmask used by non-Wintab backends in PenPoint.Buttons.
    private const uint BarrelFlag = 0x0001;
    private const uint EraserFlag = 0x0002;

    private bool _wintabBarrel1, _wintabBarrel2, _wintabBarrel3;
    private bool _wintabTip;
    private bool _pointerBarrel;
    private bool _eraser;
    private bool _tipFromPressure;
    private InputApi? _lastSource;

    /// <summary>The most recent raw <see cref="PenPoint.Buttons"/> value seen,
    /// regardless of which backend produced it. Useful for diagnostic display.</summary>
    public uint LastRawButtons { get; private set; }

    /// <summary>True if the pen tip is currently in contact with the surface.</summary>
    public bool IsTipDown => _tipFromPressure || _wintabTip;

    /// <summary>True if the eraser end of the stylus was active in the most recent point.
    /// Mirrors <see cref="PenPoint.IsEraser"/> from the latest <see cref="Update"/> call.</summary>
    public bool IsEraser => _eraser;

    /// <summary>
    /// True if the specified barrel button (1-based: 1, 2, or 3) is currently held.
    /// On non-Wintab backends, only button 1 can ever be true (see class remarks).
    /// </summary>
    public bool IsBarrelDown(int buttonNumber)
    {
        if (IsWintabSource(_lastSource))
        {
            return buttonNumber switch
            {
                1 => _wintabBarrel1,
                2 => _wintabBarrel2,
                3 => _wintabBarrel3,
                _ => false,
            };
        }
        return buttonNumber == 1 && _pointerBarrel;
    }

    /// <summary>
    /// Updates the tracker with a new pen point. Call this for every point
    /// drained from the session, in arrival order.
    /// </summary>
    public void Update(PenPoint pt)
    {
        // Source can change at runtime if the user switches API. Reset state
        // when the encoding family changes so stale flags don't linger.
        if (_lastSource is { } prev && IsWintabSource(prev) != IsWintabSource(pt.Source))
        {
            ResetState();
        }
        _lastSource = pt.Source;
        LastRawButtons = pt.Buttons;

        // These two are derivable on every backend.
        _tipFromPressure = pt.Pressure > 0;
        _eraser = pt.IsEraser;

        if (IsWintabSource(pt.Source))
        {
            UpdateWintab(pt);
        }
        else
        {
            UpdatePointerBitmask(pt);
        }
    }

    /// <summary>
    /// Resets all tracked state. Call when restarting a session.
    /// </summary>
    public void Reset()
    {
        ResetState();
        _lastSource = null;
        LastRawButtons = 0;
    }

    // ── Internal ─────────────────────────────────────────────────

    private void UpdateWintab(PenPoint pt)
    {
        // Wintab encodes one event per packet: (action << 16) | buttonNumber.
        // Packets with no event have Buttons == 0; state is preserved.
        switch (pt.ButtonAction)
        {
            case PenButtonAction.Pressed:
                SetWintabState(pt.ButtonNumber, true);
                break;
            case PenButtonAction.Released:
                SetWintabState(pt.ButtonNumber, false);
                break;
        }
    }

    private void SetWintabState(int buttonNumber, bool down)
    {
        switch (buttonNumber)
        {
            case PenButtonNumber.Tip: _wintabTip = down; break;
            case PenButtonNumber.Barrel1: _wintabBarrel1 = down; break;
            case PenButtonNumber.Barrel2: _wintabBarrel2 = down; break;
            case PenButtonNumber.Barrel3: _wintabBarrel3 = down; break;
        }
    }

    private void UpdatePointerBitmask(PenPoint pt)
    {
        // WM_POINTER / WinUI / WPF / WinForms / Avalonia all encode buttons as
        // an absolute flag bitmask in PenPoint.Buttons. Each new packet replaces
        // prior state — no diffing needed.
        _pointerBarrel = (pt.Buttons & BarrelFlag) != 0;
    }

    private void ResetState()
    {
        _wintabBarrel1 = _wintabBarrel2 = _wintabBarrel3 = false;
        _wintabTip = false;
        _pointerBarrel = false;
        _eraser = false;
        _tipFromPressure = false;
    }

    private static bool IsWintabSource(InputApi? source) =>
        source is InputApi.WintabSystem or InputApi.WintabDigitizer;
}
