using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPenKit.Wintab;

namespace WinPenKit.MappingWizard;

/// <summary>
/// Measures one step: full-screen targets on each monitor the tablet reaches, held over in turn
/// with each input API, comparing where the API puts the pen with where Windows puts the cursor.
/// </summary>
/// <remarks>
/// <para><b>Held, not clicked.</b> A target counts once the pen has hovered over it, still, for
/// <see cref="HoldTime"/>. A click is too easy to make by accident, and a tap would also move the
/// foreground to whatever window is under the nib, which stops Wintab delivering.</para>
/// <para><b>The cursor decides where the pen is.</b> The targets only tell the person where to
/// go. Whether the pen is over a target, and the reference every position is compared with, is
/// the cursor: the driver moves it, and Windows places it on the physical desktop correctly. The
/// nib is not a better reference -- on a pen display it sits behind glass, and on an opaque
/// tablet there is nothing to see.</para>
/// <para><b>Holding still is what makes it exact.</b> The cursor is read when packets are
/// drained, a moment after they were made, so a moving pen trails it. Over a still pen the two
/// agree, which is why the mean over the hold can be judged to a few pixels.</para>
/// </remarks>
internal sealed class Measurement : IDisposable
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);

    /// <summary>A pen report older than this means the pen has left.</summary>
    private static readonly TimeSpan PenGone = TimeSpan.FromMilliseconds(150);

    private readonly Step _step;
    private readonly IReadOnlyList<Monitor> _monitors;
    private readonly List<InputApi> _apis;
    private readonly List<Overlay> _overlays = [];
    private readonly List<(int Monitor, int Target, Point Center)> _targets = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int _apiIndex = -1;
    private int _targetIndex;
    private readonly List<IPenSession> _sessions = [];
    private readonly List<Sample> _hold = [];
    private TimeSpan? _holdStart;
    // Not MinValue: the paint code subtracts it from the clock, and that would overflow.
    private TimeSpan _lastPen = TimeSpan.FromDays(-1);
    private TimeSpan _apiStarted;
    private (double X, double Y)? _reported;
    private bool _finished;

    public StepResult Result { get; }

    /// <summary>Raised once, when every API has been measured or the person stops.</summary>
    public event Action<bool>? Finished;

    public Measurement(Step step, IReadOnlyList<Monitor> monitors, IReadOnlyList<InputApi> apis)
    {
        _step = step;
        _monitors = monitors;
        _apis = [.. apis];

        Result = new StepResult
        {
            Step = step,
            Monitors = monitors,
            SystemDpi = Displays.SystemDpi,
            Driver = WinPenKit.Diagnostics.WintabDiagnostics.DeviceName() ?? "(none)",
            DriverScreen = WinPenKit.Diagnostics.WintabDiagnostics.DriverScreen() ?? "(unreadable)",
        };

        // Four targets per monitor, in from each corner. Not at the corners: a pen display's
        // bezel and an opaque tablet's edge both make the last few percent hard to reach.
        foreach (var m in step.TargetMonitors(monitors))
        {
            int n = 0;
            foreach (var (fx, fy) in new[] { (0.15, 0.15), (0.85, 0.15), (0.85, 0.85), (0.15, 0.85) })
                _targets.Add((m.Number, ++n, new Point(
                    m.Bounds.X + (int)(m.Bounds.Width * fx), m.Bounds.Y + (int)(m.Bounds.Height * fy))));

            _overlays.Add(new Overlay(this, m));
        }

        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        foreach (var o in _overlays) o.ShowOn();
        _overlays[0].Activate();
        NextApi();
        _timer.Start();
    }

    private InputApi CurrentApi => _apis[_apiIndex];
    private (int Monitor, int Target, Point Center) CurrentTarget => _targets[_targetIndex];

    /// <summary>The hold radius: about 4% of the smaller side of the monitor, in its own pixels.</summary>
    private int Radius(int monitor)
    {
        var m = _monitors.First(x => x.Number == monitor);
        return Math.Max(30, Math.Min(m.Bounds.Width, m.Bounds.Height) * 4 / 100);
    }

    private void NextApi()
    {
        StopSessions();
        _apiIndex++;
        _targetIndex = 0;
        _holdStart = null;
        _reported = null;

        if (_apiIndex >= _apis.Count)
        {
            Finish(true);
            return;
        }

        var api = CurrentApi;
        if (api == InputApi.WmPointer)
        {
            // WM_POINTER goes to the window under the pen, so every overlay needs its own.
            foreach (var o in _overlays) StartSession(api, o.Handle);
        }
        else
        {
            StartSession(api, _overlays[0].Handle);
            if (_sessions.LastOrDefault() is WintabSessionBase wintab)
                Result.DesktopMaps[api] = wintab.DesktopMap.Description;
        }

        _apiStarted = _clock.Elapsed;
        Invalidate();
    }

    private void StartSession(InputApi api, IntPtr hwnd)
    {
        var session = PenSessionFactory.Create(api);
        session.CaptureRegion = PenCaptureRegion.Unbounded;
        if (session.Start(hwnd) is { } error)
        {
            Result.Notes.Add($"{api.Label()} did not start: {error}");
            session.Dispose();
            return;
        }
        _sessions.Add(session);
    }

    private void StopSessions()
    {
        foreach (var s in _sessions) { s.Stop(); s.Dispose(); }
        _sessions.Clear();
    }

    private void Tick()
    {
        if (_apiIndex < 0 || _apiIndex >= _apis.Count) return;

        GetCursorPos(out POINT c);

        var now = _clock.Elapsed;
        var target = CurrentTarget;
        foreach (var session in _sessions)
        {
            foreach (var pt in session.DrainPoints())
            {
                _lastPen = now;
                _reported = (pt.DesktopX, pt.DesktopY);
                if (_holdStart is not null)
                    _hold.Add(new Sample(_step.Number, CurrentApi, target.Monitor, target.Target,
                        pt.RawX, pt.RawY, pt.DesktopX, pt.DesktopY, c.X, c.Y));
            }
        }

        bool penHere = now - _lastPen < PenGone;
        double distance = Math.Sqrt(Math.Pow(c.X - target.Center.X, 2) + Math.Pow(c.Y - target.Center.Y, 2));
        bool onTarget = penHere && distance <= Radius(target.Monitor);

        if (!onTarget)
        {
            _holdStart = null;
            _hold.Clear();
        }
        else if (_holdStart is null)
        {
            _holdStart = now;
        }
        else if (now - _holdStart >= HoldTime && _hold.Count > 0)
        {
            Result.Targets.Add(new TargetResult(CurrentApi, target.Monitor, target.Target, target.Center, [.. _hold]));
            _hold.Clear();
            _holdStart = null;
            _targetIndex++;
            if (_targetIndex >= _targets.Count)
            {
                NextApi();
                return;
            }
        }

        Invalidate();
    }

    public void SkipApi()
    {
        if (_apiIndex < 0 || _apiIndex >= _apis.Count) return;
        Result.Skipped.Add(CurrentApi);
        Result.Targets.RemoveAll(t => t.Api == CurrentApi);
        NextApi();
    }

    public void Stop() => Finish(false);

    private void Finish(bool completed)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        StopSessions();
        foreach (var o in _overlays) o.Close();
        _apiIndex = _apis.Count;
        Finished?.Invoke(completed);
        Finished = null;
    }

    private void Invalidate()
    {
        foreach (var o in _overlays) o.Invalidate();
    }

    public void Dispose()
    {
        _timer.Dispose();
        StopSessions();
        foreach (var o in _overlays) o.Dispose();
    }

    // ── Drawing ──────────────────────────────────────────────────

    private void Paint(Overlay overlay, Graphics g)
    {
        var m = overlay.Monitor;
        var origin = m.Bounds.Location;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(24, 26, 30));

        float scale = m.Dpi / 96f;
        using var big = new Font("Segoe UI", 16 * scale, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);

        if (_apiIndex >= 0 && _apiIndex < _apis.Count)
        {
            var target = CurrentTarget;
            int done = _targetIndex;
            string header = $"Step {_step.Number} of the plan  -  {CurrentApi.Label()} ({_apiIndex + 1} of {_apis.Count})  -  " +
                            $"target {done + 1} of {_targets.Count}";
            g.DrawString(header, big, Brushes.White, 24 * scale, 20 * scale);

            string hint = "Hover the pen over the white circle and hold it still until the ring fills. Don't click.";
            if (_clock.Elapsed - _lastPen > TimeSpan.FromSeconds(1))
                hint = _targetIndex == 0 && _clock.Elapsed - _apiStarted > TimeSpan.FromSeconds(3)
                    ? "No pen data from this API yet. Lift the pen away from the tablet and bring it back."
                    : "Waiting for the pen...";
            g.DrawString(hint, small, Brushes.Gainsboro, 24 * scale, 52 * scale);
            g.DrawString("S: skip this API     Esc: stop this step", small, Brushes.Gray, 24 * scale, 76 * scale);

            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t.Monitor != m.Number) continue;
                var p = new Point(t.Center.X - origin.X, t.Center.Y - origin.Y);
                int r = Radius(t.Monitor);

                if (i < _targetIndex)
                {
                    using var donePen = new Pen(Color.FromArgb(90, 200, 120), 3 * scale);
                    g.DrawEllipse(donePen, p.X - r / 2, p.Y - r / 2, r, r);
                }
                else if (i == _targetIndex)
                {
                    using var ring = new Pen(Color.White, 3 * scale);
                    g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
                    g.FillEllipse(Brushes.White, p.X - 4 * scale, p.Y - 4 * scale, 8 * scale, 8 * scale);
                    if (_holdStart is { } start)
                    {
                        float sweep = (float)Math.Min(360, (_clock.Elapsed - start) / HoldTime * 360);
                        using var fill = new Pen(Color.FromArgb(90, 200, 120), 8 * scale);
                        g.DrawArc(fill, p.X - r - 8 * scale, p.Y - r - 8 * scale, 2 * r + 16 * scale, 2 * r + 16 * scale, -90, sweep);
                    }
                }
                else
                {
                    using var pending = new Pen(Color.FromArgb(90, 90, 100), 2 * scale);
                    g.DrawEllipse(pending, p.X - r / 2, p.Y - r / 2, r, r);
                }
            }

            // Where the API says the pen is. The distance from this dot to the cursor is the
            // error being measured, so a person sees a bad mapping before any report says so.
            if (_reported is { } rep && m.Bounds.Contains((int)rep.X, (int)rep.Y))
            {
                float x = (float)(rep.X - origin.X), y = (float)(rep.Y - origin.Y);
                using var dot = new SolidBrush(Color.FromArgb(230, 90, 80));
                g.FillEllipse(dot, x - 6 * scale, y - 6 * scale, 12 * scale, 12 * scale);
                g.DrawString($"{CurrentApi.Label()} says the pen is here", small, dot, x + 10 * scale, y - 20 * scale);
            }
        }
    }

    // ── Overlay window ───────────────────────────────────────────

    /// <summary>A borderless window covering one monitor exactly, in physical pixels.</summary>
    private sealed class Overlay : Form
    {
        private readonly Measurement _owner;
        public Monitor Monitor { get; }

        public Overlay(Measurement owner, Monitor monitor)
        {
            _owner = owner;
            Monitor = monitor;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Text = "WinPenKit mapping wizard";
            Bounds = monitor.Bounds;
        }

        public void ShowOn()
        {
            Show();
            Bounds = Monitor.Bounds;
        }

        // Moving a per-monitor-aware window onto a monitor at another scaling makes WinForms
        // resize it for the new DPI. This window is sized in physical pixels already, so the
        // resize is refused and the bounds put back.
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            e.Cancel = true;
            Bounds = Monitor.Bounds;
        }

        protected override void OnPaint(PaintEventArgs e) => _owner.Paint(this, e.Graphics);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) _owner.Stop();
            else if (e.KeyCode == Keys.S) _owner.SkipApi();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}
