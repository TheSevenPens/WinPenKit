#include "qtbackend.h"

#include <QGuiApplication>
#include <QtGui/private/qguiapplication_p.h>
#include <qpa/qplatformintegration.h>

namespace {

using WindowsApplication = QNativeInterface::Private::QWindowsApplication;

/// Null when the platform integration is not the Windows one, or when Qt has changed the
/// interface out from under this. Looked up per call rather than cached: the cast is cheap and
/// a cached pointer would outlive a platform integration this sample does not own.
WindowsApplication* native() {
    return dynamic_cast<WindowsApplication*>(QGuiApplicationPrivate::platformIntegration());
}

} // namespace

namespace qtbackend {

bool available() {
    return native() != nullptr;
}

PenApi current() {
    auto* app = native();
    if (!app) return PenApi::WmPointer;
    return app->isWinTabEnabled() ? PenApi::WinTab : PenApi::WmPointer;
}

bool select(PenApi api) {
    auto* app = native();
    if (!app) return false;

    const bool want = api == PenApi::WinTab;

    // The return value is not enough on its own. Ask Qt what it ended up with, and report
    // success only if that matches -- a setter that returns true while the query disagrees is
    // exactly the shape of fault that let `nowmpointer` go unnoticed for a day.
    app->setWinTabEnabled(want);
    return app->isWinTabEnabled() == want;
}

} // namespace qtbackend
