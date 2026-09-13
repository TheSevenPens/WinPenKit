namespace WinPenKit.WinUI;

/// <summary>
/// Which pen input APIs a WinUI 3 application can offer.
/// </summary>
/// <remarks>
/// <para><see cref="PenSessionFactory.GetAvailableApis"/> answers for any application, so it
/// cannot answer this one: it does not know the host's UI framework, and the framework
/// decides two things it has no view of -- whether <see cref="InputApi.WinUiPointer"/> can be
/// offered at all, and whether <see cref="InputApi.WmPointer"/> can reach the
/// application.</para>
/// <para><see cref="InputApi.WmPointer"/> subclasses the window procedure, and WinUI 3 consumes
/// pointer messages before a subclass sees them, so it is never offered here. That is a
/// fact about the host, which is why it lives in this package rather than in the core.</para>
/// </remarks>
public static class WinUiPenApis
{
    /// <summary>
    /// The APIs this application can offer, in the order a dropdown should list them.
    /// Use <see cref="InputApiExtensions.Label"/> for the name to show.
    /// </summary>
    public static IReadOnlyList<InputApi> GetAvailable()
    {
        // WmPointer is dropped rather than never discovered: it is genuinely present on the
        // system, and a raw Win32 process in the same session could use it. It simply cannot
        // reach this one.
        var apis = PenSessionFactory.GetAvailableApis()
            .Where(api => api != InputApi.WmPointer)
            .ToList();

        // WinUI 3's XAML pointer events are part of the framework, so they are always
        // available here.
        apis.Add(InputApi.WinUiPointer);
        return apis;
    }
}
