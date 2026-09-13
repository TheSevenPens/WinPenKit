using WinPenKit;
using WinPenKit.Diagnostics;
using WinPenKit.TestConsole;

// The clock checks need no tablet and no window, so they run before discovery -- which asks
// for one, and would turn a headless check into a prompt nobody answers.
if (args.Any(a => string.Equals(a, "--selftest-clock", StringComparison.OrdinalIgnoreCase)))
{
    // Environment.Exit rather than return, so that adding a check does not turn this into an
    // int-returning entry point and make every existing bare return a compile error.
    Environment.Exit(ClockSelfTest.Run());
}

// Replays recorded pkTime/tick pairs through the conversion the Wintab packet handler performs.
// No tablet: the readings were taken with one and live in testdata.
if (args.Any(a => string.Equals(a, "--verify-wintab-anchoring", StringComparison.OrdinalIgnoreCase)))
{
    int v = Array.FindIndex(args, a => string.Equals(a, "--verify-wintab-anchoring", StringComparison.OrdinalIgnoreCase));
    if (v + 1 >= args.Length)
    {
        Console.Error.WriteLine("--verify-wintab-anchoring needs a path to a probe dump");
        Environment.Exit(2);
    }

    var rows = new List<(uint, long)>();
    foreach (var line in File.ReadLines(args[v + 1]))
    {
        if (line.Length == 0 || line[0] == '#' || line.StartsWith("pkTime", StringComparison.Ordinal)) continue;
        var parts = line.Split(',');
        if (parts.Length >= 2 && uint.TryParse(parts[0], out uint raw) && long.TryParse(parts[1], out long tick))
            rows.Add((raw, tick));
    }

    Console.WriteLine($"WINTAB ANCHORING, replayed over {rows.Count} recorded readings");
    int bad = WintabEpochProbe.VerifyAnchoring(Console.Out, rows);
    Console.WriteLine(bad == 0 ? "RESULT anchoring checks passed" : $"RESULT {bad} failed");
    Environment.Exit(bad == 0 ? 0 : 1);
}

// The epoch probe drives its own Wintab session and prompts for a pause mid-run, so it cannot
// share the discovery prompt below.
if (args.Any(a => string.Equals(a, "--probe-wintab-epoch", StringComparison.OrdinalIgnoreCase)))
{
    // Optional seconds argument. The caller may need time to reach the tablet before the window
    // opens, and a probe that closes before the pen ever lands reports nothing at all.
    int idx = Array.FindIndex(args, a => string.Equals(a, "--probe-wintab-epoch", StringComparison.OrdinalIgnoreCase));
    int seconds = idx + 1 < args.Length && int.TryParse(args[idx + 1], out int s) ? s : 30;

    // Optional x y after the duration. The window has to sit on the monitor being drawn on, and
    // this process is DPI-unaware, so the caller supplies coordinates in the space it sees
    // rather than this code guessing at a layout it cannot measure correctly.
    (int, int)? at = idx + 3 < args.Length
                     && int.TryParse(args[idx + 2], out int wx)
                     && int.TryParse(args[idx + 3], out int wy)
                     ? (wx, wy) : null;

    Environment.Exit(WintabEpochProbe.Run(Console.Out, TimeSpan.FromSeconds(seconds), at));
}

// ── Discovery ────────────────────────────────────────────────────

var apis = PenSessionFactory.GetAvailableApis();
Console.WriteLine($"Available APIs: {string.Join(", ", apis)}");

if (apis.Count == 0)
{
    Console.WriteLine("No pen input APIs found. Is a tablet driver installed?");
    return;
}

// ── Create session ───────────────────────────────────────────────

Console.WriteLine();
for (int i = 0; i < apis.Count; i++)
    Console.WriteLine($"  [{i}] {apis[i]}");

Console.Write($"\nSelect API [0-{apis.Count - 1}] (default 0): ");
var input = Console.ReadLine()?.Trim();
int choice = int.TryParse(input, out var c) && c >= 0 && c < apis.Count ? c : 0;

using var session = PenSessionFactory.Create(apis[choice]);
var error = session.Start();
if (error != null)
{
    Console.WriteLine($"Start failed: {error}");
    return;
}

Console.WriteLine($"\nRunning: {session.Api}");
Console.WriteLine($"MaxPressure: {session.MaxPressure}");
Console.WriteLine($"Capabilities: {session.Capabilities}");
Console.WriteLine($"DebugInfo: {session.DebugInfo}");
Console.WriteLine($"\nHover or draw with your pen. Press Enter to quit.\n");

// ── Poll loop ────────────────────────────────────────────────────

var timer = new System.Timers.Timer(100); // 10 Hz for readable console output
timer.Elapsed += (_, _) =>
{
    var points = session.DrainPoints();
    if (points.Length == 0) return;

    var pt = points[^1]; // show latest

    // RawX means a different thing on each backend, and on three of them it means nothing.
    var rawUnits = session.Conventions.RawUnits;
    string rawText = rawUnits == PenRawUnits.None
        ? "--"
        : $"{pt.RawX},{pt.RawY} ({rawUnits.Label()})";
    float pct = session.MaxPressure > 0
        ? (float)pt.Pressure / session.MaxPressure * 100f
        : 0f;

    Console.Write(
        $"\r  Desktop:{pt.DesktopX,7:F1},{pt.DesktopY,7:F1}  " +
        $"Raw:{rawText,-18}  " +
        $"P:{pt.Pressure,5} ({pct,5:F1}%)  " +
        $"Z:{pt.Z,4}  " +
        $"Cursor:{pt.Cursor}  " +
        $"Buttons:0x{pt.Buttons:X8}  " +
        $"[{points.Length} pts]   ");
};
timer.Start();

Console.ReadLine();
timer.Stop();

Console.WriteLine("\nStopped.");
