using System.Globalization;
using System.Runtime.InteropServices;

namespace WinPenKit.Diagnostics;

/// <summary>
/// A recorded pen stroke in desktop pixels, and the level 2 and 3 checks that replay it
/// through an application's coordinate conversion.
/// </summary>
/// <remarks>
/// <para>The self test's level 0 and 1 checks cover the drawing surface and need no input.
/// These cover the other half - whether the path the application draws is still the path the
/// pen took - and that needs pen data. Replaying a recording supplies it without a tablet and
/// without a person, which is the only way those checks can run in CI or under an agent.</para>
/// <para><b>What this does not cover.</b> A recording holds what the session produced, so
/// replaying it exercises everything downstream of the session and nothing inside it. An
/// application that converts perfectly can still be fed pre-quantized coordinates by its own
/// session - which is exactly what WPF's stylus stack did - and no amount of replaying will
/// show it. That half needs real hardware. The <c>L2.recording-subpixel</c> check exists to
/// make the boundary visible: it reports whether the data being replayed is sub-pixel at all,
/// so a pass here is never mistaken for a statement about the session.</para>
/// </remarks>
public sealed class StrokeReplay
{
    /// <summary>Points as recorded, in desktop pixels.</summary>
    public IReadOnlyList<(double X, double Y, int Pressure)> Points { get; }

    /// <summary>
    /// The pressure range the recording was captured under, or 0 when the file does not say.
    /// </summary>
    /// <remarks>
    /// <see cref="Points"/> carries raw pressure, so normalising it needs this. Recordings
    /// written before <see cref="StrokeRecorder"/> emitted the header line report 0, which
    /// means "not stated" rather than "zero" -- check before dividing.
    /// </remarks>
    public int MaxPressure { get; }

    private StrokeReplay(List<(double, double, int)> points, int maxPressure)
    {
        Points = points;
        MaxPressure = maxPressure;
    }

    /// <summary>
    /// Loads a recording: <c>desktopX,desktopY,pressure</c>, with <c>#</c> comments and a
    /// header line.
    /// </summary>
    public static StrokeReplay Load(string path)
    {
        var pts = new List<(double, double, int)>();
        int maxPressure = 0;
        const string maxPressureKey = "# MaxPressure:";

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith(maxPressureKey, StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[maxPressureKey.Length..].Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out maxPressure);
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;

            string[] f = line.Split(',');
            if (f.Length < 3) continue;
            if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                continue;   // the header
            if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                continue;
            int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p);
            pts.Add((x, y, p));
        }
        return new StrokeReplay(pts, maxPressure);
    }

    /// <summary>
    /// The recording shifted so it sits inside a canvas of the given size at the given desktop
    /// origin.
    /// </summary>
    /// <remarks>
    /// The shift is a whole number of pixels, deliberately. A fractional shift would change
    /// every coordinate's fractional part and so change the very thing being measured; an
    /// integer one relocates the stroke while leaving it bit-for-bit as sub-pixel as it was.
    /// </remarks>
    public StrokeReplay CenteredOn(double canvasOriginX, double canvasOriginY,
                                   double canvasWidthPx, double canvasHeightPx)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y, _) in Points)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        double dx = Math.Round(canvasOriginX + (canvasWidthPx - (maxX - minX)) / 2 - minX);
        double dy = Math.Round(canvasOriginY + (canvasHeightPx - (maxY - minY)) / 2 - minY);

        var moved = new List<(double, double, int)>(Points.Count);
        foreach (var (x, y, p) in Points)
            moved.Add((x + dx, y + dy, p));

        // Carries MaxPressure through. A shifted recording is the same capture in a different
        // place; dropping the scale here would lose it on the one path every replay takes.
        return new StrokeReplay(moved, MaxPressure);
    }

    /// <summary>
    /// Locates the bundled reference recording by walking up from the executable, or returns
    /// null. Samples run from several different output layouts, so a search beats a path.
    /// </summary>
    public static string? FindDefaultRecording()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "testdata", "reference-stroke.csv");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Whether the command line asks for a replay, and with which recording.</summary>
    /// <remarks>
    /// <c>--replay</c> alone uses the bundled reference stroke; <c>--replay &lt;path&gt;</c>
    /// uses a recording of your own, which is how a stream captured from real hardware gets
    /// checked against the same assertions.
    /// </remarks>
    public static bool Requested(string[] args, out string? path)
    {
        path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--replay", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                path = args[i + 1];
            path ??= FindDefaultRecording();
            return true;
        }
        return false;
    }

    /// <summary>Mean angle between consecutive segments, in degrees.</summary>
    /// <remarks>
    /// The diagnostic that survived this investigation when image-derived ones did not.
    /// Quantizing a path to a pixel grid leaves only a handful of directions a short segment
    /// can point in, so the path stops following the pen and starts zigzagging - which shows
    /// up here and is invisible to almost everything else.
    /// </remarks>
    public static double MeanTurnAngle(IReadOnlyList<(double X, double Y)> pts)
    {
        double sum = 0;
        int n = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double ax = pts[i].X - pts[i - 1].X, ay = pts[i].Y - pts[i - 1].Y;
            double bx = pts[i + 1].X - pts[i].X, by = pts[i + 1].Y - pts[i].Y;
            double na = Math.Sqrt(ax * ax + ay * ay), nb = Math.Sqrt(bx * bx + by * by);
            if (na < 1e-9 || nb < 1e-9) continue;

            double cos = Math.Clamp((ax * bx + ay * by) / (na * nb), -1, 1);
            sum += Math.Acos(cos) * 180.0 / Math.PI;
            n++;
        }
        return n > 0 ? sum / n : 0;
    }
}

public static class SelfTestReplay
{
    /// <summary>
    /// Runs the level 2 and 3 checks: replays <paramref name="stroke"/> through
    /// <paramref name="convert"/> and measures what came out.
    /// </summary>
    /// <param name="test">Report to add the results to.</param>
    /// <param name="stroke">Recording, already positioned over the canvas.</param>
    /// <param name="convert">The application's own desktop-to-canvas conversion.</param>
    /// <param name="canvasScale">Canvas units to device pixels, for the snap test.</param>
    public static void CheckReplay(this SelfTest test, StrokeReplay stroke,
                                   Func<double, double, (double X, double Y)> convert,
                                   double canvasScale)
    {
        var input = new List<(double X, double Y)>(stroke.Points.Count);
        var output = new List<(double X, double Y)>(stroke.Points.Count);
        foreach (var (x, y, _) in stroke.Points)
        {
            input.Add((x, y));
            output.Add(convert(x, y));
        }

        if (input.Count < 3)
        {
            test.Skip("L2.recording-subpixel", "recording too short");
            return;
        }

        // Whether the data being replayed is sub-pixel at all. This is a statement about the
        // recording, not about the application - but without it, a clean run below could mean
        // either a lossless conversion or a conversion that had nothing left to lose.
        int integral = 0;
        foreach (var (x, y) in input)
            if (Math.Abs(x - Math.Round(x)) < 1e-9 && Math.Abs(y - Math.Round(y)) < 1e-9)
                integral++;
        double integralPct = 100.0 * integral / input.Count;
        test.Check("L2.recording-subpixel", integralPct < 5.0,
            $"{integralPct:F1}% of {input.Count} recorded points are on whole pixels" +
            (integralPct < 5.0 ? "" : "  <- the recording is already quantized; nothing below can fail"));

        // Whether the conversion put the path back on the pixel grid. A legitimate pen stream
        // essentially never lands on whole device pixels, so a high percentage here means an
        // integer-typed API somewhere in the conversion.
        int snapped = 0;
        foreach (var (x, y) in output)
            if (Math.Abs(x * canvasScale - Math.Round(x * canvasScale)) < 1e-6 &&
                Math.Abs(y * canvasScale - Math.Round(y * canvasScale)) < 1e-6)
                snapped++;
        double snappedPct = 100.0 * snapped / output.Count;
        test.Check("L3.conversion-snap", snappedPct < 5.0,
            $"{snappedPct:F1}% of converted points land on whole device pixels" +
            (snappedPct < 5.0 ? "" : "  <- an integer-typed API is truncating the position"));

        // The strongest of the three, and the only one that needs no threshold. The conversion
        // is a translation and a uniform scale, both of which preserve angles exactly, so a
        // lossless implementation reproduces the input's turn angle to the decimal. Comparing
        // against a fixed number instead would need calibrating against how the stroke was
        // drawn; comparing against the input calibrates itself.
        double inAngle = StrokeReplay.MeanTurnAngle(input);
        double outAngle = StrokeReplay.MeanTurnAngle(output);
        double delta = Math.Abs(outAngle - inAngle);
        bool lossless = delta < 0.05;

        test.Check("L3.conversion-lossless", lossless,
            $"mean turn angle in {inAngle:F2} deg, out {outAngle:F2} deg (delta {delta:F2})" +
            (lossless ? "" : "  <- the conversion changed the shape of the path"));
    }
    /// <summary>
    /// Whether the application's canvas origin follows the window when the window moves.
    /// </summary>
    /// <remarks>
    /// <para>Every other check here converts points while the window holds still, and a canvas
    /// origin that is simply wrong cancels out of all of them: the replay positions its input
    /// relative to the origin the application reports, then the application subtracts the same
    /// value back off. <c>L1.surface-alignment</c> does not close the gap either, because it
    /// asks whether the origin is a whole number rather than whether it is the right one.</para>
    /// <para>So this check moves the window a known distance and converts the same desktop
    /// point again. A conversion that reads the origin fresh reports a canvas position shifted
    /// by exactly that distance. One that cached the origin reports the same position as
    /// before, because nothing told it the window moved.</para>
    /// <para>That is the fault this exists for: <c>Scribble.Wpf</c> cached its origin at
    /// bitmap-creation time, dragging the window raised no size change, and every stroke after
    /// a drag landed the drag distance away from the pen while all nine checks passed.</para>
    /// <para>The window is moved and put back, so run this after the checks that measure where
    /// the window sits.</para>
    /// </remarks>
    /// <param name="hwnd">The application window.</param>
    /// <param name="convert">
    /// The application's desktop-to-canvas conversion, obtained the way the input path obtains
    /// it. A delegate that re-reads the origin when the pen code would not re-read it tests
    /// something the pen never does.
    /// </param>
    /// <param name="canvasScale">
    /// Device pixels per unit of whatever <paramref name="convert"/> returns. The same value
    /// passed to <see cref="CheckReplay"/>.
    /// </param>
    public static void CheckOriginTracksWindow(this SelfTest test, IntPtr hwnd,
                                               Func<double, double, (double X, double Y)> convert,
                                               double canvasScale)
    {
        const string id = "L3.origin-tracks-window";

        if (hwnd == IntPtr.Zero) { test.Skip(id, "no window handle"); return; }
        if (IsZoomed(hwnd)) { test.Skip(id, "window is maximized; moving it would restore it"); return; }
        if (!GetWindowRect(hwnd, out RECT before)) { test.Skip(id, "GetWindowRect failed"); return; }
        if (canvasScale <= 0) { test.Skip(id, "canvas scale not reported"); return; }

        // Odd numbers, so a conversion that happens to quantize cannot match by luck, and small
        // enough that the window stays where it was to within a nudge.
        const int dx = 37, dy = 23;

        // One fixed point on the desktop, converted before and after. Which point does not
        // matter: the conversion is affine, so every point shifts by the same amount.
        double probeX = before.Left + 64, probeY = before.Top + 64;

        var beforeMove = convert(probeX, probeY);

        if (!SetWindowPos(hwnd, IntPtr.Zero, before.Left + dx, before.Top + dy, 0, 0, MoveOnly))
        {
            test.Skip(id, "SetWindowPos failed");
            return;
        }

        var afterMove = convert(probeX, probeY);

        bool restored = SetWindowPos(hwnd, IntPtr.Zero, before.Left, before.Top, 0, 0, MoveOnly);

        // The canvas moved with the window, so a fixed point on the desktop sits that much
        // further back in canvas coordinates.
        double expectedX = beforeMove.X - dx / canvasScale;
        double expectedY = beforeMove.Y - dy / canvasScale;
        double errX = Math.Abs(afterMove.X - expectedX);
        double errY = Math.Abs(afterMove.Y - expectedY);

        // Half a device pixel. Wide enough for a conversion that rounds its origin, narrow
        // enough that a cached origin - out by the full 37 and 23 - cannot pass.
        double tolerance = 0.5 / canvasScale;
        bool tracks = errX < tolerance && errY < tolerance;

        string detail = tracks
            ? $"moved {dx},{dy}px; conversion followed"
            : $"moved {dx},{dy}px; conversion off by {errX * canvasScale:F2},{errY * canvasScale:F2}px" +
              "  <- the canvas origin is cached and nothing refreshes it when the window moves";

        test.Check(id, tracks, detail + (restored ? "" : "  (window not restored)"));
    }

    private const uint MoveOnly = 0x0001 | 0x0004 | 0x0010;   // NOSIZE | NOZORDER | NOACTIVATE

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);
}
