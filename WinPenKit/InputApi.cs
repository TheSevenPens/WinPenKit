namespace WinPenKit;

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

/// <summary>
/// The short name an application shows for an <see cref="InputApi"/>, and whether the API
/// depends on the host's UI framework.
/// </summary>
/// <remarks>
/// Six copies of the same switch statement spelled these names before this existed -- four in
/// C#, one in C++, one in Rust -- so "Wintab (high-res)" was six independent spellings of one
/// string. This follows <see cref="PenRawUnitsExtensions.Label"/>, which exists for the same
/// reason.
/// </remarks>
public static class InputApiExtensions
{
    /// <summary>
    /// The name to show in a dropdown. Short enough for a toolbar, and it names the API
    /// rather than the implementation: a person choosing one is choosing an input path.
    /// </summary>
    public static string Label(this InputApi api) => api switch
    {
        InputApi.WintabSystem => "Wintab",
        InputApi.WintabDigitizer => "Wintab (high-res)",
        InputApi.WmPointer => "WM_Pointer",
        InputApi.WinUiPointer => "WinUI Pointer",
        InputApi.WpfStylus => "WPF Stylus",
        InputApi.AvaloniaPointer => "Avalonia Pointer",
        InputApi.WinFormsPointer => "WinForms Pointer",
        _ => api.ToString(),
    };

    /// <summary>
    /// True when the API works in any application whatever its UI framework, so
    /// <see cref="PenSessionFactory"/> can both discover and create it.
    /// </summary>
    /// <remarks>
    /// <see cref="InputApi.WmPointer"/> is agnostic by this definition and still absent from
    /// every framework application's dropdown: it subclasses the window procedure, and WPF,
    /// WinForms, WinUI and Avalonia each consume pointer messages before a subclass sees
    /// them. That exclusion is a fact about the host, not about the API, so it lives in each
    /// framework package's own discovery rather than here.
    /// </remarks>
    public static bool IsFrameworkAgnostic(this InputApi api) => api is
        InputApi.WintabSystem or InputApi.WintabDigitizer or InputApi.WmPointer;
}
