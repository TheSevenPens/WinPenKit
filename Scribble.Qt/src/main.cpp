// Scribble.Qt - the same drawing application as the other six samples, built on Qt's own
// stylus handling instead of WinPenKit.
//
// It exists to be compared against them. Six samples sharing one library can agree with each
// other and still be wrong together; a seventh built on an independent stack is what turns
// "our six agree" into a statement about Windows. Krita reaches pen input the same way, so
// this is also the closest thing in the repository to what Krita actually sees.
//
// What Qt settles for the application, and cannot be changed afterwards:
//
//   * The backend is chosen during platform plugin initialisation, before QApplication is
//     running, so it is fixed for the life of the process. The ribbon's dropdown therefore
//     saves the choice and asks for a restart rather than switching. Krita's UI does the same
//     thing for the same reason. --wintab and --pointer override the saved choice for one
//     run without changing it.
//   * Qt's WinTab context is always tablet-native resolution. There is no low-resolution
//     option, so --wintab here is comparable to WinPenKit's WintabDigitizer and never to its
//     WintabSystem.
//   * Qt reports position as a fractional QPointF in device independent pixels and pressure
//     normalised to 0..1. Both are converted at the edge of this application rather than
//     carried inward, so everything below the event handler is in the same units the other
//     samples use.

#include <QApplication>
#include <QGuiApplication>
#include <QTimer>

#include <string>
#include <vector>

#include "penapi.h"
#include "scribblewindow.h"

// After Qt, deliberately: see the note in scribblewindow.cpp.
// Qt defines `emit` as an empty macro so that `emit signalName()` reads as syntax. The self
// test report has a method called emit(), which that macro turns into `r.()`. Undefining it
// here rather than building with QT_NO_KEYWORDS keeps `signals:` working in the header; the
// cost is that this file says Q_EMIT where it would otherwise say emit.
#undef emit

#include "selftest.h"

namespace {

bool hasFlag(int argc, char** argv, const char* flag) {
    for (int i = 1; i < argc; i++)
        if (_stricmp(argv[i], flag) == 0) return true;
    return false;
}

} // namespace

int main(int argc, char** argv) {
    // The saved choice, then the command line on top of it. A flag is for one run and does not
    // overwrite what the dropdown last stored -- otherwise a single --wintab run would silently
    // change what the next plain launch does.
    PenApi api = penapi::load();
    if (hasFlag(argc, argv, "--wintab"))  api = PenApi::WinTab;
    if (hasFlag(argc, argv, "--pointer")) api = PenApi::WmPointer;
    const bool wantWinTab = api == PenApi::WinTab;

    // Qt reads -platform out of argv inside the QApplication constructor, so the choice has to
    // be made here, in a copy of argv, before that constructor runs. There is no later moment:
    // the Windows platform plugin decides between WM_POINTER and WinTab while it initialises.
    std::vector<char*> args(argv, argv + argc);
    char platformFlag[] = "-platform";
    char platformValue[] = "windows:nowmpointer";
    if (wantWinTab) {
        args.push_back(platformFlag);
        args.push_back(platformValue);
    }
    int qtArgc = static_cast<int>(args.size());

    // Without this Qt rounds the scale factor to a whole number, so a 225% display reports a
    // device pixel ratio of 2 and every surface measurement is 11% out while looking tidy.
    // PassThrough is Qt 6's default; setting it explicitly means a future change of default
    // cannot quietly move the numbers this sample reports.
    QGuiApplication::setHighDpiScaleFactorRoundingPolicy(
        Qt::HighDpiScaleFactorRoundingPolicy::PassThrough);

    QApplication app(qtArgc, args.data());

    ScribbleWindow window(api);

    std::string recordPath;
    if (selftest::record_requested(recordPath))
        window.canvas()->recordTo(recordPath);

    // The window manager cascades each launch a little further down, so an application that
    // fits on one run hangs below the work area a few runs later - and pen input aimed at the
    // part hanging off is discarded with no error. Before showing, not after.
    window.show();
    selftest::clamp_to_work_area(reinterpret_cast<HWND>(window.winId()));

    std::string replayPath;
    const bool replay = selftest::replay_requested(replayPath);
    if (selftest::requested() || replay) {
        int code = 0;
        // Queued rather than run inline: every level 1 check is about the drawing surface, and
        // the surface does not exist until the first resize has been delivered.
        QTimer::singleShot(0, &window, [&] {
            code = window.runSelfTest(replay ? replayPath : std::string());
            QCoreApplication::exit(code);
        });
        app.exec();
        return code;
    }

    const int code = app.exec();
    window.saveRecording();
    return code;
}
