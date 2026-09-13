using WinPenKit;
using WinPenKit.Diagnostics;
using WinPenKit.WinForms;
using SkiaSharp;

namespace Scribble.WinForms;

public sealed class MainForm : Form
{
    private IPenSession? _session;
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 16 };

    // PointF, not Point. The pen reports sub-pixel positions and Skia draws in floats; an
    // integer here would quantize the path to the pixel grid between the two, which is exactly
    // the precision the session goes to some trouble to deliver.
    private PointF? _lastCanvasPoint;
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
    private readonly ComboBox _apiCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    // AutoSize rather than a fixed width: 60px held "Clear" at 96 dpi and truncated it to "Cle"
    // at 168, which is the kind of thing that only shows up on the machine that has the display.
    private readonly Button _clearButton = new()
    {
        Text = "Clear",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(6, 0, 6, 0),
    };
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
        // Wintab hands packets to whichever context is on top of the driver's overlap order, and
        // losing focus to another application drops this one down it. Without telling the session
        // we are back, the first stroke after returning is silently swallowed. Matches what Qt
        // does on window activation, which is why Qt apps do not have the bug.
        Activated += (_, _) => _session?.OnActivated();

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

        ribbon.Controls.Add(MakeSection("PEN API", MakeRow(_apiCombo, _clearButton)));
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
            // The window manager cascades each launch a little further down, so an application
            // that fits on one run hangs below the work area a few runs later - and pen input
            // aimed at the part hanging off is discarded with no error.
            WinPenKit.WindowPlacement.ClampToWorkArea(Handle);

            _apis = WinFormsPenApis.GetAvailable();
            foreach (var api in _apis)
                _apiCombo.Items.Add(api.Label());
            if (_apiCombo.Items.Count > 0)
                _apiCombo.SelectedIndex = 0;
        };

        FormClosing += (_, _) =>
        {
            // Saved before the session is torn down, so the header still names the session the
            // points came from.
            if (_recorder != null && _recordPath != null)
            {
                int written = _recorder.Save(_recordPath);
                Console.Error.WriteLine($"[record] {written} points -> {_recordPath}");
            }

            if (_epochPath != null)
            {
                using var w = new StreamWriter(_epochPath);
                w.WriteLine("WINTAB EPOCH PROBE");
                w.WriteLine($"[INFO] session                {_session?.GetType().Name ?? "none"}");
                if (_epochSampler == null)
                {
                    w.WriteLine("[SKIP] wintab/attached         not a Wintab session; pkTime does not exist here");
                }
                else
                {
                    _epochSampler.Report(w);

                    // Beside the report, named after it. The readings cost a person and a tablet
                    // to obtain, so an error in the analysis should not cost them again.
                    string dump = Path.ChangeExtension(_epochPath, ".csv");
                    using var d = new StreamWriter(dump);
                    _epochSampler.Dump(d);
                    w.WriteLine($"[INFO] raw readings            {dump}");
                }
            }

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

    /// <summary>Lay controls out left to right, for a section that should not grow downward.</summary>
    /// <remarks>
    /// The ribbon takes its height from its tallest section, so a section that stacks tall
    /// controls vertically can push past the ribbon and have its last child clipped - which is
    /// what happened to Clear under the API combo.
    /// </remarks>
    private static FlowLayoutPanel MakeRow(params Control[] children)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        foreach (var child in children)
        {
            child.Margin = new Padding(0, 0, 6, 0);
            row.Controls.Add(child);
        }
        return row;
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

    /// <summary>
    /// Runs the launch-time acceptance checks against this form and returns the report.
    /// </summary>
    /// <remarks>
    /// Most of level 1 is close to tautological here, and that is worth knowing rather than
    /// mistaking for a strong pass. A Per-Monitor V2 WinForms app lays out in physical pixels,
    /// so the canvas cannot be sized in the wrong unit and a control origin is always whole.
    /// The checks that catch real bugs in WPF and Rust are structurally unable to fail in this
    /// framework - which is most of why it is the gentler place to start.
    /// </remarks>
    /// <summary>
    /// The form's own desktop-to-canvas conversion, exposed so a replay goes through the same
    /// code the pen does. A replay with its own arithmetic would be testing itself.
    /// </summary>
    private (double X, double Y) DesktopToCanvas(double x, double y)
    {
        var origin = _canvasPanel.PointToScreen(Point.Empty);
        return (x - origin.X, y - origin.Y);
    }

    /// <remarks>
    /// Asynchronous because of <c>L1.presentation-sampling</c>, which watches the screen until
    /// the markers it drew appear there. Awaiting yields this thread, which is the one that has
    /// to render them.
    /// </remarks>
    internal async Task<SelfTest> RunSelfTest(string? replayPath = null)
    {
        var t = new SelfTest { AppName = "Scribble.WinForms" };

        t.CheckDpiAwareness();
        t.CheckWindowPlacement(Handle);
        t.ReportScale(DeviceDpi / 96.0);

        // Layout units are device pixels here, so the logical-to-physical ratio is 1.0 even
        // though the display scale above is not. Passing the display scale would compare the
        // bitmap against a size it was never meant to have.
        t.CheckSurfacePhysical(_bitmapWidth, _bitmapHeight,
                               _canvasPanel.Width, _canvasPanel.Height, 1.0);

        var origin = _canvasPanel.PointToScreen(Point.Empty);
        t.CheckSurfaceAlignment(origin.X, origin.Y);

        // DrawImageUnscaled, so the bitmap reaches the screen at its own pixel size.
        t.CheckPresentation1To1(_bitmapWidth, _bitmapHeight,
                                _canvasPanel.Width, _canvasPanel.Height);

        if (replayPath != null)
        {
            var canvasOrigin = _canvasPanel.PointToScreen(Point.Empty);
            var stroke = StrokeReplay.Load(replayPath)
                .CenteredOn(canvasOrigin.X, canvasOrigin.Y, _bitmapWidth, _bitmapHeight);
            t.CheckReplay(stroke, DesktopToCanvas, 1.0);
        }

        // Last two, in this order: one measures what is on the screen, the other moves the
        // window. Measuring first means the window has not just been moved back.

        await MeasurePresentationSampling(t);

        t.CheckOriginTracksWindow(Handle, DesktopToCanvas, 1.0);

        return t;
    }

    /// <summary>
    /// Draws the probe's markers into the canvas, presents a frame, and lets the probe find
    /// them on the screen.
    /// </summary>
    /// <remarks>
    /// The canvas is cleared first so nothing already on it can be mistaken for a marker, and
    /// the markers are left in place: the self test shuts the application down straight after.
    /// </remarks>
    private async Task MeasurePresentationSampling(SelfTest t)
    {
        if (_skCanvas == null)
        {
            t.Skip("L1.presentation-sampling", "no drawing surface");
            return;
        }

        var probe = new PresentationProbe(_bitmapWidth, _bitmapHeight);

        _skCanvas.Clear(new SKColor(0xF0, 0xF0, 0xF0));
        probe.Draw(m =>
        {
            using var paint = new SKPaint { Color = new SKColor(m.R, m.G, m.B), IsAntialias = false };
            _skCanvas.DrawRect(m.X, m.Y, m.Size, m.Size, paint);
        });
        CopyToGfxBitmap();
        _canvasPanel.Invalidate();
        _canvasPanel.Update();

        await probe.MeasureAsync(t, Handle, TimeSpan.FromSeconds(5));
    }


    // ── Session lifecycle ────────────────────────────────────────

    private bool _starting;

    private StrokeRecorder? _recorder;
    private string? _recordPath;

    /// <summary>Starts capturing the pen stream, written to <paramref name="path"/> on close.</summary>
    /// <remarks>
    /// This backend drains the coalesced pointer history and the Avalonia one does not, so a
    /// recording from each of the same stroke is what turns that difference into two point
    /// counts instead of a judgement about how angular the ink looks. Issue 43.
    /// </remarks>
    internal void RecordTo(string path)
    {
        _recordPath = path;
        _recorder = new StrokeRecorder();
    }

    /// <summary>
    /// Collect raw Wintab <c>pkTime</c> against the system clock, and write the verdict to
    /// <paramref name="path"/> when the window closes.
    /// </summary>
    /// <remarks>
    /// This sample hosts the probe because it owns a window. Wintab delivers packets to the
    /// foreground application, so a console host collects nothing -- it has no window to hold
    /// the foreground, and pen contact gives the foreground to whatever is under the pen.
    /// Drawing in a real window is the whole fix. Pick a Wintab entry in the dropdown: on any
    /// other backend there is no pkTime and the report says so instead of guessing.
    /// </remarks>
    internal void ProbeEpochTo(string path) => _epochPath = path;

    private string? _epochPath;
    private WintabEpochSampler? _epochSampler;

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

            // Described at start, not at save: this sample switches pen API while a recording
            // is running, and the header has to name the session the points came from.
            _recorder?.Describe(_session.GetType().Name, _session.MaxPressure, _session.Conventions.Timestamp);

            // Re-attached per session, not once at startup: this sample switches pen API while
            // running, and a sampler bound to a disposed session collects nothing while looking
            // like it is working.
            if (_epochPath != null)
                _epochSampler = WintabEpochSampler.TryAttach(_session);

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
            _recorder?.Add(pt);

            // Converted by hand rather than through PointToClient, which takes an integer Point
            // and so forces the position onto the whole-pixel grid on the way in. The panel's own
            // origin is genuinely on a pixel boundary, so taking that as an integer loses nothing;
            // only the pen's position needs its precision kept.
            var origin = _canvasPanel.PointToScreen(Point.Empty);
            var canvasPt = new PointF(
                (float)(pt.DesktopX - origin.X),
                (float)(pt.DesktopY - origin.Y));

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

        // The unit comes from the session. This backend reports HIMETRIC, which is not the
        // same space as DesktopX and should not be read as though it were.
        var rawUnits = _session?.Conventions.RawUnits ?? PenRawUnits.None;
        _rawPosLabel.Text = rawUnits == PenRawUnits.None
            ? "Raw: --"
            : $"Raw: {last.RawX},{last.RawY} ({rawUnits.Label()})";
        // A pen position is sub-pixel, so this is shown to two decimals. At zero decimals the readout cannot show the one fault it would most often be used to find: a coordinate quantized to a whole pixel looks identical to a good one.
        _screenPosLabel.Text = $"Screen: {last.DesktopX:F2},{last.DesktopY:F2}";

        var appPt = PointToClient(new Point((int)last.DesktopX, (int)last.DesktopY));
        _appPosLabel.Text = $"App: {appPt.X},{appPt.Y}";

        if (_lastCanvasPoint is { } cp)
            _canvasPosLabel.Text = $"Canvas: {cp.X:F1},{cp.Y:F1}";

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
