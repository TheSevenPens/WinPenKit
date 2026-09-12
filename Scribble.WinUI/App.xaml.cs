using Microsoft.UI.Xaml;
using WinPenKit.Diagnostics;

namespace Scribble.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;

        // Unpackaged WinUI does not hand the command line to OnLaunched, so read it directly.
        if (SelfTest.Requested(Environment.GetCommandLineArgs()))
            window.ArmSelfTest();

        window.Activate();
    }
}
