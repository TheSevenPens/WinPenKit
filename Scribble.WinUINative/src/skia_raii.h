#pragma once

// Thin C++ wrappers over Skia's C API.
//
// libSkiaSharp.dll exports Skia's flat C interface and no C++ symbols, so every call here is
// sk_something(handle, ...). That is workable but it leaks: every paint has to be deleted by
// hand, and one early return past a sk_paint_delete is a leak that nothing reports.
//
// These types exist so the rest of the sample reads like the other C++ sample, Scribble.Qt,
// rather than like C. They are deliberately minimal: no smart-pointer generality, no
// conversion operators beyond the one each type needs, nothing that hides which C function is
// being called.

#include "include/c/sk_canvas.h"
#include "include/c/sk_data.h"
#include "include/c/sk_image.h"
#include "include/c/sk_paint.h"
#include "include/c/sk_path.h"
#include "include/c/sk_surface.h"
#include "include/c/sk_types.h"

#include <cstdint>
#include <utility>
#include <vector>

namespace skia {

/// Owns an `sk_paint_t`. Move-only: two owners would double-delete, and Skia's C API gives no
/// way to detect that afterwards.
class Paint {
public:
    Paint() : p_(sk_paint_new()) {}
    ~Paint() { if (p_) sk_paint_delete(p_); }

    Paint(const Paint&) = delete;
    Paint& operator=(const Paint&) = delete;

    Paint(Paint&& o) noexcept : p_(std::exchange(o.p_, nullptr)) {}
    Paint& operator=(Paint&& o) noexcept {
        if (this != &o) { if (p_) sk_paint_delete(p_); p_ = std::exchange(o.p_, nullptr); }
        return *this;
    }

    sk_paint_t* get() const { return p_; }

    Paint& color(uint32_t argb)     { sk_paint_set_color(p_, argb); return *this; }
    Paint& antialias(bool on)       { sk_paint_set_antialias(p_, on); return *this; }
    Paint& stroke(float width) {
        sk_paint_set_style(p_, STROKE_SK_PAINT_STYLE);
        sk_paint_set_stroke_width(p_, width);
        return *this;
    }
    Paint& roundCap()  { sk_paint_set_stroke_cap(p_, ROUND_SK_STROKE_CAP);  return *this; }
    Paint& roundJoin() { sk_paint_set_stroke_join(p_, ROUND_SK_STROKE_JOIN); return *this; }

private:
    sk_paint_t* p_;
};

/// A raster surface Skia draws into, over pixel memory this class owns.
///
/// `sk_surface_new_raster_direct` rather than `sk_surface_new_raster`: the pixels have to be
/// readable afterwards to be copied into a WriteableBitmap, and a surface that allocates its
/// own would mean a snapshot and a second copy every frame.
///
/// BGRA_8888 premultiplied, matching what the four managed samples ask SkiaSharp for and what
/// a WinUI WriteableBitmap expects, so the copy out is a straight memcpy per row.
class RasterSurface {
public:
    RasterSurface() = default;
    ~RasterSurface() { release(); }

    RasterSurface(const RasterSurface&) = delete;
    RasterSurface& operator=(const RasterSurface&) = delete;

    bool valid() const { return surface_ != nullptr; }
    int width() const  { return width_; }
    int height() const { return height_; }

    const uint32_t* pixels() const { return pixels_.data(); }
    size_t rowBytes() const { return static_cast<size_t>(width_) * 4; }

    sk_canvas_t* canvas() const { return canvas_; }

    /// Resizes, preserving what was already drawn by blitting the old content in at the
    /// origin. Shrinking discards what falls outside, exactly as every other sample does, and
    /// growing again does not bring it back.
    ///
    /// Returns false and leaves the surface untouched if Skia refuses the allocation, so a
    /// caller that ignores the result draws into the old surface rather than into nothing.
    bool resize(int w, int h);

    void clear(uint32_t argb) { if (canvas_) sk_canvas_clear(canvas_, argb); }

private:
    void release();

    sk_surface_t* surface_ = nullptr;
    sk_canvas_t*  canvas_  = nullptr;   // owned by surface_, not separately released
    std::vector<uint32_t> pixels_;
    int width_ = 0;
    int height_ = 0;
};

} // namespace skia
