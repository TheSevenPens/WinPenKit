#include "pch.h"
#include "skia_raii.h"

#include <algorithm>
#include <cstring>

namespace skia {

void RasterSurface::release() {
    if (surface_) sk_surface_unref(surface_);
    surface_ = nullptr;
    canvas_  = nullptr;   // owned by the surface
}

bool RasterSurface::resize(int w, int h) {
    if (w <= 0 || h <= 0) return false;
    if (w == width_ && h == height_ && surface_) return true;

    std::vector<uint32_t> next(static_cast<size_t>(w) * h, 0xFFFFFFFFu);

    // Carry the old content over before the old buffer goes away. Row by row rather than one
    // memcpy: the stride changes with the width, so a single copy would shear the image.
    if (!pixels_.empty()) {
        const int copyW = (std::min)(w, width_);
        const int copyH = (std::min)(h, height_);
        for (int y = 0; y < copyH; ++y) {
            std::memcpy(next.data() + static_cast<size_t>(y) * w,
                        pixels_.data() + static_cast<size_t>(y) * width_,
                        static_cast<size_t>(copyW) * 4);
        }
    }

    sk_imageinfo_t info{};
    info.colorspace = nullptr;
    info.width      = w;
    info.height     = h;
    info.colorType  = BGRA_8888_SK_COLORTYPE;
    info.alphaType  = PREMUL_SK_ALPHATYPE;

    // The surface points at this vector's memory, so the vector has to be moved into place
    // before the surface is created and must not reallocate afterwards.
    std::vector<uint32_t> owned = std::move(next);

    sk_surface_t* s = sk_surface_new_raster_direct(
        &info, owned.data(), static_cast<size_t>(w) * 4, nullptr, nullptr, nullptr);

    // Leave the existing surface alone on failure. Tearing down what works in order to report
    // that something else did not is how a transient allocation failure becomes a blank canvas
    // for the rest of the session.
    if (!s) return false;

    release();
    pixels_  = std::move(owned);
    surface_ = s;
    canvas_  = sk_surface_get_canvas(s);
    width_   = w;
    height_  = h;
    return true;
}

} // namespace skia
