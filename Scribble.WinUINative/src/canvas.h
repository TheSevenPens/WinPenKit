#pragma once

#include "pch.h"
#include "skia_raii.h"

namespace scribble {

/// The drawing surface: a Skia raster bitmap in physical pixels, presented through a WinUI
/// `Image` holding a `WriteableBitmap`.
///
/// The same shape as `Scribble.WinUI`'s `DrawingCanvas`, deliberately. Both keep the surface in
/// **physical device pixels** and size the `Image` element to `pixels / rasterizationScale`, so
/// one surface pixel lands on one screen pixel at any display scale. Letting XAML size the
/// bitmap in logical units instead is what makes a stroke soft on a 2.25x display, and the
/// `L1.presentation-1to1` check exists to catch exactly that.
class Canvas {
public:
    /// `hwnd` is the top-level window. The canvas needs it to place itself on the desktop,
    /// and asking XamlRoot for it through ContentIslandEnvironment is a longer route to the
    /// same handle that the caller already has.
    Canvas(winrt::Microsoft::UI::Xaml::Controls::Image image,
           winrt::Microsoft::UI::Xaml::FrameworkElement host,
           HWND hwnd);

    /// Matches the surface to the host's current size in physical pixels. Call on size change
    /// and on scale change; both alter the answer.
    void ensureSurface();

    /// Draws one segment in surface pixels. Width is physical pixels, as in every other
    /// sample, so the brush slider means the same thing across all of them.
    void drawSegment(float x1, float y1, float x2, float y2, float widthPx);

    void clear();

    /// Pushes the Skia pixels into the WriteableBitmap and invalidates it. Called once per
    /// batch of segments rather than per segment: each call copies the whole surface.
    void present();

    int surfaceWidth()  const { return surface_.width(); }
    int surfaceHeight() const { return surface_.height(); }

    /// The host's top-left in physical desktop pixels. Read live rather than cached: a window
    /// that moves must not keep reporting where it used to be.
    winrt::Windows::Foundation::Point originOnDesktop() const;

    double rasterizationScale() const;

    /// The surface, for the acceptance checks and the presentation probe.
    skia::RasterSurface& surface() { return surface_; }

private:
    winrt::Microsoft::UI::Xaml::Controls::Image image_{ nullptr };
    winrt::Microsoft::UI::Xaml::FrameworkElement host_{ nullptr };
    winrt::Microsoft::UI::Xaml::Media::Imaging::WriteableBitmap bitmap_{ nullptr };

    HWND hwnd_ = nullptr;

    skia::RasterSurface surface_;
    bool dirty_ = false;
};

} // namespace scribble
