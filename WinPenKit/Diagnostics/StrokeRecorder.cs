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
    private readonly List<(double X, double Y, uint Pressure, long TimeUs)> _points = [];
    private string? _source;
    private int _maxPressure;
    private PenTimestampSource _timestampSource;
    private bool _spansSessions;

    /// <summary>
    /// Name the session these points are coming from, and its pressure range.
    /// </summary>
    /// <remarks>
    /// <para>Call this when a session starts, not when the recording is saved. The pressure
    /// column is raw, so a reader needs the maximum to normalise it, and by save time the
    /// maximum may belong to a different device: this sample can switch pen API while a
    /// recording is running. The same mistake was found and fixed in PenDynamicsLab, where
    /// the metadata was read at stop and wrote the new device's range over samples scaled to
    /// the old one.</para>
    /// <para>Called again with different values after points exist, the earlier description
    /// is kept -- those points belong to it -- and the file says the stream spans more than
    /// one session, because one maximum cannot describe both halves.</para>
    /// </remarks>
    public void Describe(string source, int maxPressure,
                         PenTimestampSource timestampSource = PenTimestampSource.None)
    {
        if (_points.Count > 0 && (source != _source || maxPressure != _maxPressure))
        {
            _spansSessions = true;
            return;
        }

        _source = source;
        _maxPressure = maxPressure;
        _timestampSource = timestampSource;
    }

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
        _points.Add((pt.DesktopX, pt.DesktopY, pt.Pressure, pt.TimestampMicroseconds));
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
        if (_source != null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Source: {_source}");

        // The pressure column is raw. Without this line a reader has nothing to divide by,
        // and the file describes a stroke whose pressures cannot be interpreted.
        sb.AppendLine(CultureInfo.InvariantCulture, $"# MaxPressure: {_maxPressure}");
        if (_spansSessions)
            sb.AppendLine("# WARNING: the pen API changed while this was recording. The values " +
                          "above describe the session the first points came from; later points " +
                          "came from another. Do not normalise pressure from this file, and " +
                          "do not read the time column across the change -- the two sessions " +
                          "count from different origins, so a gap that spans them measures " +
                          "nothing.");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {_points.Count} points");
        // The time column is relative to the first point, so the file needs no epoch and
        // carries no machine uptime. What it does need is the clock, because that sets what a
        // gap of zero means: on WPF it means two points the clock could not tell apart, and on
        // WM_POINTER it means two points that genuinely arrived together.
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Timestamp: {_timestampSource}");
        sb.AppendLine("desktopX,desktopY,pressure,timeUs");

        long origin = _points[0].TimeUs;
        foreach (var (x, y, p, t) in _points)
        {
            sb.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(p.ToString(CultureInfo.InvariantCulture)).Append(',');
            // Zero throughout when the backend supplies no time, which is what
            // PenTimestampSource.None in the header above says to expect.
            sb.Append((_timestampSource == PenTimestampSource.None ? 0 : t - origin)
                .ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
        return _points.Count;
    }
}
