namespace WinPenKit.WinForms;

/// <summary>
/// Which pen input APIs a WinForms application can offer.
/// </summary>
/// <remarks>
/// <para><see cref="PenSessionFactory.GetAvailableApis"/> answers for any application, so it
/// cannot answer this one: it does not know the host's UI framework, and the framework
/// decides two things it has no view of -- whether <see cref="InputApi.WinFormsPointer"/> can be
/// offered at all, and whether <see cref="InputApi.WmPointer"/> can reach the
/// application.</para>
/// <para><see cref="InputApi.WmPointer"/> subclasses the window procedure, and WinForms consumes
/// pointer messages before a subclass sees them, so it is never offered here. That is a
/// fact about the host, which is why it lives in this package rather than in the core.</para>
/// </remarks>
public static class WinFormsPenApis
{
    /// <summary>
    /// The APIs this application can offer, in the order a dropdown should list them.
    /// Use <see cref="InputApiExtensions.Label"/> for the name to show.
    /// </summary>
    /// <remarks>
    /// <see cref="InputApi.WinFormsPointer"/> is included only when the pointer API is
    /// present. It reads the same WM_POINTER messages as
    /// <see cref="InputApi.WmPointer"/> and differs only in how it receives them, so a
    /// system without that API cannot offer it either.
    /// </remarks>
    public static IReadOnlyList<InputApi> GetAvailable()
    {
        // WmPointer is dropped rather than never discovered: it is genuinely present on the
        // system, and a raw Win32 process in the same session could use it. It simply cannot
        // reach this one.
        var apis = PenSessionFactory.GetAvailableApis()
            .Where(api => api != InputApi.WmPointer)
            .ToList();

        // Same messages as WmPointer, received through a NativeWindow WndProc override
        // instead of a subclass -- so it needs the same API to be present.
        if (PointerApi.IsAvailable())
            apis.Add(InputApi.WinFormsPointer);
        return apis;
    }
}
