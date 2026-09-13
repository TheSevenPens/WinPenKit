// Does the vendored header set + generated import library + shipped DLL actually work
// together? Draws a stroke into a raster surface and reads pixels back out, so a link that
// succeeds but calls into nothing is still caught.

#include "include/c/sk_surface.h"
#include "include/c/sk_canvas.h"
#include "include/c/sk_paint.h"
#include "include/c/sk_path.h"
#include "include/c/sk_image.h"
#include "include/c/sk_data.h"

#include <cstdio>
#include <cstdint>
#include <vector>

int main() {
    const int W = 64, H = 64;

    sk_imageinfo_t info{};
    info.colorspace = nullptr;
    info.width = W;
    info.height = H;
    info.colorType = BGRA_8888_SK_COLORTYPE;
    info.alphaType = PREMUL_SK_ALPHATYPE;

    std::vector<uint32_t> pixels(static_cast<size_t>(W) * H, 0u);

    sk_surface_t* surface =
        sk_surface_new_raster_direct(&info, pixels.data(), W * 4, nullptr, nullptr, nullptr);
    if (!surface) { printf("FAIL sk_surface_new_raster_direct returned null\n"); return 1; }

    sk_canvas_t* canvas = sk_surface_get_canvas(surface);
    if (!canvas) { printf("FAIL sk_surface_get_canvas returned null\n"); return 1; }

    sk_canvas_clear(canvas, 0xFFFFFFFF);

    sk_paint_t* paint = sk_paint_new();
    sk_paint_set_color(paint, 0xFF000000);
    sk_paint_set_antialias(paint, true);
    sk_paint_set_style(paint, STROKE_SK_PAINT_STYLE);
    sk_paint_set_stroke_width(paint, 8.0f);
    sk_paint_set_stroke_cap(paint, ROUND_SK_STROKE_CAP);

    sk_point_t a{ 8.0f, 32.0f };
    sk_point_t b{ 56.0f, 32.0f };
    sk_canvas_draw_line(canvas, a.x, a.y, b.x, b.y, paint);

    sk_paint_delete(paint);
    sk_surface_unref(surface);

    // A link can succeed against stubs; only reading the pixels proves Skia ran.
    const uint32_t centre = pixels[32u * W + 32u];
    const uint32_t corner = pixels[2u * W + 2u];
    int dark = 0;
    for (uint32_t p : pixels) if ((p & 0x00FFFFFFu) == 0u && (p >> 24) == 0xFFu) dark++;

    printf("centre = 0x%08X (expect opaque black 0xFF000000)\n", centre);
    printf("corner = 0x%08X (expect opaque white 0xFFFFFFFF)\n", corner);
    printf("fully black pixels = %d\n", dark);

    const bool ok = centre == 0xFF000000u && corner == 0xFFFFFFFFu && dark > 100;
    printf("%s\n", ok ? "PASS skia c api is live" : "FAIL pixels are not what Skia would draw");
    return ok ? 0 : 1;
}
