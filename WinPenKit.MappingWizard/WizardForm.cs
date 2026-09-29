using System.Diagnostics;

namespace WinPenKit.MappingWizard;

/// <summary>
/// The wizard's own window: the plan, what to set for the selected step, and whether it is set.
/// </summary>
internal sealed class WizardForm : Form
{
    private readonly Session _session;
    private readonly List<Step> _plan;
    private readonly HashSet<string> _changedDevices = [];
    private List<Monitor> _monitors;
    private Measurement? _measurement;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
    };
    private readonly Label _instructions = new() { Dock = DockStyle.Top, AutoSize = false };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false };
    private readonly Button _apply = new() { AutoSize = true };
    private readonly Button _measure = new() { Text = "Measure", AutoSize = true };
    private readonly Button _skip = new() { Text = "Skip step", AutoSize = true };
    private readonly Button _results = new() { Text = "Open results", AutoSize = true };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, AutoSize = false };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };

    public WizardForm(Session session)
    {
        _session = session;
        _monitors = Displays.Read(session.Monitors);
        _plan = Planner.Build(session.Monitors);

        Text = "WinPenKit - Wintab mapping wizard";
        // Sized by hand for the monitor it opens on: every size below is 96-DPI pixels, scaled.
        AutoScaleMode = AutoScaleMode.None;
        int S(int px) => px * DeviceDpi / 96;
        _instructions.Height = S(250); _status.Height = S(170); _footer.Height = S(60);
        foreach (var l in new[] { _instructions, _status, _footer }) l.Padding = new Padding(S(8));
        ClientSize = new Size(S(1180), S(680));
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        _list.Columns.Add("Step", S(50));
        _list.Columns.Add("Configuration", S(560));
        _list.Columns.Add("Result", S(90));
        foreach (var step in _plan)
            _list.Items.Add(new ListViewItem([step.Number.ToString(), step.Title, ""]) { Tag = step });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(52), Padding = new Padding(S(8)) };
        buttons.Controls.AddRange([_apply, _measure, _skip, _results]);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_status);
        right.Controls.Add(buttons);
        right.Controls.Add(_instructions);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        Load += (_, _) => split.SplitterDistance = S(700);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        Controls.Add(_footer);

        _footer.Text = $"Results: {_session.Folder}\nResolutions this wizard changes are put back when it closes.";

        _list.SelectedIndexChanged += (_, _) => Refresh(false);
        _apply.Click += (_, _) => ApplyResolution();
        _measure.Click += (_, _) => Measure();
        _skip.Click += (_, _) => SkipStep();
        _results.Click += (_, _) => Process.Start(new ProcessStartInfo(_session.Folder) { UseShellExecute = true });
        _refresh.Tick += (_, _) => Refresh(true);

        UpdateResults();
        SelectNextPending(0);
        _refresh.Start();
    }

    private Step? Selected => _list.SelectedItems.Count > 0 ? (Step)_list.SelectedItems[0].Tag! : null;

    private void SelectNextPending(int from)
    {
        var next = _plan.Skip(from).FirstOrDefault(s => !Done(s)) ?? _plan.FirstOrDefault(s => !Done(s));
        if (next is null) return;
        var item = _list.Items[_plan.IndexOf(next)];
        item.Selected = true;
        item.EnsureVisible();
    }

    private bool Done(Step s) => _session.Steps.Any(r => r.Step.Number == s.Number) || _session.Skipped.Contains(s.Number);

    private void Refresh(bool reread)
    {
        if (_measurement is not null) return;
        if (reread) _monitors = Displays.Read(_session.Monitors);

        var step = Selected;
        if (step is null)
        {
            _instructions.Text = "Every step is done. The summary is in the results folder.";
            _status.Text = "";
            _apply.Visible = _measure.Enabled = _skip.Enabled = false;
            return;
        }

        var target = _monitors.FirstOrDefault(m => m.Number == step.ResolutionOn);
        string mapping = step.MappedTo is { } mm && _monitors.FirstOrDefault(m => m.Number == mm) is { } mapped
            ? $"map the pen to monitor {mm} only ({mapped.Device.TrimStart('\\', '.')}, {mapped.Current}, {Where(mapped)})"
            : "map the pen to all displays";

        // Resolution comes first, because it moves scaling. Windows stores each monitor's scaling
        // as steps above or below the scaling it recommends, and the recommendation depends on
        // the resolution -- so setting scaling and then resolution undid the scaling, and the
        // first version of this wizard asked for them in that order.
        string scalingNote = step.Scaling == ScalingSetup.AsIs
            ? "leave it as it is"
            : $"{Step.ScalingText(step.Scaling)}. This is for every monitor, not only monitor {step.ResolutionOn}";
        _instructions.Text =
            $"Step {step.Number} of {_plan.Count}\n\n" +
            $"1.  Resolution: monitor {step.ResolutionOn} at {step.Resolution} ({step.ResolutionLabel}).  Use the button below.\n\n" +
            $"2.  Scaling: {scalingNote}.  (Settings > System > Display > Scale.)  Do this after the resolution: " +
            "changing a monitor's resolution also changes its scaling.\n\n" +
            $"3.  Tablet: in the tablet driver's settings, {mapping}.\n\n" +
            "Then press Measure. Targets appear on the monitors the pen should reach.";

        var unmet = Planner.Unmet(step, _monitors);
        var lines = new List<string>();
        lines.Add(unmet.Any(u => u.Contains("needs")) || target is null ? "✗  resolution" : "✓  resolution");
        lines.Add(unmet.Any(u => u.Contains("scaling")) ? "✗  scaling" : "✓  scaling");
        lines.Add("?  tablet mapping: cannot be read; if it is wrong, the targets will be out of the pen's reach");
        lines.AddRange(unmet);
        if (Planner.StaleSystemDpi(_monitors) is { } stale) lines.Add("Note: " + stale);
        _status.Text = string.Join("\n", lines);

        bool needsResolution = target is not null &&
            (target.Current.Width != step.Resolution.Width || target.Current.Height != step.Resolution.Height);
        _apply.Visible = needsResolution;
        _apply.Text = $"Set monitor {step.ResolutionOn} to {step.Resolution}";
        _measure.Enabled = _skip.Enabled = true;
    }

    private static string Where(Monitor m) => m.Primary ? "the primary" : $"at {m.Bounds.X},{m.Bounds.Y}";

    private void ApplyResolution()
    {
        var step = Selected;
        var target = step is null ? null : _monitors.FirstOrDefault(m => m.Number == step.ResolutionOn);
        if (step is null || target is null) return;

        if (Displays.SetResolution(target.Device, step.Resolution) is { } error)
        {
            MessageBox.Show(this, error, "Resolution not changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _changedDevices.Add(target.Device);

        using var keep = new KeepDialog($"Monitor {target.Number} is now at {step.Resolution}.");
        if (keep.ShowDialog(this) != DialogResult.OK)
            Displays.Restore(target.Device);

        Refresh(true);
    }

    private void Measure()
    {
        var step = Selected;
        if (step is null) return;

        _monitors = Displays.Read(_session.Monitors);
        var unmet = Planner.Unmet(step, _monitors);
        if (unmet.Count > 0 &&
            MessageBox.Show(this, "This step's settings are not all in effect:\n\n" + string.Join("\n", unmet) +
                                  "\n\nMeasure anyway? The difference is recorded with the result.",
                            "Settings don't match", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var available = PenSessionFactory.GetAvailableApis();
        var apis = new[] { InputApi.WintabSystem, InputApi.WintabDigitizer, InputApi.WmPointer }
            .Where(available.Contains).ToList();
        if (apis.Count == 0)
        {
            MessageBox.Show(this, "No pen input API is available. Is a tablet attached?", "Nothing to measure");
            return;
        }

        _refresh.Stop();
        _measurement = new Measurement(step, _monitors, apis);
        foreach (var u in unmet) _measurement.Result.Notes.Add("measured with this unmet: " + u);
        if (Planner.StaleSystemDpi(_monitors) is { } stale) _measurement.Result.Notes.Add(stale);

        _measurement.Finished += completed =>
        {
            var m = _measurement;
            _measurement = null;
            if (completed && m is not null) _session.Add(m.Result);
            m?.Dispose();
            UpdateResults();
            Activate();
            if (completed) SelectNextPending(_plan.IndexOf(step) + 1);
            _refresh.Start();
            Refresh(true);
        };
        _measurement.Start();
    }

    private void SkipStep()
    {
        var step = Selected;
        if (step is null) return;
        _session.Skip(step.Number);
        UpdateResults();
        SelectNextPending(_plan.IndexOf(step) + 1);
    }

    private void UpdateResults()
    {
        foreach (ListViewItem item in _list.Items)
        {
            var step = (Step)item.Tag!;
            var result = _session.Steps.FirstOrDefault(r => r.Step.Number == step.Number);
            item.SubItems[2].Text = result is not null
                ? (result.Targets.Count > 0 && result.Targets.All(t => t.Error <= Session.MaxError) ? "pass" : "FAIL")
                : _session.Skipped.Contains(step.Number) ? "skipped" : "";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Stop();
        _measurement?.Stop();
        foreach (var device in _changedDevices) Displays.Restore(device);
        base.OnFormClosed(e);
    }
}
