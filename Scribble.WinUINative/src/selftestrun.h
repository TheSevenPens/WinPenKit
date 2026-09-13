#pragma once

#include "pch.h"
#include "canvas.h"
#include "pensession.h"

#include <functional>
#include <string>

namespace scribble {

/// Runs the shared acceptance checks against a live window.
///
/// The same `selftest.h` that `Scribble.Win32` and `Scribble.Qt` use, unmodified. The check
/// ids, their meanings and the report format have to match or the comparison these samples
/// exist for is not a comparison.
///
/// `replayPath` empty runs the levels that need no pen data.
///
/// Returns the process exit code: 0 when every check passed.
/// `runOnUi` marshals a callable onto the UI thread and waits for it.
///
/// The checks do not run on the UI thread, and that is not a detail. The presentation probe
/// draws, then waits for the drawing to reach the screen before capturing. Anything that
/// occupies the UI thread while it waits stops WinUI presenting the very frame being waited
/// for -- run from inside a DispatcherTimer tick, the probe reported "neither marker reached
/// the screen" every time, because none had. Pumping messages from inside that callback does
/// not help: the compositor commit happens when the callback returns.
///
/// So the checks run on their own thread and reach back for anything that touches XAML. The
/// capture itself is GDI against the screen and needs no thread affinity.
int runSelfTest(Canvas& canvas, HWND hwnd, PenSession& pen, bool xamlPointerBackend,
                PenInputApi requested, const std::string& replayPath,
                const std::function<void(std::function<void()>)>& runOnUi);

/// Which backend the sample should open, remembered between runs.
///
/// Stored in `HKCU\Software\TheSevenPens\Scribble.WinUINative`, the same place and shape
/// `Scribble.Qt` uses, so someone moving between the samples finds the setting where they
/// left it.
///
/// Unlike Qt, switching here takes effect immediately -- a WinPenKit session is opened and
/// closed at will and WinUI's own pointer events are just handlers -- so this is a
/// convenience rather than the only way to choose, and no restart notice is needed.
namespace settings {
    std::optional<PenInputApi> savedApi();
    void saveApi(PenInputApi api);
}

} // namespace scribble
