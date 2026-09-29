namespace WinPenKit.MappingWizard;

/// <summary>
/// "Keep these changes?" with a countdown that reverts on its own, as Windows' own display
/// settings do. A resolution a monitor cannot show leaves nothing on screen to click, so the
/// safe answer has to be the one that happens when nobody answers.
/// </summary>
/// <remarks>
/// Laid out by panels that size to their contents rather than at fixed positions. The first
/// version placed its buttons by pixel, which at 250% scaling put them mostly below the bottom
/// edge of the dialog -- and it is shown right after the scaling may have changed, so no fixed
/// layout can be right.
/// </remarks>
internal sealed class KeepDialog : Form
{
    private const int Seconds = 15;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _left = Seconds;

    public KeepDialog(string what)
    {
        Text = "Keep these changes?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        Font = new Font("Segoe UI", 10);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int pad = 12 * DeviceDpi / 96;
        var label = new Label { AutoSize = true, MaximumSize = new Size(480 * DeviceDpi / 96, 0), Margin = new Padding(pad) };
        var keep = new Button { Text = "Keep", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(pad / 2, 0, pad / 2, 0) };
        var revert = new Button { Text = "Revert", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(pad / 2, 0, pad / 2, 0) };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
            Margin = new Padding(pad, 0, pad, pad),
        };
        buttons.Controls.AddRange([revert, keep]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill };
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);

        AcceptButton = keep;
        CancelButton = revert;

        void Update() => label.Text = $"{what}\n\nReverting in {_left} seconds unless you keep it.";
        Update();
        _timer.Tick += (_, _) =>
        {
            if (--_left <= 0) { _timer.Stop(); DialogResult = DialogResult.Cancel; }
            else Update();
        };
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
