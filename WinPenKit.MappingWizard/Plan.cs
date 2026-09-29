namespace WinPenKit.MappingWizard;

/// <summary>How the monitors should be scaled for a group of steps.</summary>
internal enum ScalingSetup
{
    /// <summary>Whatever they are now. Recorded, not checked.</summary>
    AsIs,
    /// <summary>Every monitor at 100%.</summary>
    All100,
    /// <summary>Every monitor at the same scaling, other than 100%.</summary>
    AllSameAbove100,
    /// <summary>At least two monitors at different scalings.</summary>
    Mixed,
}

/// <summary>
/// One configuration to measure: how the monitors are scaled, where the tablet is mapped, and
/// what resolution the monitor under test runs at.
/// </summary>
/// <param name="MappedTo">The monitor number the tablet is mapped to, or null for all displays.</param>
/// <param name="ResolutionOn">The monitor whose resolution this step sets.</param>
/// <param name="Resolution">The resolution to set on it.</param>
/// <param name="ResolutionLabel">"native" or "lower", for the report.</param>
internal sealed record Step(
    int Number, ScalingSetup Scaling, int? MappedTo,
    int ResolutionOn, DisplayMode Resolution, string ResolutionLabel)
{
    public string Title =>
        $"{ScalingText(Scaling)}; tablet mapped to {(MappedTo is { } m ? $"monitor {m}" : "all displays")}; " +
        $"monitor {ResolutionOn} at {Resolution} ({ResolutionLabel})";

    public static string ScalingText(ScalingSetup s) => s switch
    {
        ScalingSetup.AsIs => "scaling as it is",
        ScalingSetup.All100 => "every monitor at 100%",
        ScalingSetup.AllSameAbove100 => "every monitor at the same scaling, above 100%",
        ScalingSetup.Mixed => "monitors at different scalings",
        _ => s.ToString(),
    };

    /// <summary>The monitors that get targets: the one the tablet is mapped to, or all of them.</summary>
    public IEnumerable<Monitor> TargetMonitors(IReadOnlyList<Monitor> monitors)
        => MappedTo is { } m ? monitors.Where(x => x.Number == m) : monitors;
}

/// <summary>
/// Builds the list of steps from the monitors present, and checks whether a step's settings
/// are in effect.
/// </summary>
/// <remarks>
/// <para>Every combination is too many to do by hand, so the plan varies one thing at a time
/// from a baseline and crosses only the pair most likely to matter: scaling against mapping.
/// Measured on 28 Sep 2026, mixed scaling was what broke the Wintab mapping, and whether the
/// fix held depended on whether the tablet was mapped to one display or all of them.</para>
/// <para>The order is chosen for the person doing it. Scaling is outermost, because changing
/// the primary monitor's scaling properly needs a sign-out. Mapping is next, because it means
/// opening the tablet driver. Resolution is innermost, because the wizard sets it with a button.</para>
/// </remarks>
internal static class Planner
{
    public static List<Step> Build(IReadOnlyList<Monitor> monitors)
    {
        var scalings = new List<ScalingSetup> { ScalingSetup.AsIs, ScalingSetup.All100, ScalingSetup.AllSameAbove100 };
        if (monitors.Count > 1) scalings.Add(ScalingSetup.Mixed);

        var mappings = monitors.Select(m => (int?)m.Number).ToList();
        if (monitors.Count > 1) mappings.Add(null);

        var steps = new List<Step>();
        foreach (var scaling in scalings)
        {
            foreach (var mapped in mappings)
            {
                // The resolution varied is on the mapped monitor; with all displays mapped, on
                // the first monitor.
                var mon = monitors.First(m => m.Number == (mapped ?? monitors[0].Number));
                steps.Add(new Step(steps.Count + 1, scaling, mapped, mon.Number, mon.Native, "native"));
                if (mon.LowerMode() is { } lower)
                    steps.Add(new Step(steps.Count + 1, scaling, mapped, mon.Number, lower, "lower"));
            }
        }
        return steps;
    }

    /// <summary>What still has to change before a step can be measured. Empty when ready.</summary>
    public static List<string> Unmet(Step step, IReadOnlyList<Monitor> monitors)
    {
        var unmet = new List<string>();

        var mon = monitors.FirstOrDefault(m => m.Number == step.ResolutionOn);
        if (mon is null)
            unmet.Add($"Monitor {step.ResolutionOn} is not connected.");
        else if (mon.Current.Width != step.Resolution.Width || mon.Current.Height != step.Resolution.Height)
            unmet.Add($"Monitor {step.ResolutionOn} is at {mon.Current}; this step needs {step.Resolution}.");

        var dpis = monitors.Select(m => m.Dpi).Distinct().ToList();
        switch (step.Scaling)
        {
            case ScalingSetup.All100 when dpis.Count != 1 || dpis[0] != 96:
                unmet.Add("Every monitor should be at 100% scaling. " + ScalingNow(monitors));
                break;
            case ScalingSetup.AllSameAbove100 when dpis.Count != 1 || dpis[0] == 96:
                unmet.Add("Every monitor should be at the same scaling, above 100%. " + ScalingNow(monitors));
                break;
            case ScalingSetup.Mixed when dpis.Count < 2:
                unmet.Add("At least two monitors should be at different scalings. " + ScalingNow(monitors));
                break;
        }
        return unmet;
    }

    /// <summary>
    /// Worth saying, not blocking: Windows fixes the system DPI at sign-in, from the primary
    /// monitor, and changing scaling afterwards leaves it behind. That is a real configuration
    /// people run in, so it is measured rather than refused -- but it is recorded, because it
    /// may change what the driver does.
    /// </summary>
    public static string? StaleSystemDpi(IReadOnlyList<Monitor> monitors)
    {
        var primary = monitors.FirstOrDefault(m => m.Primary);
        if (primary is null || primary.Dpi == Displays.SystemDpi) return null;
        return $"Windows is still using {Displays.SystemDpi * 100 / 96}% as its system scaling, from when you signed in, " +
               $"but the primary monitor is now at {primary.ScalePercent}%. Sign out and back in for a clean result, " +
               "or carry on to measure this case too; it is recorded either way.";
    }

    private static string ScalingNow(IReadOnlyList<Monitor> monitors)
        => "Now: " + string.Join(", ", monitors.Select(m => $"monitor {m.Number} {m.ScalePercent}%")) + ".";
}
