namespace PenSession;

/// <summary>
/// Factory for creating <see cref="IPenSession"/> instances.
/// Discovers available input APIs and creates sessions for specific APIs.
///
/// <para><b>Framework-agnostic only.</b> This factory creates sessions that
/// work in any app type (Wintab, WM_POINTER). Framework-specific sessions
/// (WinUI Pointer, WPF Stylus, Avalonia Pointer) require UI elements in
/// their constructors and must be created directly by the app:</para>
///
/// <list type="bullet">
///   <item><c>new WinUiPointerSession(element, hwnd)</c> — from PenSession.WinUI</item>
///   <item><c>new WpfStylusSession(element)</c> — from PenSession.Wpf</item>
///   <item><c>new AvaloniaPointerSession(control)</c> — from PenSession.Avalonia</item>
/// </list>
///
/// <para>All sessions implement <see cref="IPenSession"/> and can be used
/// interchangeably once created.</para>
/// </summary>
public static class PenSessionFactory
{
    /// <summary>
    /// Probes the system and returns which framework-agnostic input APIs
    /// are available. Checks for driver presence (e.g., Wintab32.dll on
    /// disk), not just OS version.
    ///
    /// <para>Does not include framework-specific APIs (WinUiPointer,
    /// WpfStylus, AvaloniaPointer). Apps should add those to the list
    /// manually if they want to offer them in a dropdown:</para>
    /// <code>
    /// var apis = new List&lt;InputApi&gt;(PenSessionFactory.GetAvailableApis());
    /// apis.Add(InputApi.WinUiPointer); // always available in WinUI 3
    /// </code>
    /// </summary>
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
