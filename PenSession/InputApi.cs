namespace PenSession;

/// <summary>
/// Identifies which input API a session uses and which produced a <see cref="PenPoint"/>.
/// Each value maps to a concrete <see cref="IPenSession"/> implementation.
/// </summary>
public enum InputApi
{
    /// <summary>Wintab system context — screen-pixel output, pen drives cursor.</summary>
    WintabSystem,

    /// <summary>Wintab digitizer context — tablet-native output with ScaleAxis
    /// conversion to desktop coordinates, preserving sub-pixel precision.</summary>
    WintabDigitizer,

    /// <summary>Windows Pointer (WM_POINTER) — modern Windows pen input path.
    /// Works in raw Win32 apps via window subclassing.</summary>
    WmPointer,

    /// <summary>WinUI 3 PointerPoint — XAML pointer events.
    /// Works in WinUI 3 apps only. Uses the framework's native input path.</summary>
    WinUiPointer,

    /// <summary>WPF Stylus — WPF's native stylus/pen events.
    /// Works in WPF apps only. Uses the framework's StylusMove/StylusDown path.</summary>
    WpfStylus,

    /// <summary>Avalonia Pointer — Avalonia's native pointer events.
    /// Works in Avalonia apps only.</summary>
    AvaloniaPointer,

    /// <summary>WinForms Pointer — WM_POINTER via NativeWindow WndProc override.
    /// Works in WinForms apps only.</summary>
    WinFormsPointer,
}
