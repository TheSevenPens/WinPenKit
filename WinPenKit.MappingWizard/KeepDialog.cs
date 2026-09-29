namespace WinPenKit.MappingWizard;

/// <summary>
/// "Keep this resolution?" with a countdown that reverts on its own, as Windows' own display
/// settings do. A resolution a monitor cannot show leaves nothing on screen to click, so the
/// safe answer has to be the one that happens when nobody answers.
/// </summary>
internal sealed class KeepDialog : Form
{
    private const int Seconds = 15;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _label = new() { AutoSize = false, Dock = DockStyle.Top, Height = 70, Padding = new Padding(12) };
    private int _left = Seconds;

    public KeepDialog(string what)
    {
        Text = "Keep this resolution?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 130);

        var keep = new Button { Text = "Keep", DialogResult = DialogResult.OK, Width = 100, Left = 196, Top = 84 };
        var revert = new Button { Text = "Revert", DialogResult = DialogResult.Cancel, Width = 100, Left = 304, Top = 84 };
        Controls.AddRange([_label, keep, revert]);
        AcceptButton = keep;
        CancelButton = revert;

        void Update() => _label.Text = $"{what}\nReverting in {_left} seconds unless you keep it.";
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
