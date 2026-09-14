using WinPenKit.Diagnostics;

namespace WintabContexts;

/// <summary>
/// One number, large enough to read from across a room, and what it takes to change it.
/// </summary>
/// <remarks>
/// <para>
/// Built to be filmed. The context count is the whole point, so it is set in a size nothing else
/// competes with, and everything that qualifies it sits underneath in one column. A screen
/// recording of contexts leaking has to make the number the thing the eye goes to.
/// </para>
/// <para>
/// The count starts blank rather than at a value. Until Refresh is pressed nothing has been asked,
/// and a figure on screen that nobody asked for is a figure whose age is unknown.
/// </para>
/// </remarks>
internal sealed class MainForm : Form
{
    private readonly Label _count = new();
    private readonly Label _countCaption = new();
    private readonly Label _detail = new();
    private readonly Label _status = new();
    private readonly Button _refresh = new();
    private readonly Button _reset = new();
    private readonly CheckBox _auto = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    /// <summary>What stands where the number goes before anything has been asked.</summary>
    private const string Nothing = "--";

    /// <summary>
    /// Said under the number, always, and never rephrased.
    /// </summary>
    /// <remarks>
    /// It used to gain "(the driver claims a maximum of 32)" once a number had been read, which
    /// widened the window on the first press of Refresh. A window that changes size while it is
    /// being filmed is a distraction from the number it exists to show, so the maximum is a line
    /// in the detail block with the driver's other facts, where it belongs anyway.
    /// </remarks>
    private const string CountCaption = "contexts open";

    public MainForm()
    {
        // Sizes are written at 96 dpi and scaled by hand. A form built in code never gets the
        // AutoScaleDimensions a designer file would set, so WinForms has no baseline to scale
        // from: the fonts, being in points, came out large on a 225% display while every size in
        // pixels stayed literal, and the window was less than half the size its contents needed.
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10f);

        Text = "Wintab contexts";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Scaled(20), Scaled(12), Scaled(20), Scaled(12)),
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        // Every row sized to its contents, and the window sized to the rows. There used to be a
        // percentage row absorbing the slack, which put a band of nothing between the detail and
        // the buttons -- fine on a large display and wasteful on a 1920 by 1080 one, where a
        // diagnostic sitting beside the thing it is diagnosing wants to be small.
        for (int row = 0; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Nothing else on the form is allowed to be this size. Anchored to nothing, which is how a
        // TableLayoutPanel centres a control that sizes itself.
        //
        // Dashes rather than nothing, so that the place a number goes is visibly a place a number
        // goes. It also stops the row changing height the first time one arrives, which an empty
        // label would have done.
        _count.Text = Nothing;
        _count.Font = new Font("Segoe UI", 46f, FontStyle.Bold);
        _count.AutoSize = true;
        _count.Anchor = AnchorStyles.None;
        _count.Margin = new Padding(0);
        _count.TextAlign = ContentAlignment.MiddleCenter;

        _countCaption.Text = CountCaption;
        _countCaption.AutoSize = true;
        _countCaption.Anchor = AnchorStyles.None;
        _countCaption.ForeColor = SystemColors.GrayText;
        _countCaption.Margin = new Padding(0, 2, 0, 0);

        _detail.Font = new Font("Consolas", 10f);
        _detail.AutoSize = true;
        _detail.Dock = DockStyle.Top;
        _detail.Margin = new Padding(0, Scaled(12), 0, Scaled(10));

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        _refresh.Text = "&Refresh";
        _refresh.AutoSize = true;
        _refresh.Padding = new Padding(18, 6, 18, 6);
        _refresh.Click += (_, _) => Reread(byHand: true);

        _auto.Text = "every second";
        _auto.AutoSize = true;
        _auto.Anchor = AnchorStyles.Left;
        _auto.Margin = new Padding(14, 0, 0, 0);
        _auto.CheckedChanged += (_, _) => _timer.Enabled = _auto.Checked;

        buttons.Controls.Add(_refresh);
        buttons.Controls.Add(_auto);

        _reset.Text = "Restart the Wacom driver";
        _reset.AutoSize = true;
        _reset.Padding = new Padding(18, 6, 18, 6);
        _reset.Margin = new Padding(0, Scaled(8), 0, 0);
        _reset.Click += (_, _) => ResetDriver();

        _status.AutoSize = true;
        _status.Dock = DockStyle.Top;
        _status.ForeColor = SystemColors.GrayText;
        _status.Margin = new Padding(0, Scaled(10), 0, 0);
        // Narrow enough to wrap inside the width the detail block already takes, so a long
        // sentence makes the window taller rather than wider.
        _status.MaximumSize = new Size(Scaled(330), 0);

        layout.Controls.Add(_count);
        layout.Controls.Add(_countCaption);
        layout.Controls.Add(_detail);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_reset);
        layout.Controls.Add(_status);
        Controls.Add(layout);

        // Sized to what the layout asked for, rather than to a guess that then had to be padded.
        // GrowAndShrink on the form as well, so a longer status line grows the window instead of
        // being cut off by it.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _timer.Interval = 1000;
        _timer.Tick += (_, _) => Reread(byHand: false);

        DescribeDriver();
        DescribeResetButton();
    }

    /// <summary>A length written at 96 dpi, in the pixels this display actually uses.</summary>
    private int Scaled(int atNinetySix) => (int)Math.Round(atNinetySix * DeviceDpi / 96.0);

    /// <summary>Ask the driver, and show what it said.</summary>
    private void Reread(bool byHand)
    {
        if (!Wintab.IsPresent)
        {
            _count.Text = Nothing;
            _countCaption.Text = "no Wintab on this machine";
            _status.Text = "The tablet driver is not installed, or does not provide Wintab.";
            return;
        }

        if (WintabDiagnostics.ContextTable() is not { } table)
        {
            _count.Text = Nothing;
            _countCaption.Text = "the driver would not say how many";
            return;
        }

        _count.Text = table.Open.ToString();
        _countCaption.Text = CountCaption;

        DescribeDriver(table.Maximum);

        if (byHand) _status.Text = $"Read at {DateTime.Now:HH:mm:ss}.";
    }

    /// <summary>Everything known about whose driver is answering.</summary>
    /// <remarks>
    /// The vendor line is the one that matters for the question this tool exists to ask. Every
    /// vendor's implementation is installed as the same filename, so the company recorded in that
    /// file is what says whose contexts are being counted.
    /// </remarks>
    private void DescribeDriver(uint? maximum = null)
    {
        if (!Wintab.IsPresent)
        {
            _detail.Text = "";
            return;
        }

        var dll = Wintab.Library;

        _detail.Text = string.Join(Environment.NewLine,
            $"system contexts  {Wintab.SystemContexts}",
            $"driver maximum   {maximum?.ToString() ?? "not read yet"}",
            "",
            $"vendor           {dll?.CompanyName ?? "not reported"}",
            $"wintab32.dll     {dll?.FileVersion ?? "not reported"}",
            $"implementation   {Wintab.Implementation}",
            $"spec / impl      {Wintab.SpecVersion} / {Wintab.ImplVersion}",
            $"devices          {Wintab.Devices}");
    }

    /// <summary>Only offer the reset where it is known to be the right one.</summary>
    private void DescribeResetButton()
    {
        if (TabletService.WacomServiceExists) return;

        _reset.Enabled = false;
        _reset.Text = "Restart the Wacom driver  (not installed)";
        _status.Text = "No Wacom service on this machine. Other vendors' service names have not "
                     + "been tested, so nothing is offered for them rather than guessing.";
    }

    private void ResetDriver()
    {
        _status.Text = "Asking for administrator rights...";
        _reset.Enabled = false;
        Application.DoEvents();

        string? failure = TabletService.Restart();

        _reset.Enabled = true;

        if (failure is not null)
        {
            _status.Text = failure;
            return;
        }

        // Read it straight away: watching the number fall is the point of the button.
        Reread(byHand: false);
        _status.Text = $"Driver restarted at {DateTime.Now:HH:mm:ss}. Applications that were "
                     + "already running have lost their contexts and may need restarting.";
    }
}
