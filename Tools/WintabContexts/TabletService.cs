using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace WintabContexts;

/// <summary>
/// Restarting the tablet driver's service, which is what clears leaked contexts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wacom only, and it says so.</b> The service name differs by vendor and only Wacom's has been
/// tested, so this looks for that one and offers nothing when it is absent rather than guessing at
/// a name and restarting something else. Somebody with a Huion tablet gets a disabled button and a
/// sentence, which is honest; a button that restarted the wrong service would not be.
/// </para>
/// <para>
/// Not <c>TabletInputService</c>, which is Windows' own pen and touch service and nothing to do
/// with Wintab.
/// </para>
/// </remarks>
internal static class TabletService
{
    /// <summary>Wacom's Wintab service. The one that holds the context table.</summary>
    public const string WacomService = "WTabletServicePro";

    /// <summary>Whether the Wacom service is on this machine, whatever state it is in.</summary>
    /// <remarks>
    /// Asked of the registry rather than of ServiceController, which lives in a NuGet package
    /// these days. A dependency is a poor trade for one existence check, and every installed
    /// service has a key here whether it is running or not.
    /// </remarks>
    public static bool WacomServiceExists
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{WacomService}");

                return key is not null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Restart it, asking Windows for the elevation that stopping a service needs.
    /// </summary>
    /// <returns>Null when it ran, or why it did not.</returns>
    /// <remarks>
    /// Through an elevated PowerShell rather than <see cref="ServiceController"/> directly,
    /// because this process is not elevated and should not be: a diagnostic that had to be run as
    /// administrator to read a number would be a worse diagnostic. The prompt appears only for the
    /// restart, and declining it is a normal thing to do.
    /// </remarks>
    public static string? Restart()
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"Restart-Service {WacomService} -Force\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null) return "Windows did not start the elevated command.";

            process.WaitForExit();

            return process.ExitCode == 0
                ? null
                : $"The restart command exited with code {process.ExitCode}.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the administrator prompt was declined. Not a failure worth an
            // alarming message -- the user said no.
            return "Cancelled at the administrator prompt.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
