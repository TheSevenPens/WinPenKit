#pragma once

#include <QSettings>
#include <QString>

/// Which pen input path Qt was started with.
///
/// Qt settles this while the Windows platform plugin initialises, before QApplication is
/// running, so it cannot change while the process lives and the application cannot ask
/// afterwards which one it got. Everything about the switcher follows from that: the choice is
/// saved, and it takes effect on the next launch. Krita works the same way and says so.
enum class PenApi {
    WmPointer,   ///< Qt's default on Windows.
    WinTab,      ///< Selected with -platform windows:nowmpointer. Always tablet-native.
};

namespace penapi {

/// The name shown in the dropdown and in a recording's header.
inline QString label(PenApi api) {
    return api == PenApi::WinTab ? QStringLiteral("Wintab (high-res)")
                                 : QStringLiteral("WM_Pointer");
}

/// A longer form for the restart notice, where the short name is not enough on its own.
inline QString description(PenApi api) {
    return api == PenApi::WinTab
        ? QStringLiteral("Wintab, tablet-native")
        : QStringLiteral("WM_Pointer");
}

/// Where the choice is kept between runs.
///
/// Organization and application are given explicitly so this works before QApplication exists
/// -- which it must, because the answer decides what arguments QApplication is constructed
/// with.
inline QSettings store() {
    return QSettings(QStringLiteral("TheSevenPens"), QStringLiteral("Scribble.Qt"));
}

/// The API to start with. Defaults to WM_Pointer, which is Qt's own default on Windows.
inline PenApi load() {
    QSettings s = store();
    return s.value(QStringLiteral("penApi")).toString() == QLatin1String("wintab")
        ? PenApi::WinTab
        : PenApi::WmPointer;
}

/// Remembers the choice for the next launch. It does not affect the running process.
inline void save(PenApi api) {
    QSettings s = store();
    s.setValue(QStringLiteral("penApi"),
               api == PenApi::WinTab ? QStringLiteral("wintab") : QStringLiteral("pointer"));
    s.sync();
}

} // namespace penapi
