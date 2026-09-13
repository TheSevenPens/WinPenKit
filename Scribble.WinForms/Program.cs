using WinPenKit.Diagnostics;

namespace Scribble.WinForms;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var form = new MainForm();

        if (StrokeRecorder.Requested(args, out string? recordPath))
            form.RecordTo(recordPath!);

        bool replay = StrokeReplay.Requested(args, out string? replayPath);
        if (!SelfTest.Requested(args) && !replay)
        {
            Application.Run(form);
            return 0;
        }

        // The form has to be shown: every level 1 check is about the drawing surface, and the
        // surface does not exist until the first layout pass.
        int code = 0;
        form.Shown += async (_, _) =>
        {
            var report = await form.RunSelfTest(replay ? replayPath : null);
            code = report.Emit();
            form.Close();
        };
        Application.Run(form);
        return code;
    }
}
