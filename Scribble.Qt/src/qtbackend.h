#pragma once

#include "penapi.h"

/// Reads and sets Qt's Windows tablet backend at runtime.
///
/// Qt has no public API for this. The only route is
/// `QNativeInterface::Private::QWindowsApplication`, reached by casting the platform
/// integration, which needs Qt's private headers. Those headers are why this is a separate
/// translation unit: nothing else in the sample should depend on them.
///
/// **This replaces a mechanism that never worked.** The sample previously passed
/// `-platform windows:nowmpointer` to select WinTab. Qt 6.8.3's Windows plugin parses no such
/// option -- its list is `fontengine`, `dialogs`, `altgr`, `gl`, `nodirectwrite`,
/// `nocolorfonts`, `nomousefromtouch`, `verbose`, `tabletabsoluterange`, `dpiawareness`,
/// `menus`, `reverse`, `darkmode` -- so Qt printed `Unknown option "nowmpointer"` to the
/// message handler, which this sample never installed, and started on WM_POINTER regardless of
/// what was asked for. Every `--wintab` run before 13 Sep 2026 was a WM_POINTER run wearing the
/// wrong label.
namespace qtbackend {

/// Whether the native interface could be reached at all. False on a non-Windows platform
/// plugin, or if Qt changes the interface.
bool available();

/// The backend Qt is using now. `WmPointer` when the interface is unavailable, which is Qt's
/// default on Windows -- but callers should check `available()` rather than treat that as an
/// answer.
PenApi current();

/// Switches the backend. Returns true only when Qt both accepted the change and reports the
/// requested backend afterwards, so a caller never has to trust the setter alone.
///
/// Qt 6.8.3 accepts this after the event loop is running, which contradicts the widely repeated
/// claim -- including in this repository's own notes until today -- that the choice is fixed at
/// process startup.
///
/// **Unverified: whether pen events actually arrive on the new backend after a switch.** The
/// toggle is proven; delivery across it is not, and needs a tablet. Some drivers suppress
/// WM_POINTER once a WinTab context has been opened, which no amount of querying Qt will
/// reveal.
bool select(PenApi api);

} // namespace qtbackend
