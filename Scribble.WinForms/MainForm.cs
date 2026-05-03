using WinPenKit;
using WinPenKit.WinForms;
using SkiaSharp;

namespace Scribble.WinForms;

public sealed class MainForm : Form
{
    private IPenSession? _session;
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 16 };

    private Point? _lastCanvasPoint;
    private double _brushSize = 6;
    private IReadOnlyList<InputApi> _apis = [];
    private DateTime _lastPointTime;
    private readonly PenButtonTracker _buttons = new();
    private static readonly Color ActiveColor = Color.LimeGreen;
    private static readonly Color InactiveColor = Color.Gray;
    private static readonly Color EraserActiveColor = Color.OrangeRed;

    // SkiaSharp bitmap-backed canvas.
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private Bitmap? _gfxBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    // Controls.
    private readonly ComboBox _apiCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Button _clearButton = new() { Text = "Clear", Width = 60 };
    private readonly TrackBar _brushSlider = new() { Minimum = 1, Maximum = 50, Value = 6, Width = 100, TickFrequency = 10 };
    private readonly Label _brushLabel = new() { Text = "Size 6 px", AutoSize = true };
    private readonly Panel _canvasPanel;

    // Telemetry labels.
    private readonly Label _proximityLabel = new() { Text = "⚫ Out", AutoSize = true };
    private readonly Label _cursorLabel = new() { Text = "Cursor: --", AutoSize = true };
    private readonly Label _rawPosLabel = new() { Text = "Raw: --,--", AutoSize = true };
    private readonly Label _screenPosLabel = new() { Text = "Screen: --,--", AutoSize = true };
    private readonly Label _appPosLabel = new() { Text = "App: --,--", AutoSize = true };
    private readonly Label _canvasPosLabel = new() { Text = "Canvas: --,--", AutoSize = true };
    private readonly Label _rawPressureLabel = new() { Text = "Raw: --", AutoSize = true };
    private readonly Label _normPressureLabel = new() { Text = "Norm: --", AutoSize = true };
    private readonly Label _azimuthLabel = new() { Text = "Azimuth: --", AutoSize = true };
    private readonly Label _altitudeLabel = new() { Text = "Altitude: --", AutoSize = true };
    private readonly Label _twistLabel = new() { Text = "Twist: --", AutoSize = true };

    // BUTTONS section — five circular indicators + raw hex.
    private readonly CircleIndicator _tipDot = new();
    private readonly CircleIndicator _eraserDot = new();
    private readonly CircleIndicator _barrel1Dot = new();
    private readonly CircleIndicator _barrel2Dot = new();
    private readonly CircleIndicator _barrel3Dot = new();
    private readonly Label _rawButtonsLabel = new() { Text = "0x00000000", AutoSize = true };

    public MainForm()
    {
        Text = "Scribble WinForms - WinPenKit";
        Size = new Size(1200, 700);

        // ── Ribbon ───────────────────────────────────────────────
        var ribbon = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.FromArgb(245, 245, 245),
            Padding = new Padding(4, 4, 4, 4)
        };

        ribbon.Controls.Add(MakeSection("APP", _apiCombo, _clearButton));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("BRUSH", _brushLabel, _brushSlider));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("PEN", _proximityLabel, _cursorLabel));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("BUTTONS",
            MakeDotRow(_tipDot, "Tip", _eraserDot, "Era"),
            MakeDotRow(_barrel1Dot, "B1", _barrel2Dot, "B2", _barrel3Dot, "B3"),
            _rawButtonsLabel));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("POSITION", _rawPosLabel, _screenPosLabel, _appPosLabel, _canvasPosLabel));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("PRESSURE", _rawPressureLabel, _normPressureLabel));
        ribbon.Controls.Add(MakeSeparator());
        ribbon.Controls.Add(MakeSection("ORIENTATION", _azimuthLabel, _altitudeLabel, _twistLabel));

        Controls.Add(ribbon);

        // ── Canvas ───────────────────────────────────────────────
        _canvasPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0)
        };
        _canvasPanel.Paint += CanvasPanel_Paint;
        _canvasPanel.Resize += (_, _) => EnsureBitmap();
        Controls.Add(_canvasPanel);
        _canvasPanel.BringToFront();

        // ── Events ───────────────────────────────────────────────
        _apiCombo.SelectedIndexChanged += (_, _) => StartSession();
        _clearButton.Click += (_, _) => { ClearBitmap(); _lastCanvasPoint = null; };
        _brushSlider.ValueChanged += (_, _) =>
        {
            _brushSize = _brushSlider.Value;
            _brushLabel.Text = $"Size {_brushSlider.Value} px";
        };

        _renderTimer.Tick += RenderTimer_Tick;

        Load += (_, _) =>
        {
            // WM_POINTER via DLL subclassing doesn't work in WinForms.
            // Use WinFormsPointer (NativeWindow WndProc override) instead.
            var allApis = PenSessionFactory.GetAvailableApis();
            var apiList = allApis.Where(a => a != InputApi.WmPointer).ToList();
            apiList.Add(InputApi.WinFormsPointer);
            _apis = apiList;
            foreach (var api in _apis)
            {
                string name = api switch
                {
                    InputApi.WintabSystem => "Wintab",
                    InputApi.WintabDigitizer => "Wintab (high-res)",
                    InputApi.WinFormsPointer => "WinForms Pointer",
                    _ => api.ToString()
                };
                _apiCombo.Items.Add(name);
            }
            if (_apiCombo.Items.Count > 0)
                _apiCombo.SelectedIndex = 0;
        };

        FormClosing += (_, _) =>
        {
            _renderTimer.Stop();
            _session?.Stop();
            _session?.Dispose();
            _skCanvas?.Dispose();
            _skBitmap?.Dispose();
            _gfxBitmap?.Dispose();
        };
    }

    // ── Ribbon helpers ───────────────────────────────────────────

    private static FlowLayoutPanel MakeSection(string title, params Control[] children)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(4, 0, 4, 0)
        };

        var header = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(85, 85, 85),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        };
        panel.Controls.Add(header);

        foreach (var child in children)
            panel.Controls.Add(child);

        return panel;
    }

    private static FlowLayoutPanel MakeDotRow(params object[] items)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        foreach (var item in items)
        {
            switch (item)
            {
                case Control c:
                    c.Margin = new Padding(0, 3, 4, 0);
                    row.Controls.Add(c);
                    break;
                case string text:
                    row.Controls.Add(new Label
                    {
                        Text = text,
                        AutoSize = true,
                        Margin = new Padding(0, 0, 8, 0),
                        ForeColor = Color.FromArgb(85, 85, 85)
                    });
                    break;
            }
        }
        return row;
    }

    private static Panel MakeSeparator()
    {
        return new Panel
        {
            BackColor = Color.FromArgb(210, 210, 210),
            Width = 1,
            Height = 90,
            Margin = new Padding(4, 0, 4, 0)
        };
    }

    // ── Bitmap management ────────────────────────────────────────

    private void EnsureBitmap()
    {
        int w = _canvasPanel.Width;
        int h = _canvasPanel.Height;
        if (w <= 0 || h <= 0) return;
        if (_skBitmap != null && _bitmapWidth == w && _bitmapHeight == h) return;

        var oldBitmap = _skBitmap;
        var oldCanvas = _skCanvas;

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);
        _bitmapWidth = w;
        _bitmapHeight = h;

        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));

        if (oldBitmap != null)
        {
            _skCanvas.DrawBitmap(oldBitmap, 0, 0);
            oldCanvas?.Dispose();
            oldBitmap.Dispose();
        }

        _gfxBitmap?.Dispose();
        _gfxBitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        CopyToGfxBitmap();
    }

    private unsafe void CopyToGfxBitmap()
    {
        if (_skBitmap == null || _gfxBitmap == null) return;

        var data = _gfxBitmap.LockBits(
            new Rectangle(0, 0, _bitmapWidth, _bitmapHeight),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        Buffer.MemoryCopy(
            (void*)_skBitmap.GetPixels(),
            (void*)data.Scan0,
            _bitmapWidth * _bitmapHeight * 4,
            _bitmapWidth * _bitmapHeight * 4);

        _gfxBitmap.UnlockBits(data);
    }

    private void CanvasPanel_Paint(object? sender, PaintEventArgs e)
    {
        if (_gfxBitmap != null)
            e.Graphics.DrawImageUnscaled(_gfxBitmap, 0, 0);
    }

    private void ClearBitmap()
    {
        _skCanvas?.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        CopyToGfxBitmap();
        _canvasPanel.Invalidate();
    }

    // ── Session lifecycle ────────────────────────────────────────

    private bool _starting;

    private void StartSession()
    {
        if (_starting) return; // Prevent re-entrant calls from combo events.
        if (_apis.Count == 0 || _apiCombo.SelectedIndex < 0) return;
        _starting = true;

        try
        {
            _renderTimer.Stop();
            _session?.Stop();
            _session?.Dispose();
            _buttons.Reset();

            var api = _apis[_apiCombo.SelectedIndex];
            System.Diagnostics.Debug.WriteLine($"[Scribble.WinForms] Starting session: {api}");

            _session = api == InputApi.WinFormsPointer
                ? new WinFormsPointerSession(this)
                : PenSessionFactory.Create(api);
            _lastCanvasPoint = null;

            EnsureBitmap();

            var error = _session.Start(Handle);
            if (error != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Scribble.WinForms] Start failed: {error}");
                Text = $"Scribble WinForms - {error}";
                _session.Dispose();
                _session = null;
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Scribble.WinForms] Session started OK");
            Text = "Scribble WinForms - WinPenKit";
            _renderTimer.Start();
        }
        finally
        {
            _starting = false;
        }
    }

    // ── Render timer ─────────────────────────────────────────────

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null || _skCanvas == null) return;

        var points = _session.DrainPoints();
        if (points.Length == 0)
        {
            if ((DateTime.UtcNow - _lastPointTime).TotalMilliseconds > 200)
                _proximityLabel.Text = "⚫ Out";
            return;
        }

        int maxP = _session.MaxPressure;
        bool drew = false;

        foreach (var pt in points)
        {
            _buttons.Update(pt);

            var screenPt = new Point((int)pt.DesktopX, (int)pt.DesktopY);
            var canvasPt = _canvasPanel.PointToClient(screenPt);

            if (canvasPt.X < 0 || canvasPt.X > _bitmapWidth ||
                canvasPt.Y < 0 || canvasPt.Y > _bitmapHeight)
            {
                _lastCanvasPoint = null;
                continue;
            }

            if (_lastCanvasPoint is { } from && pt.Pressure > 0 && maxP > 0)
            {
                float width = (float)pt.Pressure / maxP * (float)_brushSize + 0.5f;

                using var paint = new SKPaint
                {
                    Color = SKColors.Black,
                    StrokeWidth = width,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke
                };

                _skCanvas.DrawLine(from.X, from.Y, canvasPt.X, canvasPt.Y, paint);
                drew = true;
            }

            _lastCanvasPoint = canvasPt;
        }

        if (drew)
        {
            CopyToGfxBitmap();
            _canvasPanel.Invalidate();
        }

        // Update telemetry.
        var last = points[^1];
        _lastPointTime = DateTime.UtcNow;

        _proximityLabel.Text = "🟢 Proximity";
        _cursorLabel.Text = $"Cursor: {last.Cursor}";

        _rawPosLabel.Text = $"Raw: {last.RawX},{last.RawY}";
        _screenPosLabel.Text = $"Screen: {last.DesktopX:F0},{last.DesktopY:F0}";

        var appPt = PointToClient(new Point((int)last.DesktopX, (int)last.DesktopY));
        _appPosLabel.Text = $"App: {appPt.X},{appPt.Y}";

        if (_lastCanvasPoint is { } cp)
            _canvasPosLabel.Text = $"Canvas: {cp.X},{cp.Y}";

        float pct = maxP > 0 ? (float)last.Pressure / maxP * 100f : 0f;
        _rawPressureLabel.Text = $"Raw: {last.Pressure}";
        _normPressureLabel.Text = $"Norm: {pct:F1}%";

        _azimuthLabel.Text = $"Azimuth: {last.Azimuth:F1}";
        _altitudeLabel.Text = $"Altitude: {last.Altitude:F1}";
        _twistLabel.Text = $"Twist: {last.Twist:F1}";

        _tipDot.SetState((_buttons.IsTipDown && !_buttons.IsEraser) ? ActiveColor : InactiveColor);
        _eraserDot.SetState(_buttons.IsEraser ? EraserActiveColor : InactiveColor);
        _barrel1Dot.SetState(_buttons.IsBarrelDown(1) ? ActiveColor : InactiveColor);
        _barrel2Dot.SetState(_buttons.IsBarrelDown(2) ? ActiveColor : InactiveColor);
        _barrel3Dot.SetState(_buttons.IsBarrelDown(3) ? ActiveColor : InactiveColor);
        if (_buttons.LastRawButtons != 0)
            _rawButtonsLabel.Text = $"0x{_buttons.LastRawButtons:X8}";
    }
}

internal sealed class CircleIndicator : Control
{
    private Color _color = Color.Gray;

    public CircleIndicator()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(10, 10);
        TabStop = false;
    }

    public void SetState(Color color)
    {
        if (_color == color) return;
        _color = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel() => DoubleBuffered = true;
}
