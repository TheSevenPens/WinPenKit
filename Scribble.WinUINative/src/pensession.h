#pragma once

#include "pch.h"
#include "pen_session.h"

#include <string>

namespace scribble {

/// What the ribbon shows about the most recent pen point.
///
/// Assembled here and handed over whole, the same shape `Scribble.Qt` uses, so the ribbon holds
/// no pen state of its own and one place decides what each field means.
struct PenReadout {
    bool hasData = false;
    bool inProximity = false;

    double screenX = 0, screenY = 0;   ///< Physical desktop pixels.
    double appX = 0, appY = 0;         ///< Physical pixels from the window's client origin.
    double canvasX = 0, canvasY = 0;   ///< Physical pixels from the canvas origin.

    int  rawX = 0, rawY = 0;           ///< Units named by `rawUnits`.
    PenRawUnits rawUnits = PEN_RAW_NONE;

    uint32_t rawPressure = 0;
    int      maxPressure = 1;

    double azimuth = 0, altitude = 0, twist = 0;
    double tiltX = 0, tiltY = 0;

    bool tip = false, eraser = false, barrel1 = false, barrel2 = false, barrel3 = false;
    uint32_t rawButtons = 0;

    uint32_t cursor = 0;
};

/// The window WM_POINTER messages actually arrive at, for a WinUI 3 top-level window.
///
/// A Win32 app hands its own HWND to pen_session_start and WM_POINTERUPDATE arrives there.
/// WinUI 3 does not work that way: the top-level window hosts the XAML content in a child
/// `Microsoft.UI.Content.DesktopChildSiteBridge`, and that child is the window under the pen,
/// so it is the one the messages are delivered to. Subclassing the top-level window instead
/// drains zero points, with the session reporting itself running and no error anywhere.
///
/// Returns the top-level window unchanged when no such child exists, so this is safe to call
/// on any HWND.
HWND inputTargetWindow(HWND topLevel);

/// Owns a WinPenKit session over the C ABI, and decodes the parts whose meaning depends on the
/// backend so that nothing downstream has to.
///
/// The same ABI `Scribble.Win32` uses. There is no managed binding here and no WinPenKit
/// header beyond `pen_session.h`.
class PenSession {
public:
    ~PenSession() { stop(); }

    PenSession(const PenSession&) = delete;
    PenSession& operator=(const PenSession&) = delete;
    PenSession() = default;

    /// Opens a session for `api` and attaches it to `hwnd`.
    ///
    /// `hwnd` is required by WM_POINTER, which subclasses it, and optional for Wintab, where it
    /// sets the capture region. Returns an error string on failure, or an empty string on
    /// success -- the C API returns a static message, so there is nothing to free.
    std::string start(PenInputApi api, HWND hwnd);
    void stop();

    bool running() const { return handle_ != nullptr; }

    PenInputApi api() const { return api_; }
    int maxPressure() const { return maxPressure_; }
    const PenConventions& conventions() const { return conventions_; }

    /// The name to show for the running API. Taken from the library rather than spelled here,
    /// so this sample cannot drift from the others over how an API is named.
    const char* apiLabel() const;

    /// Drains up to `max` points. Returns how many were written.
    int drain(PenPoint* out, int max);

    /// Folds one point into the button state and fills a readout. Button encodings differ per
    /// backend and this is the only place that knows it.
    void applyButtons(const PenPoint& pt);

    bool tipDown() const { return tip_; }
    bool barrel1() const { return barrel1_; }
    bool barrel2() const { return barrel2_; }
    bool barrel3() const { return barrel3_; }
    uint32_t lastRawButtons() const { return lastRawButtons_; }

    /// Tells the session the window was activated. Wintab needs this badly: contexts sit in an
    /// overlap order and nothing puts yours back on top after another application takes focus,
    /// so the first stroke after returning is silently swallowed.
    void onActivated() { if (handle_) pen_session_on_activated(handle_); }

private:
    PenSessionHandle handle_ = nullptr;
    PenInputApi api_ = PEN_API_WM_POINTER;
    // Value-initialized rather than listing the members. PenConventions gains a field when
    // the timestamp work lands, and a brace list that names every member is a compile error on
    // one side of that merge and silently wrong on the other. Zero is PEN_RAW_NONE and the
    // Wintab encodings either way, which is what an unstarted session should report.
    PenConventions conventions_{};
    int maxPressure_ = 1;

    bool tip_ = false, barrel1_ = false, barrel2_ = false, barrel3_ = false;
    uint32_t lastRawButtons_ = 0;
};

} // namespace scribble
