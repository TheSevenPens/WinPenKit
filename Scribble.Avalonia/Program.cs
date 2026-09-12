using Avalonia;

namespace Scribble.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            // UseWin32().UseSkia() rather than UsePlatformDetect(): that extension method
            // ships in Avalonia.Desktop, which this project no longer references.
            .UseWin32()
            .UseSkia()
            // Avalonia 12 split text shaping out of Skia. Without this the app throws
            // "No text shaping system configured" at startup.
            .UseHarfBuzz()
            .LogToTrace();
}
