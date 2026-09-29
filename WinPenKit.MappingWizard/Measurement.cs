using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPenKit.Wintab;

namespace WinPenKit.MappingWizard;

/// <summary>
/// Measures one step: full-screen targets on each monitor the tablet reaches, held over in turn
/// with each input API, comparing where the API puts the pen with where Windows puts the cursor.
/// </summary>
/// <remarks>
/// <para><b>Pressed and held.</b> A target counts once the pen tip has been pressed down on it
/// for <see cref="HoldTime"/>; the ring fills only while the tip is down, so a quick tap does
/// not count. A first version measured while the pen hovered, and that was confusing to use:
/// nothing says when a hover has begun, where pressing is an unmistakable act. Every window the
/// tip can land on is one of this wizard's, so pressing never hands the foreground away.</para>
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
    private readonly List<(int Monitor, int Target, Point Center, int Pass)> _targets = [];

    // For a grid scan: where the cursor has been lately, to record which way each target was
    // approached, and whether the pen left proximity since the last target.
    private readonly Queue<(TimeSpan At, Point Where)> _trail = new();
    private bool _penLeftSinceTarget;
    private (int X, int Y) _approach;
    // The nearest the cursor has come to the current target, for a target that has to be skipped.
    private (double Distance, Point Cursor, (double X, double Y)? Reported) _closest = (double.MaxValue, Point.Empty, null);
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
    private bool _pressed;
    private readonly bool _quick;
    // Quick check: the last second of (reported, cursor) pairs, kept as evidence for the answer.
    private readonly Queue<(TimeSpan At, double Dx, double Dy)> _recent = new();

    public StepResult Result { get; }

    /// <summary>Raised once, when every API has been measured or the person stops.</summary>
    public event Action<bool>? Finished;

    /// <param name="grid">
    /// A grid scan instead of four targets: <see cref="GridColumns"/> x <see cref="GridRows"/>
    /// targets per monitor, visited in order and then again in reverse. It is for finding where
    /// the driver's all-displays behaviour changes -- a spatial boundary shows up the same from
    /// both directions, a state-dependent one does not (issue #132).
    /// </param>
    /// <param name="quick">
    /// A quick check instead of targets: for each API the person moves the pen about, watches
    /// whether the red dot stays on the pointer, and answers Y or N. Gross errors -- the only kind
    /// this investigation has found -- are obvious by eye in seconds, so the four-target
    /// measurement is kept for configurations that fail a quick check.
    /// </param>
    public Measurement(Step step, IReadOnlyList<Monitor> monitors, IReadOnlyList<InputApi> apis, bool grid = false, bool quick = false)
    {
        _step = step;
        _monitors = monitors;
        _apis = [.. apis];
        _quick = quick;

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
            foreach (var (fx, fy) in quick ? [] : grid ? GridFractions() : [(0.15, 0.15), (0.85, 0.15), (0.85, 0.85), (0.15, 0.85)])
                _targets.Add((m.Number, ++n, new Point(
                    m.Bounds.X + (int)(m.Bounds.Width * fx), m.Bounds.Y + (int)(m.Bounds.Height * fy)), 1));

            _overlays.Add(new Overlay(this, m));
        }

        if (grid)
        {
            var back = _targets.AsEnumerable().Reverse().Select(t => t with { Pass = 2 }).ToList();
            _targets.AddRange(back);
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

    // 3 x 3 is enough to show where the driver switches scaling -- the switch was a whole row
    // on the 5 x 4 grid it replaced -- at under half the holds.
    public const int GridColumns = 3, GridRows = 3;

    /// <summary>Row by row, left to right, in from the edges as the four-target layout is.</summary>
    private static IEnumerable<(double, double)> GridFractions()
    {
        for (int r = 0; r < GridRows; r++)
            for (int c = 0; c < GridColumns; c++)
                yield return (0.1 + 0.8 * c / (GridColumns - 1), 0.1 + 0.8 * r / (GridRows - 1));
    }

    private InputApi CurrentApi => _apis[_apiIndex];
    private (int Monitor, int Target, Point Center, int Pass) CurrentTarget => _targets[_targetIndex];

    /// <summary>
    /// The hold radius: about 8% of the smaller side of the monitor, in its own pixels. It was 4%,
    /// which was too fussy about where the pen was held; the errors being looked for are far larger.
    /// </summary>
    private int Radius(int monitor)
    {
        var m = _monitors.First(x => x.Number == monitor);
        return Math.Max(60, Math.Min(m.Bounds.Width, m.Bounds.Height) * 8 / 100);
    }

    private void NextApi()
    {
        StopSessions();
        _apiIndex++;
        _targetIndex = 0;
        _holdStart = null;
        _reported = null;
        _pressed = false;

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
        if (_quick)
        {
            QuickTick(now, c);
            return;
        }

        var target = CurrentTarget;
        foreach (var session in _sessions)
        {
            foreach (var pt in session.DrainPoints())
            {
                _lastPen = now;
                _reported = (pt.DesktopX, pt.DesktopY);

                // Pressure, not a button flag: every API reports it the same way, where the tip
                // flag is encoded differently by Wintab and by the pointer APIs.
                _pressed = pt.Pressure > 0;
                if (_holdStart is not null && _pressed)
                    _hold.Add(new Sample(_step.Number, CurrentApi, target.Monitor, target.Target,
                        pt.RawX, pt.RawY, pt.DesktopX, pt.DesktopY, c.X, c.Y,
                        target.Pass, (long)now.TotalMilliseconds));
            }
        }

        bool penHere = now - _lastPen < PenGone;
        if (!penHere) _penLeftSinceTarget = true;

        _trail.Enqueue((now, new Point(c.X, c.Y)));
        while (_trail.Count > 0 && now - _trail.Peek().At > TimeSpan.FromMilliseconds(250)) _trail.Dequeue();
        double distance = Math.Sqrt(Math.Pow(c.X - target.Center.X, 2) + Math.Pow(c.Y - target.Center.Y, 2));
        if (penHere && distance < _closest.Distance)
            _closest = (distance, new Point(c.X, c.Y), _reported);
        bool onTarget = penHere && _pressed && distance <= Radius(target.Monitor);

        if (!onTarget)
        {
            _holdStart = null;
            _hold.Clear();
        }
        else if (_holdStart is null)
        {
            _holdStart = now;
            var from = _trail.Count > 0 ? _trail.Peek().Where : new Point(c.X, c.Y);
            _approach = (from.X - c.X, from.Y - c.Y);
        }
        else if (now - _holdStart >= HoldTime && _hold.Count > 0)
        {
            Result.Targets.Add(new TargetResult(CurrentApi, target.Monitor, target.Target, target.Center, [.. _hold],
                target.Pass, _approach.X, _approach.Y, _penLeftSinceTarget));
            AdvanceTarget();
            if (_apiIndex >= _apis.Count) return;
        }

        Invalidate();
    }

    private void AdvanceTarget()
    {
        _penLeftSinceTarget = false;
        _hold.Clear();
        _holdStart = null;
        _closest = (double.MaxValue, Point.Empty, null);
        _targetIndex++;
        if (_targetIndex >= _targets.Count) NextApi();
    }

    /// <summary>
    /// Gives up on a target the cursor cannot reach, and records that it could not, with the
    /// closest the cursor came. Measured on 28 Sep 2026: with the tablet mapped to all displays
    /// and the primary monitor scaled higher than the other, the cursor could not be brought onto
    /// part of the primary monitor at all, so an unreachable target is data, not a failed run.
    /// </summary>
    public void SkipTarget()
    {
        if (_apiIndex < 0 || _apiIndex >= _apis.Count) return;
        var t = CurrentTarget;
        string closest = _closest.Distance == double.MaxValue
            ? "the pen never came near"
            : $"the cursor came no closer than {_closest.Distance:F0} px, at ({_closest.Cursor.X},{_closest.Cursor.Y})" +
              (_closest.Reported is { } r ? $", where {CurrentApi.Label()} put the pen at ({r.X:F0},{r.Y:F0})" : "");
        Result.Unreachable.Add($"{CurrentApi.Label()}, pass {t.Pass}, monitor {t.Monitor} target {t.Target} at ({t.Center.X},{t.Center.Y}): {closest}");
        AdvanceTarget();
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

    // ── Quick check ──────────────────────────────────────────────

    private void QuickTick(TimeSpan now, POINT c)
    {
        foreach (var session in _sessions)
        {
            foreach (var pt in session.DrainPoints())
            {
                _lastPen = now;
                _reported = (pt.DesktopX, pt.DesktopY);
                _recent.Enqueue((now, pt.DesktopX - c.X, pt.DesktopY - c.Y));
            }
        }
        while (_recent.Count > 0 && now - _recent.Peek().At > TimeSpan.FromSeconds(1)) _recent.Dequeue();
        Invalidate();
    }

    /// <summary>
    /// Records the person's answer for the current API, with the last second of differences
    /// from the cursor as evidence, and moves on. The answer is the verdict; the numbers are there
    /// so a surprising answer can be checked.
    /// </summary>
    public void Answer(bool agrees)
    {
        if (!_quick || _apiIndex < 0 || _apiIndex >= _apis.Count) return;
        var recent = _recent.ToList();
        double meanAbs = recent.Count == 0 ? double.NaN
            : recent.Average(r => Math.Max(Math.Abs(r.Dx), Math.Abs(r.Dy)));
        Result.Quick.Add(new QuickVerdict(CurrentApi, agrees, meanAbs, recent.Count));
        _recent.Clear();
        NextApi();
    }

    private void PaintQuick(Monitor m, Point origin, Graphics g, float scale, Font big, Font small)
    {
        g.DrawString($"Quick check  -  step {_step.Number}  -  {CurrentApi.Label()} ({_apiIndex + 1} of {_apis.Count})",
                     big, Brushes.White, 24 * scale, 20 * scale);
        string hint = _clock.Elapsed - _lastPen > TimeSpan.FromSeconds(1)
            ? (_clock.Elapsed - _apiStarted > TimeSpan.FromSeconds(3)
                ? "No pen data from this API yet. Lift the pen away from the tablet and bring it back."
                : "Waiting for the pen...")
            : "Move the pen around this monitor. Does the red dot stay on the pointer?";
        g.DrawString(hint, small, Brushes.Gainsboro, 24 * scale, 52 * scale);
        g.DrawString("Y: yes, they agree     N: no, they don't     S: skip this API     Esc: stop", small, Brushes.Gray, 24 * scale, 76 * scale);

        if (_reported is { } rep && m.Bounds.Contains((int)rep.X, (int)rep.Y))
        {
            float x = (float)(rep.X - origin.X), y = (float)(rep.Y - origin.Y);
            using var dot = new SolidBrush(Color.FromArgb(230, 90, 80));
            g.FillEllipse(dot, x - 8 * scale, y - 8 * scale, 16 * scale, 16 * scale);
        }
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

        if (_quick && _apiIndex >= 0 && _apiIndex < _apis.Count)
        {
            PaintQuick(m, origin, g, scale, big, small);
            return;
        }

        if (_apiIndex >= 0 && _apiIndex < _apis.Count)
        {
            var target = CurrentTarget;
            int done = _targetIndex;
            string pass = _step.Scan is null ? "" : $"pass {target.Pass} of 2 ({(target.Pass == 1 ? "in order" : "in reverse")})  -  ";
            string header = $"{(_step.Scan is null ? $"Step {_step.Number} of the plan" : "Grid scan")}  -  {pass}{CurrentApi.Label()} ({_apiIndex + 1} of {_apis.Count})  -  " +
                            $"target {done + 1} of {_targets.Count}";
            g.DrawString(header, big, Brushes.White, 24 * scale, 20 * scale);

            string hint = "Press the pen down on the white circle and keep it pressed until the ring fills.";
            if (_clock.Elapsed - _lastPen > TimeSpan.FromSeconds(1))
                hint = _targetIndex == 0 && _clock.Elapsed - _apiStarted > TimeSpan.FromSeconds(3)
                    ? "No pen data from this API yet. Lift the pen away from the tablet and bring it back."
                    : "Waiting for the pen...";
            g.DrawString(hint, small, Brushes.Gainsboro, 24 * scale, 52 * scale);
            g.DrawString("N: skip a target you can't reach     S: skip this API     Esc: stop (a grid scan keeps what it has)", small, Brushes.Gray, 24 * scale, 76 * scale);

            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t.Monitor != m.Number || t.Pass != target.Pass) continue;
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
            else if (_owner._quick && e.KeyCode == Keys.Y) _owner.Answer(true);
            else if (_owner._quick && e.KeyCode == Keys.N) _owner.Answer(false);
            else if (e.KeyCode == Keys.N) _owner.SkipTarget();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}
