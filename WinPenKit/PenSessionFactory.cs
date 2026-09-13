namespace WinPenKit;

/// <summary>
/// Factory for creating <see cref="IPenSession"/> instances.
/// Discovers available input APIs and creates sessions for specific APIs.
///
/// <para><b>Framework-agnostic only.</b> This factory creates sessions that
/// work in any app type (Wintab, WM_POINTER). Framework-specific sessions
/// (WinUI Pointer, WPF Stylus, Avalonia Pointer, WinForms Pointer) require
/// UI elements in their constructors and must be created directly by the
/// app:</para>
///
/// <list type="bullet">
///   <item><c>new WinUiPointerSession(element, hwnd)</c> — from WinPenKit.WinUI</item>
///   <item><c>new WpfStylusSession(element)</c> — from WinPenKit.Wpf</item>
///   <item><c>new AvaloniaPointerSession(control)</c> — from WinPenKit.Avalonia</item>
///   <item><c>new WinFormsPointerSession(form)</c> — from WinPenKit.WinForms</item>
/// </list>
///
/// <para>All sessions implement <see cref="IPenSession"/> and can be used
/// interchangeably once created.</para>
/// </summary>
public static class PenSessionFactory
{
    /// <summary>
    /// Probes the system and returns which framework-agnostic input APIs are available.
    /// Calls into each driver rather than reading an OS version: Wintab through
    /// <c>WTInfoA</c>, the pointer API through <c>GetPointerType</c>.
    /// </summary>
    /// <remarks>
    /// <para>Returns only the APIs that work in any application. It cannot answer for a
    /// particular one, because a UI framework decides both whether its own API can be offered
    /// and whether <see cref="InputApi.WmPointer"/> can reach the application at all.</para>
    /// <para>An application built on a framework should call that framework package's
    /// discovery instead -- <c>WpfPenApis.GetAvailable</c>,
    /// <c>WinFormsPenApis.GetAvailable</c>, <c>AvaloniaPenApis.GetAvailable</c> or
    /// <c>WinUiPenApis.GetAvailable</c> -- each of which answers the whole question for that
    /// framework.</para>
    /// </remarks>
    public static IReadOnlyList<InputApi> GetAvailableApis()
    {
        var apis = new List<InputApi>();

        if (Wintab.WintabNative.IsAvailable())
        {
            apis.Add(InputApi.WintabSystem);
            apis.Add(InputApi.WintabDigitizer);
        }

        if (Pointer.PointerNative.IsAvailable())
        {
            apis.Add(InputApi.WmPointer);
        }

        return apis;
    }

    /// <summary>
    /// Creates a session for the specified framework-agnostic input API.
    /// For framework-specific APIs, create the session directly (see class docs).
    /// </summary>
    public static IPenSession Create(InputApi api) => api switch
    {
        InputApi.WintabSystem => new Wintab.WintabSystemSession(),
        InputApi.WintabDigitizer => new Wintab.WintabDigitizerSession(),
        InputApi.WmPointer => new Pointer.WmPointerSession(),
        _ => throw new ArgumentException(
            $"Unsupported input API: {api}. Framework-specific sessions " +
            "(WinUiPointer, WpfStylus, AvaloniaPointer) must be created directly.",
            nameof(api)),
    };

    /// <summary>
    /// Creates a session using the best available framework-agnostic API.
    /// Prefers Wintab digitizer (hi-res), then Wintab system, then WM_POINTER.
    /// </summary>
    public static IPenSession CreateDefault()
    {
        var apis = GetAvailableApis();

        if (apis.Contains(InputApi.WintabDigitizer))
            return Create(InputApi.WintabDigitizer);

        if (apis.Contains(InputApi.WintabSystem))
            return Create(InputApi.WintabSystem);

        if (apis.Contains(InputApi.WmPointer))
            return Create(InputApi.WmPointer);

        throw new InvalidOperationException(
            "No pen input API is available. Is a tablet driver installed?");
    }
}
