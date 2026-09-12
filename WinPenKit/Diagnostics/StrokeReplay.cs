using System.Globalization;

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

    private StrokeReplay(List<(double, double, int)> points) => Points = points;

    /// <summary>
    /// Loads a recording: <c>desktopX,desktopY,pressure</c>, with <c>#</c> comments and a
    /// header line.
    /// </summary>
    public static StrokeReplay Load(string path)
    {
        var pts = new List<(double, double, int)>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
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
        return new StrokeReplay(pts);
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
        return new StrokeReplay(moved);
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
}
