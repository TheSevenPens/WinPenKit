using PenSession;

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
    float pct = session.MaxPressure > 0
        ? (float)pt.Pressure / session.MaxPressure * 100f
        : 0f;

    Console.Write(
        $"\r  Desktop:{pt.DesktopX,7:F1},{pt.DesktopY,7:F1}  " +
        $"Raw:{pt.RawX,6},{pt.RawY,6}  " +
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
