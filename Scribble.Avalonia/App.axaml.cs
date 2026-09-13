using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WinPenKit.Diagnostics;

namespace Scribble.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var args = desktop.Args ?? [];

            if (StrokeRecorder.Requested(args, out string? recordPath))
                window.RecordTo(recordPath!);

            bool replay = StrokeReplay.Requested(args, out string? replayPath);
            if (SelfTest.Requested(args) || replay)
            {
                // Opened fires before the first layout pass has produced a surface, so the
                // checks are posted behind it at Loaded priority rather than run inline.
                window.Opened += (_, _) => Dispatcher.UIThread.Post(
                    () =>
                    {
                        desktop.Shutdown(window.RunSelfTest(replay ? replayPath : null).Emit());
                    },
                    DispatcherPriority.Loaded);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
