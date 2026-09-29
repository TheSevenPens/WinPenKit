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

    // Every monitor's scaling when the wizard opened, put back when it closes if the wizard
    // changed any. Read at the start because scaling is stored relative to the resolution: a
    // value read later, under a resolution the wizard set, would mean something else once the
    // resolution is put back.
    private readonly Dictionary<string, ScalingState>? _startScaling = Scaling.Read();
    private bool _scalingChanged;
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

        _footer.Text = $"Results: {_session.Folder}\nWhen it closes, the wizard offers to put back any resolution or scaling it changed.";

        _list.SelectedIndexChanged += (_, _) => Refresh(false);
        _apply.Click += (_, _) => ApplySettings();
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
        var states = Scaling.Read();
        var scalingTarget = states is null ? null : Planner.ScalingFor(step, _monitors, states);
        string scalingNote = step.Scaling == ScalingSetup.AsIs
            ? "leave it as it is"
            : scalingTarget is not null
                ? string.Join(", ", _monitors.Where(m => scalingTarget.ContainsKey(m.Device))
                                             .Select(m => $"monitor {m.Number} at {scalingTarget[m.Device]}%")) +
                  ". The button below sets this too, after the resolution"
                : $"{Step.ScalingText(step.Scaling)}, for every monitor.  (Settings > System > Display > Scale.)  " +
                  "Do this after the resolution: changing a monitor's resolution also changes its scaling";
        _instructions.Text =
            $"Step {step.Number} of {_plan.Count}\n\n" +
            $"1.  Resolution: monitor {step.ResolutionOn} at {step.Resolution} ({step.ResolutionLabel}).\n\n" +
            $"2.  Scaling: {scalingNote}.\n\n" +
            $"3.  Tablet: in the tablet driver's settings, {mapping}.\n\n" +
            "Then press Measure. Targets appear on the monitors the pen should reach.";

        var unmet = Planner.Unmet(step, _monitors);
        bool scalingWrong = scalingTarget is not null
            ? _monitors.Any(m => scalingTarget.TryGetValue(m.Device, out int p) && m.ScalePercent != p)
            : unmet.Any(u => u.Contains("scaling"));
        var lines = new List<string>();
        lines.Add(unmet.Any(u => u.Contains("needs")) || target is null ? "✗  resolution" : "✓  resolution");
        lines.Add(scalingWrong ? "✗  scaling" : "✓  scaling");
        lines.Add("?  tablet mapping: cannot be read; if it is wrong, the targets will be out of the pen's reach");
        lines.AddRange(unmet);
        if (Planner.StaleSystemDpi(_monitors) is { } stale) lines.Add("Note: " + stale);
        _status.Text = string.Join("\n", lines);

        bool needsResolution = target is not null &&
            (target.Current.Width != step.Resolution.Width || target.Current.Height != step.Resolution.Height);
        bool needsScaling = scalingTarget is not null && scalingWrong;
        _apply.Visible = needsResolution || needsScaling;
        _apply.Text = (needsResolution, needsScaling) switch
        {
            (true, true) => "Set resolution and scaling",
            (true, false) => $"Set monitor {step.ResolutionOn} to {step.Resolution}",
            _ => "Set scaling",
        };
        _measure.Enabled = _skip.Enabled = true;
    }

    private static string Where(Monitor m) => m.Primary ? "the primary" : $"at {m.Bounds.X},{m.Bounds.Y}";

    /// <summary>
    /// Sets what the wizard can set for the selected step -- resolution, then scaling -- and asks
    /// to keep the result. Resolution first: it moves scaling, so the other order undoes itself.
    /// </summary>
    private void ApplySettings()
    {
        var step = Selected;
        var target = step is null ? null : _monitors.FirstOrDefault(m => m.Number == step.ResolutionOn);
        if (step is null || target is null) return;

        var changes = new List<string>();
        var errors = new List<string>();

        // What to go back to if the person doesn't keep this, read before anything changes.
        var scalingBefore = Scaling.Read();
        bool resolutionChanged = false;

        if (target.Current.Width != step.Resolution.Width || target.Current.Height != step.Resolution.Height)
        {
            if (Displays.SetResolution(target.Device, step.Resolution) is { } error) errors.Add(error);
            else
            {
                resolutionChanged = true;
                _changedDevices.Add(target.Device);
                changes.Add($"monitor {target.Number} at {step.Resolution}");
            }
        }

        // A resolution change settles over a moment; the scaling it moved has to be read after.
        if (resolutionChanged) Thread.Sleep(750);
        _monitors = Displays.Read(_session.Monitors);
        if (Scaling.Read() is { } states && Planner.ScalingFor(step, _monitors, states) is { } wanted)
        {
            foreach (var m in _monitors)
            {
                if (!wanted.TryGetValue(m.Device, out int percent) || m.ScalePercent == percent) continue;
                _scalingChanged = true;
                if (Scaling.Set(m.Device, percent) is { } error) errors.Add($"Monitor {m.Number}: {error}");
                else changes.Add($"monitor {m.Number} at {percent}% scaling");
            }
        }

        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "Not everything was changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (changes.Count > 0)
        {
            using var keep = new KeepDialog("Now: " + string.Join(", ", changes) + ".");
            if (keep.ShowDialog(this) != DialogResult.OK)
            {
                if (resolutionChanged) Displays.Restore(target.Device);
                if (resolutionChanged) Thread.Sleep(750);
                if (scalingBefore is not null)
                    foreach (var before in scalingBefore.Values) Scaling.Restore(before);
            }
        }

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

    /// <summary>
    /// Asks before putting anything back. Closing is not always the end: a person changing the
    /// primary monitor's scaling closes the wizard to sign out, and putting the scaling back then
    /// would undo the very change they are signing out for.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refresh.Stop();
        _measurement?.Stop();

        if (_changedDevices.Count > 0 || _scalingChanged)
        {
            var answer = MessageBox.Show(this,
                "Put the resolution and scaling back to how they were when the wizard opened?\n\n" +
                "Choose No if you are closing to sign out and carry on with this step afterwards.",
                "WinPenKit mapping wizard", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                _refresh.Start();
                return;
            }
            if (answer == DialogResult.Yes)
            {
                // Resolution first, then scaling, which is stored relative to it.
                foreach (var device in _changedDevices) Displays.Restore(device);
                if (_scalingChanged && _startScaling is not null)
                {
                    if (_changedDevices.Count > 0) Thread.Sleep(750);
                    foreach (var start in _startScaling.Values) Scaling.Restore(start);
                }
            }
        }
        base.OnFormClosing(e);
    }
}
