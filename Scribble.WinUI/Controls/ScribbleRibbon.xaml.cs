using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using Windows.Foundation;
using PenSession;

namespace Scribble.WinUI.Controls;

/// <summary>
/// Consolidated toolbar with labeled sections: APP, BRUSH, BUTTONS,
/// POSITION, PRESSURE, SENSORS. Replaces the individual bar controls.
/// </summary>
public sealed partial class ScribbleRibbon : UserControl
{
    private static readonly SolidColorBrush ActiveBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush InactiveBrush = new(Colors.Gray);
    private static readonly SolidColorBrush EraserActiveBrush = new(Colors.OrangeRed);

    private DateTime _lastPointTime;
    private bool _tipDown, _barrel1Down, _barrel2Down, _barrel3Down;
    private IReadOnlyList<InputApi> _availableApis = [];

    public event EventHandler? ContextModeChanged;
    public event EventHandler? ClearClicked;

    public ScribbleRibbon()
    {
        this.InitializeComponent();
        BrushSizeSlider.ValueChanged += (_, e) =>
            BrushSizeLabel.Text = $"{(int)e.NewValue} px";
    }

    // ── Public properties ───────────────────────────────────────

    public InputApi SelectedApi =>
        ContextModeCombo.SelectedIndex >= 0 && ContextModeCombo.SelectedIndex < _availableApis.Count
            ? _availableApis[ContextModeCombo.SelectedIndex]
            : (_availableApis.Count > 0 ? _availableApis[0] : InputApi.WintabSystem);

    public double BrushSize => BrushSizeSlider.Value;

    // ── Setup ────────────────────────────────────────────────────

    /// <summary>
    /// Populates the API dropdown from discovered available APIs.
    /// </summary>
    public void SetAvailableApis(IReadOnlyList<InputApi> apis)
    {
        _availableApis = apis;
        ContextModeCombo.Items.Clear();
        foreach (var api in apis)
        {
            string name = api switch
            {
                InputApi.WintabSystem => "Wintab",
                InputApi.WintabDigitizer => "Wintab (high-res)",
                InputApi.WinUiPointer => "WinUI Pointer",
                _ => api.ToString()
            };
            ContextModeCombo.Items.Add(name);
        }
        if (ContextModeCombo.Items.Count > 0)
            ContextModeCombo.SelectedIndex = 0;
    }

    public void SetMode(InputApi api)
    {
        // Mode is now implicit in the position display.
    }

    // ── Update methods ──────────────────────────────────────────

    public void UpdateTelemetry(PenTelemetry telemetry)
    {
        var pt = telemetry.Point;
        var cp = telemetry.CanvasPoint;
        int maxP = telemetry.MaxPressure;

        // Proximity
        _lastPointTime = DateTime.UtcNow;
        ProximityIndicator.Fill = ActiveBrush;

        // Position
        RawPosValue.Text = $"{pt.RawX}, {pt.RawY}";
        ScreenPosValue.Text = $"{telemetry.ScreenPoint.X:F0}, {telemetry.ScreenPoint.Y:F0}";
        AppPosValue.Text = $"{telemetry.AppPoint.X:F0}, {telemetry.AppPoint.Y:F0}";
        CanvasPosValue.Text = $"{cp.X:F1}, {cp.Y:F1}";

        // Pressure
        float pressurePct = maxP > 0 ? (float)pt.Pressure / maxP * 100f : 0f;
        RawPressureValue.Text = $"{pt.Pressure}";
        NormalizedPressureValue.Text = $"{pressurePct:F1}%";

        // Sensors
        AzimuthValue.Text = $"{pt.Azimuth / 10.0:F1}°";
        AltitudeValue.Text = $"{pt.Altitude / 10.0:F1}°";
        TwistValue.Text = $"{pt.Twist / 10.0:F1}°";
    }

    public void UpdateButtons(PenPoint pt)
    {
        // Track button press/release events
        if (pt.ButtonAction == PenButtonAction.Pressed)
        {
            switch (pt.ButtonNumber)
            {
                case PenButtonNumber.Tip: _tipDown = true; break;
                case PenButtonNumber.Barrel1: _barrel1Down = true; break;
                case PenButtonNumber.Barrel2: _barrel2Down = true; break;
                case PenButtonNumber.Barrel3: _barrel3Down = true; break;
            }
        }
        else if (pt.ButtonAction == PenButtonAction.Released)
        {
            switch (pt.ButtonNumber)
            {
                case PenButtonNumber.Tip: _tipDown = false; break;
                case PenButtonNumber.Barrel1: _barrel1Down = false; break;
                case PenButtonNumber.Barrel2: _barrel2Down = false; break;
                case PenButtonNumber.Barrel3: _barrel3Down = false; break;
            }
        }

        bool tipActive = _tipDown || pt.Pressure > 0;
        bool isEraser = pt.IsEraser;

        SetIndicator(TipIndicator, tipActive && !isEraser);
        SetIndicator(EraserIndicator, isEraser, useEraserColor: true);
        SetIndicator(Barrel1Indicator, _barrel1Down);
        SetIndicator(Barrel2Indicator, _barrel2Down);
        SetIndicator(Barrel3Indicator, _barrel3Down);

        if (pt.Buttons != 0)
            RawButtonValue.Text = $"0x{pt.Buttons:X8}";

        CursorValue.Text = $"{pt.Cursor}";
    }

    public void Tick()
    {
        if ((DateTime.UtcNow - _lastPointTime).TotalMilliseconds > 200)
            ProximityIndicator.Fill = InactiveBrush;
    }

    public string GetCopyText()
    {
        return $"API:{SelectedApi}  Raw:{RawPosValue.Text}  Screen:{ScreenPosValue.Text}  App:{AppPosValue.Text}  Canvas:{CanvasPosValue.Text}  " +
               $"Pressure:{RawPressureValue.Text} ({NormalizedPressureValue.Text})  " +
               $"Azi:{AzimuthValue.Text}  Alt:{AltitudeValue.Text}  Twist:{TwistValue.Text}  " +
               $"Buttons:{RawButtonValue.Text}  Cursor:{CursorValue.Text}";
    }

    // ── Private helpers ─────────────────────────────────────────

    private static void SetIndicator(Ellipse indicator, bool active, bool useEraserColor = false)
    {
        indicator.Fill = active
            ? (useEraserColor ? EraserActiveBrush : ActiveBrush)
            : InactiveBrush;
    }

    private void ContextModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ContextModeChanged?.Invoke(this, EventArgs.Empty);

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => ClearClicked?.Invoke(this, EventArgs.Empty);

    private void LogLink_Click(object sender, RoutedEventArgs e)
    {
        // PenSession logs to %TEMP%\PenSession.log
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PenSession.log");
        if (System.IO.File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
