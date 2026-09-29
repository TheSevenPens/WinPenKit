namespace WinPenKit.MappingWizard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Per-monitor aware, from the project file: every coordinate here is a physical pixel,
        // which is the space PenPoint.DesktopX promises and the cursor is read in.
        ApplicationConfiguration.Initialize();

        var session = ChooseSession();
        Application.Run(new WizardForm(session));
    }

    /// <summary>
    /// Offers to carry on with the last session. Changing the primary monitor's scaling properly
    /// needs a sign-out, so a plan is expected to span more than one run of the wizard.
    /// </summary>
    private static Session ChooseSession()
    {
        var monitors = Displays.Read();
        if (Session.Latest() is { } last)
        {
            int total = Planner.Build(last.Monitors).Count;
            int done = last.Steps.Count + last.Skipped.Count;
            bool sameMonitors = last.Monitors.Select(m => m.Device).Order()
                                    .SequenceEqual(monitors.Select(m => m.Device).Order());

            if (done < total && sameMonitors && last.PlanVersion == Session.CurrentPlanVersion &&
                MessageBox.Show(
                    $"Carry on with the session from {Path.GetFileName(last.Folder)}? {done} of {total} steps are done.\n\n" +
                    "No starts a new session; the old one is kept.",
                    "WinPenKit mapping wizard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                return last;
        }
        return Session.New(monitors);
    }
}
