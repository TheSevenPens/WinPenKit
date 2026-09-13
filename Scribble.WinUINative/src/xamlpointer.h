#pragma once

#include "pch.h"
#include "pen_session.h"

#include <deque>
#include <mutex>

namespace scribble {

/// Pen input taken from WinUI's own pointer events, shaped as `PenPoint` so that everything
/// downstream is identical to the native path.
///
/// **Why this exists rather than a WM_POINTER session.**
///
/// `Scribble.Win32` hands its own HWND to `pen_session_start` and the native WM_POINTER
/// session subclasses it. That does not work on WinUI 3. Measured here: a session opened
/// against the top-level window drains **zero** points, and so does one opened against either
/// child window WinUI creates -- `Microsoft.UI.Content.DesktopChildSiteBridge`, which covers
/// the whole content area, and `InputSiteWindowClass`. The session reports itself running, the
/// max pressure reads correctly, and nothing anywhere reports an error. WinUI consumes pointer
/// input through its own input site before a subclass on any of those windows sees it.
///
/// The managed `Scribble.WinUI` has the same shape for the same reason: its pointer backend is
/// `WinUiPointerSession`, which attaches to XAML events, not the WM_POINTER session. Wintab is
/// unaffected either way, because a Wintab context runs on its own hidden pump window and only
/// uses the application window to bound the capture region.
///
/// So on WinUI, "pointer" means the framework's events. That is a ceiling of the framework
/// rather than a choice made here, and it is worth knowing before planning a native WinUI
/// application around the WM_POINTER path.
class XamlPointerSource {
public:
    /// Starts listening. `element` is the canvas host; `scale` is read live so a move to a
    /// display with a different scale is followed.
    void attach(winrt::Microsoft::UI::Xaml::UIElement const& element, HWND hwnd);
    void detach();

    bool attached() const { return element_ != nullptr; }

    /// Drains queued points. Same contract as `pen_session_drain_points`.
    int drain(PenPoint* out, int max);

    /// WinUI normalises pressure to 0..1, so the raw field is reconstructed against this.
    /// 1024 matches what every pointer backend in WinPenKit declares -- it is the API's fixed
    /// range, not the device's, and `MaxPressure` documents that distinction.
    static constexpr int kMaxPressure = 1024;

private:
    void onPointer(winrt::Microsoft::UI::Xaml::Input::PointerRoutedEventArgs const& e,
                   bool endsStroke);

    winrt::Microsoft::UI::Xaml::UIElement element_{ nullptr };
    HWND hwnd_ = nullptr;

    winrt::event_token moved_{}, pressed_{}, released_{}, exited_{};

    std::mutex mutex_;
    std::deque<PenPoint> points_;
};

} // namespace scribble
