#include "pch.h"
#include "xamlpointer.h"

#include <cmath>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Input;
using namespace winrt::Windows::Foundation;

namespace scribble {

void XamlPointerSource::attach(UIElement const& element, HWND hwnd) {
    detach();
    element_ = element;
    hwnd_ = hwnd;

    auto draw = [this](IInspectable const&, PointerRoutedEventArgs const& e) { onPointer(e, false); };

    moved_    = element_.PointerMoved(draw);
    pressed_  = element_.PointerPressed(draw);
    released_ = element_.PointerReleased(draw);

    // Leaving the element ends the stroke. Without this a pen that exits one edge and re-enters
    // at another draws a straight line across the canvas that the pen never travelled -- the
    // caller breaks a stroke on an out-of-bounds point, but a pointer that leaves and returns
    // never produces one.
    //
    // A separate handler rather than a flag read off the args: PointerRoutedEventArgs carries
    // no RoutedEvent in WinUI 3, so which event this is has to come from the subscription.
    exited_ = element_.PointerExited(
        [this](IInspectable const&, PointerRoutedEventArgs const& e) { onPointer(e, true); });
}

void XamlPointerSource::detach() {
    if (!element_) return;
    element_.PointerMoved(moved_);
    element_.PointerPressed(pressed_);
    element_.PointerReleased(released_);
    element_.PointerExited(exited_);
    element_ = nullptr;
    hwnd_ = nullptr;

    std::lock_guard lock(mutex_);
    points_.clear();
}

void XamlPointerSource::onPointer(PointerRoutedEventArgs const& e, bool endsStroke) {
    if (!element_) return;

    auto point = e.GetCurrentPoint(element_);
    if (point.PointerDeviceType() != winrt::Microsoft::UI::Input::PointerDeviceType::Pen)
        return;

    auto root = element_.as<FrameworkElement>().XamlRoot();
    const double scale = (root && root.RasterizationScale() > 0) ? root.RasterizationScale() : 1.0;

    // Element-relative logical units to desktop physical pixels. The client origin is a whole
    // pixel, so taking it as an integer costs nothing; only the pen's own position needs its
    // precision kept, which is why the multiply happens before the add and PointToScreen-style
    // helpers are avoided.
    POINT clientOrigin{ 0, 0 };
    if (hwnd_) ::ClientToScreen(hwnd_, &clientOrigin);

    const Point pos = point.Position();
    auto transform = element_.TransformToVisual(root ? root.Content().try_as<UIElement>() : nullptr);
    const Point inWindow = transform ? transform.TransformPoint(pos) : pos;

    auto props = point.Properties();

    PenPoint pt{};
    pt.desktop_x = clientOrigin.x + inWindow.X * scale;
    pt.desktop_y = clientOrigin.y + inWindow.Y * scale;

    // Zero, with the conventions reporting PEN_RAW_NONE. WinUI exposes no device-native
    // coordinate, and reporting desktop_x truncated would read as a second measurement while
    // being the first with its fraction removed.
    pt.raw_x = 0;
    pt.raw_y = 0;

    pt.pressure = static_cast<uint32_t>(std::lround(props.Pressure() * kMaxPressure));
    pt.tilt_x = props.XTilt();
    pt.tilt_y = props.YTilt();
    pt.twist  = props.Twist();

    // Planar to spherical, the same conversion the managed pointer backends do, so azimuth and
    // altitude mean the same thing on every sample.
    {
        const double tx = pt.tilt_x * 3.14159265358979323846 / 180.0;
        const double ty = pt.tilt_y * 3.14159265358979323846 / 180.0;
        const double mag = std::sqrt(pt.tilt_x * pt.tilt_x + pt.tilt_y * pt.tilt_y);
        pt.altitude = 90.0 - (mag > 90.0 ? 90.0 : mag);
        double az = std::atan2(-std::sin(tx), std::sin(ty)) * 180.0 / 3.14159265358979323846;
        if (az < 0) az += 360.0;
        pt.azimuth = (mag == 0.0) ? 0.0 : az;
    }

    pt.buttons = (props.IsBarrelButtonPressed() ? 0x0001u : 0u)
               | (props.IsEraser() ? 0x0002u : 0u);
    pt.cursor  = props.IsEraser() ? 14u : 13u;   // normalised, as the pointer backends report
    pt.source  = PEN_API_WINUI_POINTER;

    // A pointer that has left the element ends the stroke. Zero pressure is how every backend
    // says "not drawing", so it needs no special case downstream.
    if (endsStroke) pt.pressure = 0;

    std::lock_guard lock(mutex_);
    // Bounded, so a canvas that stops draining cannot grow this without limit. Dropping the
    // oldest keeps the newest, which is what a live stroke needs.
    if (points_.size() > 4096) points_.pop_front();
    points_.push_back(pt);
}

int XamlPointerSource::drain(PenPoint* out, int max) {
    if (!out || max <= 0) return 0;

    std::lock_guard lock(mutex_);
    int n = 0;
    while (n < max && !points_.empty()) {
        out[n++] = points_.front();
        points_.pop_front();
    }
    return n;
}

} // namespace scribble
