using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using PenSession;

namespace Scribble.WinUI;

/// <summary>
/// WinUI 3 pen scribble demo. Composes a <see cref="Controls.ScribbleRibbon"/>
/// (consolidated status/control bar) with a <see cref="Controls.DrawingCanvas"/>
/// and a <see cref="PenSessionWinUI3"/> for pen input.
/// </summary>
public sealed partial class MainWindow : Window
{
    private PenSessionWinUI3? _session;
    private readonly CanvasInfoCache _canvasInfo = new();

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public MainWindow()
    {
        this.InitializeComponent();
        _renderTimer.Tick += RenderTimer_Tick;

        // Populate the toolbar dropdown with discovered APIs + WinUI Pointer.
        var apis = new List<InputApi>(PenSessionFactory.GetAvailableApis());
        apis.Add(InputApi.WinUiPointer); // Always available in WinUI 3 apps
        Toolbar.SetAvailableApis(apis);

        Toolbar.ContextModeChanged += (_, _) =>
        {
            if (_session?.IsRunning == true)
                Restart();
        };

        Toolbar.ClearClicked += (_, _) => Canvas.Clear();

        Canvas.SizeChanged += (_, e) =>
        {
            _canvasInfo.Width = e.NewSize.Width;
            _canvasInfo.Height = e.NewSize.Height;

            if (_session?.IsRunning != true && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                Start();
        };

        Closed += (_, _) =>
        {
            Stop();
            _session?.Dispose();
            _renderTimer.Stop();
        };
    }

    private void CopyPenData_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var text = Toolbar.GetCopyText();
        if (_session != null)
        {
            var debug = _session.DebugInfo;
            if (debug.Length > 0)
                text += "\n" + debug;
        }
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        args.Handled = true;
    }

    private void Start()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _session ??= new PenSessionWinUI3(hwnd, _canvasInfo, Canvas);

        _canvasInfo.PositionInWindow = Canvas.GetPositionInWindow();

        var api = Toolbar.SelectedApi;
        var error = _session.Start(api);

        if (error != null)
        {
            System.Diagnostics.Debug.WriteLine($"[Scribble.WinUI] Start failed: {error}");
            Title = $"WinTab Scribble - {error}";
            return;
        }

        if (_session.IsRunning)
        {
            Title = "WinTab Scribble - WinUI 3";
            Toolbar.SetMode(_session.Api);
            _renderTimer.Start();
        }
    }

    private void Stop()
    {
        _renderTimer.Stop();
        _session?.Stop();
    }

    private void Restart()
    {
        Stop();
        Start();
    }

    private void RenderTimer_Tick(object? sender, object e)
    {
        if (_session == null) return;

        _canvasInfo.PositionInWindow = Canvas.GetPositionInWindow();
        _session.BrushSize = Toolbar.BrushSize;

        Toolbar.Tick();

        var segments = _session.DrainSegments();
        foreach (var seg in segments)
        {
            Canvas.QueueStroke(
                (float)seg.From.X, (float)seg.From.Y,
                (float)seg.To.X, (float)seg.To.Y,
                seg.Width);
        }

        Canvas.Flush();

        if (_session.HasNewTelemetry)
        {
            Toolbar.UpdateTelemetry(_session.GetTelemetry());

            foreach (var pt in _session.LastDrainedPoints)
                Toolbar.UpdateButtons(pt);
        }
    }

    private sealed class CanvasInfoCache : ICanvasInfo
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public Point PositionInWindow { get; set; }
    }
}
