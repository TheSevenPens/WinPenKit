using System.Windows;
using WinPenKit.Diagnostics;

namespace Scribble.Wpf;

public partial class App : Application
{
    /// <summary>
    /// With <c>--selftest</c>, shows the window, runs the launch-time checks once the surface
    /// exists, prints the report and exits with 0 only if every check passed.
    /// </summary>
    /// <remarks>
    /// The window has to be shown. Every level 1 check is about the drawing surface, and the
    /// surface does not exist until the first layout pass - so a self test that avoided
    /// showing a window could only ever check the environment, which is the half that rarely
    /// breaks.
    /// </remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();

        if (!SelfTest.Requested(e.Args))
        {
            window.Show();
            return;
        }

        window.ContentRendered += (_, _) =>
        {
            int code = window.RunSelfTest().Emit();
            Shutdown(code);
        };
        window.Show();
    }
}
