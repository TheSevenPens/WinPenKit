using System.Globalization;
using System.Text;

namespace WinPenKit.Diagnostics;

/// <summary>
/// Writes a live pen stream to the recording format <see cref="StrokeReplay"/> reads.
/// </summary>
/// <remarks>
/// <para><see cref="StrokeReplay"/> covers everything downstream of a session by replaying a
/// recording, and says so: it cannot see inside the session itself. Comparing one session
/// against another needs a stream from each, and a stream needs a tablet and a person to draw
/// with it. This captures that stream so the comparison can happen away from the machine that
/// produced it.</para>
/// <para>The recording holds desktop pixels exactly as the session delivered them, with no
/// rounding of any kind on the way to the file: <c>R</c> round-trip formatting, so a value
/// read back equals the value written. A recorder that quantized its own output would report
/// every session as quantized.</para>
/// </remarks>
public sealed class StrokeRecorder
{
    private readonly List<(double X, double Y, uint Pressure)> _points = [];

    /// <summary>Description written into the file's header, naming what produced the stream.</summary>
    public string? Source { get; set; }

    /// <summary>Points captured so far.</summary>
    public int Count => _points.Count;

    /// <summary>
    /// Whether the command line asks for a recording, and where to write it.
    /// </summary>
    /// <remarks>
    /// <c>--record &lt;path&gt;</c>. The path is required: a recorder that chose its own
    /// filename would overwrite the previous capture, which is the one thing a person drawing
    /// a comparison pair cannot afford.
    /// </remarks>
    public static bool Requested(string[] args, out string? path)
    {
        path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--record", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                path = args[i + 1];
            return path != null;
        }
        return false;
    }

    /// <summary>Captures one point. Points with no tip pressure are skipped.</summary>
    public void Add(in PenPoint pt)
    {
        if (pt.Pressure == 0) return;
        _points.Add((pt.DesktopX, pt.DesktopY, pt.Pressure));
    }

    /// <summary>
    /// Writes the recording, and returns the number of points written. Writes nothing and
    /// returns 0 when no point was captured, so an empty file never stands in for a stroke
    /// nobody drew.
    /// </summary>
    public int Save(string path)
    {
        if (_points.Count == 0) return 0;

        var sb = new StringBuilder();
        sb.AppendLine("# Pen stroke recorded from a live session, in desktop pixels.");
        if (Source != null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Source: {Source}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {_points.Count} points");
        sb.AppendLine("desktopX,desktopY,pressure");

        foreach (var (x, y, p) in _points)
        {
            sb.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(p.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
        return _points.Count;
    }
}
