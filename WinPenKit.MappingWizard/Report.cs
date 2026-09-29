using System.Globalization;
using System.Text;

namespace WinPenKit.MappingWizard;

/// <summary>One pen report taken while the pen was held over a target, with the cursor beside it.</summary>
internal readonly record struct Sample(
    int Step, InputApi Api, int Monitor, int Target,
    int RawX, int RawY, double DesktopX, double DesktopY, int CursorX, int CursorY,
    int Pass = 1, long Ms = 0);

/// <summary>A target that was held long enough to count, and what was recorded over it.</summary>
/// <param name="Pass">Which pass of a grid scan: 1 visits the targets in order, 2 in reverse.</param>
/// <param name="ApproachX">Where the cursor moved from over the 250 ms before the hold began,
/// relative to where the hold began.</param>
/// <param name="PenLeftBefore">Whether the pen left proximity between the previous target and this one.</param>
internal sealed record TargetResult(InputApi Api, int Monitor, int Target, Point Center, IReadOnlyList<Sample> Samples,
    int Pass = 1, int ApproachX = 0, int ApproachY = 0, bool PenLeftBefore = false)
{
    public double MeanDx => Samples.Average(s => s.DesktopX - s.CursorX);
    public double MeanDy => Samples.Average(s => s.DesktopY - s.CursorY);
    public double Error => Math.Max(Math.Abs(MeanDx), Math.Abs(MeanDy));
}

/// <summary>A quick check's answer for one API, with the last second of differences as evidence.</summary>
/// <param name="MeanError">The mean of the larger axis difference from the cursor over that second.</param>
internal sealed record QuickVerdict(InputApi Api, bool Agrees, double MeanError, int Samples);

/// <summary>Everything measured for one step, and what the machine looked like at the time.</summary>
internal sealed class StepResult
{
    public required Step Step { get; init; }
    public required IReadOnlyList<Monitor> Monitors { get; init; }
    public required uint SystemDpi { get; init; }
    public required string Driver { get; init; }
    public required string DriverScreen { get; init; }
    public List<TargetResult> Targets { get; } = [];
    public Dictionary<InputApi, string> DesktopMaps { get; } = [];
    public List<string> Notes { get; } = [];
    public List<InputApi> Skipped { get; } = [];
    public List<string> Unreachable { get; } = [];
    public List<QuickVerdict> Quick { get; } = [];
}

/// <summary>
/// Writes a session to a folder: every sample as CSV, and a summary to read or paste into an
/// issue. Both are rewritten after every step, so a session abandoned half way keeps what it had.
/// </summary>
internal sealed class Session
{
    /// <summary>The largest mean difference from the cursor, on either axis, that passes.</summary>
    // 10 px, not the 3 px it started at: the distortions this looks for are hundreds of pixels,
    // and a tighter bar failed good results on the jitter of a hand holding a pen still.
    public const double MaxError = 10.0;

    private List<StepResult> _steps = [];

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinPenKit", "MappingWizard");

    public string Folder { get; }

    /// <summary>
    /// The monitors when the session began. Their numbers are the plan's, so a resumed session
    /// numbers the monitors from these rather than afresh.
    /// </summary>
    public List<Monitor> Monitors { get; private set; } = [];

    /// <summary>Steps the person chose to skip. Kept so a resumed session does not offer them again.</summary>
    public HashSet<int> Skipped { get; private set; } = [];

    private Session(string folder) => Folder = folder;

    public static Session New(List<Monitor> monitors)
    {
        var folder = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var session = new Session(folder) { Monitors = monitors };
        session.Write();
        return session;
    }

    /// <summary>
    /// The most recent session, if there is one. A session has to survive the wizard closing:
    /// changing the primary monitor's scaling properly means signing out.
    /// </summary>
    public static Session? Latest()
    {
        if (!Directory.Exists(Root)) return null;
        foreach (var dir in Directory.GetDirectories(Root).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "session.json");
            if (!File.Exists(file)) continue;
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Json);
                if (saved is null) continue;
                return new Session(dir) { Monitors = saved.Monitors, _steps = saved.Results, Skipped = saved.Skipped, PlanVersion = saved.PlanVersion };
            }
            catch (Exception)
            {
                // A file this version cannot read is an old session, not a reason to fail.
            }
        }
        return null;
    }

    public IReadOnlyList<StepResult> Steps => _steps;

    public void Add(StepResult result)
    {
        // A quick check and a full measurement of the same step are kept together: the quick
        // answers carry over into a later measurement, and a later quick check doesn't discard
        // targets already measured.
        if (_steps.FirstOrDefault(s => s.Step.Number == result.Step.Number) is { } earlier)
        {
            if (result.Quick.Count == 0) result.Quick.AddRange(earlier.Quick);
            if (result.Targets.Count == 0) result.Targets.AddRange(earlier.Targets);
        }
        _steps.RemoveAll(s => s.Step.Number == result.Step.Number);
        _steps.Add(result);
        _steps.Sort((a, b) => a.Step.Number.CompareTo(b.Step.Number));
        Skipped.Remove(result.Step.Number);
        Write();
    }

    public void Skip(int step)
    {
        Skipped.Add(step);
        Write();
    }

    /// <param name="PlanVersion">Which plan the step numbers refer to. Version 1 put scaling outermost;
    /// version 2 puts tablet mapping outermost. A session is only resumed under the plan it was
    /// recorded with, since the same number means a different step in each.</param>
    private sealed record Saved(List<Monitor> Monitors, List<StepResult> Results, HashSet<int> Skipped, int PlanVersion = 1);

    /// <summary>The plan this build of the wizard makes. See <see cref="Saved"/>.</summary>
    public const int CurrentPlanVersion = 2;

    public int PlanVersion { get; private set; } = CurrentPlanVersion;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private void Write()
    {
        File.WriteAllText(Path.Combine(Folder, "session.json"),
            System.Text.Json.JsonSerializer.Serialize(new Saved(Monitors, _steps, Skipped, PlanVersion), Json));

        var csv = new StringBuilder("step,api,monitor,target,rawX,rawY,desktopX,desktopY,cursorX,cursorY,pass,ms,approachX,approachY,penLeftBefore\n");
        foreach (var t in _steps.SelectMany(s => s.Targets))
            foreach (var s in t.Samples)
                csv.AppendLine(Inv($"{s.Step},{s.Api},{s.Monitor},{s.Target},{s.RawX},{s.RawY},{s.DesktopX:F2},{s.DesktopY:F2},{s.CursorX},{s.CursorY},{t.Pass},{s.Ms},{t.ApproachX},{t.ApproachY},{(t.PenLeftBefore ? 1 : 0)}"));
        File.WriteAllText(Path.Combine(Folder, "samples.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(Folder, "summary.md"), Summary());
    }

    public string Summary()
    {
        var md = new StringBuilder();
        md.AppendLine("# Wintab mapping wizard");
        md.AppendLine();
        md.AppendLine($"Session {Path.GetFileName(Folder)}. A position passes when its mean difference from the " +
                      $"cursor is at most {MaxError:F0} px on each axis; the cursor is where Windows put the pen.");
        md.AppendLine();

        var apis = _steps.SelectMany(s => s.Targets.Select(t => t.Api).Concat(s.Quick.Select(q => q.Api))).Distinct().OrderBy(a => a).ToList();
        if (_steps.Count > 0)
        {
            md.AppendLine("| Step | Configuration | " + string.Join(" | ", apis.Select(a => a.Label())) + " |");
            md.AppendLine("|---|---|" + string.Concat(apis.Select(_ => "---|")));
            foreach (var s in _steps)
                md.AppendLine($"| {s.Step.Number} | {s.Step.Title} | " +
                              string.Join(" | ", apis.Select(a => Verdict(s, a))) + " |");
            md.AppendLine();
        }

        foreach (var s in _steps)
        {
            md.AppendLine($"## Step {s.Step.Number}: {s.Step.Title}");
            md.AppendLine();
            foreach (var m in s.Monitors) md.AppendLine($"- {m.Describe()}");
            md.AppendLine($"- system scaling {s.SystemDpi * 100 / 96}% (fixed at sign-in)");
            md.AppendLine($"- tablet driver: {s.Driver}");
            md.AppendLine($"- driver's screen: {s.DriverScreen}");
            foreach (var (api, map) in s.DesktopMaps) md.AppendLine($"- {api.Label()} desktop map: {map}");
            foreach (var skipped in s.Skipped) md.AppendLine($"- {skipped.Label()}: skipped");
            foreach (var u in s.Unreachable) md.AppendLine($"- **unreachable:** {u}");
            foreach (var note in s.Notes) md.AppendLine($"- note: {note}");
            md.AppendLine();

            foreach (var q in s.Quick)
                md.AppendLine(Inv($"- quick check, {q.Api.Label()}: {(q.Agrees ? "agrees" : "**disagrees**")} (mean difference from the cursor over the last second {q.MeanError:F1} px, {q.Samples} samples)"));
            if (s.Quick.Count > 0) md.AppendLine();

            md.AppendLine("| API | Monitor | Mean error x, y (px) | Worst target | Verdict |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var group in s.Targets.GroupBy(t => (t.Api, t.Monitor)))
            {
                var all = group.SelectMany(t => t.Samples).ToList();
                double dx = all.Average(p => p.DesktopX - p.CursorX), dy = all.Average(p => p.DesktopY - p.CursorY);
                var worst = group.MaxBy(t => t.Error)!;
                bool ok = group.All(t => t.Error <= MaxError);
                string verdict = ok ? "pass" : "**FAIL**";
                md.AppendLine(Inv($"| {group.Key.Api.Label()} | {group.Key.Monitor} | {dx:F1}, {dy:F1} | {worst.Error:F1} at target {worst.Target} | {verdict} |"));
            }
            md.AppendLine();

            // The driver's actual mapping, fitted from the raw values to the cursor. This is what
            // the driver did, whatever it reports; comparing it across steps is how a rule is found.
            md.AppendLine("What each API actually did, fitted from its raw values to the cursor:");
            md.AppendLine();
            foreach (var group in s.Targets.GroupBy(t => (t.Api, t.Monitor)))
            {
                var all = group.SelectMany(t => t.Samples).ToList();
                var (ax, bx) = Fit(all.Select(p => ((double)p.RawX, (double)p.CursorX)));
                var (ay, by) = Fit(all.Select(p => ((double)p.RawY, (double)p.CursorY)));
                md.AppendLine(Inv($"- {group.Key.Api.Label()}, monitor {group.Key.Monitor}: cursorX = {ax:F6} x rawX + {bx:F1}, cursorY = {ay:F6} x rawY + {by:F1}"));
            }
            md.AppendLine();

            // For a grid scan the point is which targets are off, not the average, so every
            // target is listed. The ratio is the driver's position over the cursor's about the
            // desktop origin: 1.000 where the driver sent physical pixels, the scaling ratio where
            // it rescaled about the origin, and something else where it rescaled about another
            // point. Pass 1 and pass 2 at the same target differ only if the behaviour depends
            // on how the target was reached.
            if (s.Step.Scan is not null)
            {
                md.AppendLine("Every target, in the order visited:");
                md.AppendLine();
                md.AppendLine("| Pass | Monitor | Target | Cursor x, y | Error x, y (px) | Raw / cursor x, y | Approached from | Pen left before |");
                md.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var t in s.Targets)
                {
                    double cx = t.Samples.Average(p => p.CursorX), cy = t.Samples.Average(p => p.CursorY);
                    double rx = t.Samples.Average(p => p.RawX), ry = t.Samples.Average(p => p.RawY);
                    string rx1 = cx == 0 ? "-" : Inv($"{rx / cx:F3}"), ry1 = cy == 0 ? "-" : Inv($"{ry / cy:F3}");
                    md.AppendLine(Inv($"| {t.Pass} | {t.Monitor} | {t.Target} | {cx:F0}, {cy:F0} | {t.MeanDx:F1}, {t.MeanDy:F1} | {rx1}, {ry1} | {t.ApproachX}, {t.ApproachY} | {(t.PenLeftBefore ? "yes" : "no")} |"));
                }
                md.AppendLine();
            }
        }
        return md.ToString();
    }

    private static string Verdict(StepResult s, InputApi api)
    {
        var targets = s.Targets.Where(t => t.Api == api).ToList();
        if (targets.Count == 0)
        {
            if (s.Quick.FirstOrDefault(q => q.Api == api) is { } q)
                return q.Agrees ? "agrees (quick)" : "**DISAGREES** (quick)";
            return s.Skipped.Contains(api) ? "skipped" : "-";
        }
        var worst = targets.Max(t => t.Error);
        return worst <= MaxError ? Inv($"pass ({worst:F1})") : Inv($"**FAIL** ({worst:F0} px)");
    }

    /// <summary>
    /// Least-squares line through the points. Four targets held still give four tight clusters
    /// far apart, which pins a line well; the samples within a cluster add little but noise.
    /// </summary>
    private static (double Slope, double Intercept) Fit(IEnumerable<(double X, double Y)> points)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in points) { n++; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double d = n * sxx - sx * sx;
        if (d == 0) return (0, n > 0 ? sy / n : 0);
        double slope = (n * sxy - sx * sy) / d;
        return (slope, (sy - slope * sx) / n);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
