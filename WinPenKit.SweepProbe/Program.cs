using System.Globalization;
using System.Windows.Forms;
using WinPenKit;
using WinPenKit.SweepProbe;

// info                              what the system and the driver say. No pen needed.
// sweep <api> [seconds] [out.txt]   sweep the pen over the whole tablet; compare it with the cursor.
//   <api> is WintabDigitizer, WintabSystem or WmPointer.
//
// See Docs/PEN-SWEEP.md for what to do with the numbers, and what has already been learned from them.
// Applies ApplicationHighDpiMode from the project file. The project setting alone does nothing: without this
// call the probe ran DPI-unaware and printed a desktop shrunk by the display scale.
ApplicationConfiguration.Initialize();

if (args.Length == 0 || args[0] is not ("info" or "sweep"))
{
    Console.WriteLine("usage: WinPenKit.SweepProbe info");
    Console.WriteLine("       WinPenKit.SweepProbe sweep <WintabDigitizer|WintabSystem|WmPointer> [seconds] [out.txt]");

    return 2;
}

return args[0] == "info" ? Info() : Sweep(args);

static string Awareness() => Native.DpiAwareness() switch
{
    0 => "0 = UNAWARE: coordinates below are virtualised",
    1 => "1 = SYSTEM: coordinates below are scaled to the system DPI, and other monitors are stretched",
    2 => "2 = per-monitor: coordinates below are physical pixels",
    var other => $"{other} (unrecognised)",
};

static IEnumerable<string> Monitors() =>
    Screen.AllScreens.Select(screen => $"monitor {screen.DeviceName} primary={screen.Primary} {screen.Bounds}");

static int Info()
{
    // First, and said plainly: every position that follows is only as meaningful as this.
    Console.WriteLine($"DPI awareness of this probe: {Awareness()}");
    Console.WriteLine($"virtual screen: {SystemInformation.VirtualScreen}");

    foreach (var line in Monitors()) Console.WriteLine(line);

    Console.WriteLine();
    Console.WriteLine("Windows pointer devices (the rects WM_POINTER positions are normalised between):");

    foreach (var line in Native.PointerDevices()) Console.WriteLine("  " + line);

    Console.WriteLine();
    Console.WriteLine("Wintab, as the driver describes itself:");

    try
    {
        Console.WriteLine("  " + Native.WintabAxis(12, "X axis (WTI_DEVICES DVC_X)"));
        Console.WriteLine("  " + Native.WintabAxis(13, "Y axis (WTI_DEVICES DVC_Y)"));
        Console.WriteLine("  " + Native.WintabContext(3, "WTI_DEFCONTEXT"));
        Console.WriteLine("  " + Native.WintabContext(4, "WTI_DEFSYSCTX "));
        Console.WriteLine("  " + Native.WintabContext(400, "WTI_DDCTXS    "));
        Console.WriteLine("  " + Native.WintabContext(500, "WTI_DSCTXS    "));
    }
    catch (DllNotFoundException)
    {
        Console.WriteLine("  Wintab32.dll not found.");
    }

    Console.WriteLine();
    Console.WriteLine("What each WinPenKit session says about the size (PhysicalArea):");

    foreach (var api in new[] { InputApi.WintabDigitizer, InputApi.WintabSystem })
    {
        using var session = PenSessionFactory.Create(api);

        var error = session.Start();

        Console.WriteLine($"  {api}: " + (error ?? (session.PhysicalArea?.ToString() ?? "null")));
    }

    Console.WriteLine("  WmPointer: null until the pen has been seen; run `sweep WmPointer` to see it.");

    return 0;
}

static int Sweep(string[] args)
{
    if (args.Length < 2 || !Enum.TryParse<InputApi>(args[1], out var api))
    {
        Console.WriteLine("sweep needs an api: WintabDigitizer, WintabSystem or WmPointer");

        return 2;
    }

    var seconds = args.Length > 2 && int.TryParse(args[2], out var given) ? given : 40;
    var outPath = args.Length > 3 ? args[3] : null;

    var form = new Form
    {
        Text = "Pen sweep probe",
        TopMost = true,
        StartPosition = FormStartPosition.Manual,
        BackColor = System.Drawing.Color.White,
    };

    if (api == InputApi.WmPointer)
    {
        // The pointer API delivers to the window under the pen, so the window has to be wherever the
        // pen is mapped. Covering the whole desktop is the only way to not have to know. Text is drawn
        // onto the form rather than put in a label: a child window would take the messages first.
        form.FormBorderStyle = FormBorderStyle.None;
        form.Bounds = SystemInformation.VirtualScreen;
        form.Opacity = 0.35;
    }
    else
    {
        form.Size = new System.Drawing.Size(1000, 340);
        form.Location = new System.Drawing.Point(200, 200);
    }

    var text = "starting...";
    form.Paint += (_, paint) =>
        paint.Graphics.DrawString(text, new System.Drawing.Font("Consolas", 14), System.Drawing.Brushes.Black, 40, 40);

    IPenSession? session = null;
    var samples = new List<Sample>();
    var started = DateTime.MinValue;

    // The raw counts reached, shown live. The cursor cannot say where the tablet's edge is: it stops
    // at the edge of a monitor, which can be well before the tablet's, and the person sweeping cannot tell.
    int minRawX = int.MaxValue, maxRawX = int.MinValue, minRawY = int.MaxValue, maxRawY = int.MinValue;
    int lastRawX = 0, lastRawY = 0;

    var timer = new System.Windows.Forms.Timer { Interval = 5 };

    timer.Tick += (_, _) =>
    {
        if (session is null) return;

        foreach (var point in session.DrainPoints())
        {
            Native.GetCursorPos(out var cursor);
            samples.Add(new Sample(cursor.X, cursor.Y, point.DesktopX, point.DesktopY, point.RawX, point.RawY));

            (lastRawX, lastRawY) = (point.RawX, point.RawY);
            (minRawX, maxRawX) = (Math.Min(minRawX, point.RawX), Math.Max(maxRawX, point.RawX));
            (minRawY, maxRawY) = (Math.Min(minRawY, point.RawY), Math.Max(maxRawY, point.RawY));
        }

        var left = seconds - (DateTime.Now - started).TotalSeconds;
        var any = samples.Count > 0;

        text = $"{api}: sweep the pen over the WHOLE tablet, edge to edge, into every corner.\n"
            + "Keep going until raw x and raw y have each reached both ends of their range.\n\n"
            + $"raw now   x {lastRawX,6}   y {lastRawY,6}\n"
            + $"reached   x {(any ? minRawX : 0),6} .. {(any ? maxRawX : 0),6}   y {(any ? minRawY : 0),6} .. {(any ? maxRawY : 0),6}\n\n"
            + $"{samples.Count} points, {Math.Max(0, left):F0} s left";

        form.Invalidate();

        if (left <= 0)
        {
            timer.Stop();
            form.Close();
        }
    };

    form.Shown += (_, _) =>
    {
        session = PenSessionFactory.Create(api);

        var error = session.Start(form.Handle);

        if (error is not null)
        {
            text = "start failed: " + error;
            form.Invalidate();

            return;
        }

        session.CaptureRegion = PenCaptureRegion.Unbounded;
        started = DateTime.Now;
        timer.Start();
    };

    Application.Run(form);

    var report = Report(api, samples, session);

    foreach (var line in report) Console.WriteLine(line);

    if (outPath is not null) File.WriteAllLines(outPath, report);

    return samples.Count == 0 ? 1 : 0;
}

static List<string> Report(InputApi api, List<Sample> samples, IPenSession? session)
{
    var inv = CultureInfo.InvariantCulture;
    var lines = new List<string>
    {
        $"api {api}, {samples.Count} samples",
        $"DPI awareness of this probe: {Awareness()}",
        $"PhysicalArea: {session?.PhysicalArea?.ToString() ?? "null"}",
    };

    lines.AddRange(Monitors());

    if (samples.Count == 0)
    {
        lines.Add("No samples. For WmPointer the pen must pass over the probe's window; for Wintab the window must be in front.");

        return lines;
    }

    string Range(IEnumerable<double> values) => $"{values.Min().ToString("F1", inv)} .. {values.Max().ToString("F1", inv)}";

    lines.Add($"cursor   x {Range(samples.Select(s => (double)s.CursorX))}   y {Range(samples.Select(s => (double)s.CursorY))}");
    lines.Add($"position x {Range(samples.Select(s => s.X))}   y {Range(samples.Select(s => s.Y))}   (what the session reports as DesktopX/Y)");
    lines.Add($"raw      x {Range(samples.Select(s => (double)s.RawX))}   y {Range(samples.Select(s => (double)s.RawY))}   (tablet counts; hundredths of a mm for WmPointer)");

    var againstPosition = (X: Fit(samples.Select(s => (s.X, (double)s.CursorX))), Y: Fit(samples.Select(s => (s.Y, (double)s.CursorY))));
    var againstRaw = (X: Fit(samples.Select(s => ((double)s.RawX, (double)s.CursorX))), Y: Fit(samples.Select(s => ((double)s.RawY, (double)s.CursorY))));

    lines.Add($"cursor = a * position + b   x: a={againstPosition.X.Slope:F4} b={againstPosition.X.Intercept:F1} r2={againstPosition.X.R2:F4}"
        + $"   y: a={againstPosition.Y.Slope:F4} b={againstPosition.Y.Intercept:F1} r2={againstPosition.Y.R2:F4}");
    lines.Add($"cursor = a * raw + b        x: a={againstRaw.X.Slope:F6} b={againstRaw.X.Intercept:F1} r2={againstRaw.X.R2:F4}"
        + $"   y: a={againstRaw.Y.Slope:F6} b={againstRaw.Y.Intercept:F1} r2={againstRaw.Y.R2:F4}");

    // How to read the second fit: a is cursor pixels per raw count, so a pixel is 1 / a counts, and at
    // 100 counts a millimetre -- which is what a Wacom reports -- 0.01 / a millimetres.
    lines.Add("Read against the tablet's counts per millimetre (Wacom: 100) and the range actually reached; see Docs/PEN-SWEEP.md.");

    return lines;
}

static (double Slope, double Intercept, double R2) Fit(IEnumerable<(double X, double Y)> points)
{
    var all = points.ToList();
    var (meanX, meanY) = (all.Average(p => p.X), all.Average(p => p.Y));
    var (sxx, sxy, syy) = (
        all.Sum(p => (p.X - meanX) * (p.X - meanX)),
        all.Sum(p => (p.X - meanX) * (p.Y - meanY)),
        all.Sum(p => (p.Y - meanY) * (p.Y - meanY)));
    var slope = sxy / sxx;

    return (slope, meanY - slope * meanX, sxy * sxy / (sxx * syy));
}

internal readonly record struct Sample(int CursorX, int CursorY, double X, double Y, int RawX, int RawY);
