#include "pch.h"
#include "canvas.h"

#include <robuffer.h>
#include <winrt/Windows.Storage.Streams.h>

#include <cmath>
#include <cstring>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
using namespace winrt::Microsoft::UI::Xaml::Media::Imaging;
using namespace winrt::Windows::Foundation;

namespace {

/// The raw bytes behind a WriteableBitmap's PixelBuffer.
///
/// WinRT hands out an IBuffer, which has a Length and no pointer. IBufferByteAccess is the
/// documented escape hatch and the only way to write pixels without going through a stream.
uint8_t* bufferBytes(winrt::Windows::Storage::Streams::IBuffer const& buffer) {
    auto access = buffer.as<::Windows::Storage::Streams::IBufferByteAccess>();
    uint8_t* bytes = nullptr;
    if (FAILED(access->Buffer(&bytes))) return nullptr;
    return bytes;
}

} // namespace

namespace scribble {

Canvas::Canvas(Image image, FrameworkElement host, HWND hwnd)
    : image_(std::move(image)), host_(std::move(host)), hwnd_(hwnd) {}

double Canvas::rasterizationScale() const {
    if (!host_) return 1.0;
    // RasterizationScale, not the monitor DPI: it already folds in the display scale and any
    // scale XAML has applied above this element. Reading the monitor instead gives the right
    // answer only while nothing in the tree is scaled.
    const double s = host_.XamlRoot() ? host_.XamlRoot().RasterizationScale() : 1.0;
    return s > 0.0 ? s : 1.0;
}

void Canvas::ensureSurface() {
    if (!host_ || !image_) return;

    const double scale = rasterizationScale();
    const int w = static_cast<int>(std::lround(host_.ActualWidth()  * scale));
    const int h = static_cast<int>(std::lround(host_.ActualHeight() * scale));
    if (w <= 0 || h <= 0) return;
    if (w == surface_.width() && h == surface_.height() && bitmap_) return;

    if (!surface_.resize(w, h)) return;

    bitmap_ = WriteableBitmap(w, h);
    image_.Source(bitmap_);

    // The bitmap is w x h physical pixels; the element is sized in logical units so that it
    // covers exactly that many physical pixels. Without this division WinUI would lay the
    // bitmap out as w x h logical units and scale it up by the display factor.
    image_.Width(w / scale);
    image_.Height(h / scale);

    dirty_ = true;
    present();
}

void Canvas::drawSegment(float x1, float y1, float x2, float y2, float widthPx) {
    if (!surface_.valid()) return;

    skia::Paint paint;
    paint.color(0xFF000000).antialias(true).stroke(widthPx).roundCap().roundJoin();
    sk_canvas_draw_line(surface_.canvas(), x1, y1, x2, y2, paint.get());
    dirty_ = true;
}

void Canvas::clear() {
    if (!surface_.valid()) return;
    surface_.clear(0xFFFFFFFF);
    dirty_ = true;
    present();
}

void Canvas::present() {
    if (!dirty_ || !bitmap_ || !surface_.valid()) return;

    auto buffer = bitmap_.PixelBuffer();
    uint8_t* dst = bufferBytes(buffer);
    if (!dst) return;

    const size_t bytes = static_cast<size_t>(surface_.width()) * surface_.height() * 4;
    if (buffer.Capacity() < bytes) return;   // a resize that has not reached the bitmap yet

    std::memcpy(dst, surface_.pixels(), bytes);
    bitmap_.Invalidate();
    dirty_ = false;
}

std::pair<double, double> Canvas::desktopToCanvas(double x, double y) const {
    const Point origin = originOnDesktop();
    return { x - origin.X, y - origin.Y };
}

void Canvas::fillSurfaceRect(int x, int y, int size, int r, int g, int b) {
    if (!surface_.valid()) return;

    skia::Paint paint;
    paint.color(0xFF000000u | (static_cast<uint32_t>(r) << 16)
                            | (static_cast<uint32_t>(g) << 8)
                            |  static_cast<uint32_t>(b))
         .antialias(false);
    sk_paint_set_style(paint.get(), FILL_SK_PAINT_STYLE);

    const sk_rect_t rect{ static_cast<float>(x), static_cast<float>(y),
                          static_cast<float>(x + size), static_cast<float>(y + size) };
    sk_canvas_draw_rect(surface_.canvas(), &rect, paint.get());
    dirty_ = true;
}

Point Canvas::originOnDesktop() const {
    if (!host_) return Point{ 0, 0 };

    // Element to window, then window to desktop. TransformToVisual against the XamlRoot's
    // content gives logical units relative to the window's client area; the scale converts
    // that to physical, and the window rect puts it on the desktop.
    auto root = host_.XamlRoot();
    if (!root) return Point{ 0, 0 };

    auto transform = host_.TransformToVisual(root.Content().try_as<UIElement>());
    const Point inWindow = transform.TransformPoint(Point{ 0, 0 });
    const double scale = rasterizationScale();

    POINT clientOrigin{ 0, 0 };
    if (hwnd_) ::ClientToScreen(hwnd_, &clientOrigin);

    return Point{
        static_cast<float>(clientOrigin.x + inWindow.X * scale),
        static_cast<float>(clientOrigin.y + inWindow.Y * scale)
    };
}

} // namespace scribble
