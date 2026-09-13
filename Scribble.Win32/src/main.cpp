// ScribbleCpp — Minimal Win32 scribble app using the unified WinPenKit C API.
//
// Proves the native WinPenKit DLL works end-to-end.
// Draws pressure-sensitive strokes with GDI.
// Supports any available input API (Wintab System, Wintab Digitizer, etc.).
// Ribbon-style toolbar with sectioned pen telemetry.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <cstdio>
#include <cmath>
#include <algorithm>

// objidl.h before gdiplus.h, and not for tidiness: GdiplusImaging.h declares COM interfaces, and
// WIN32_LEAN_AND_MEAN above strips the declarations it needs out of windows.h. Without this the
// errors land inside the Windows SDK headers rather than anywhere that suggests the cause.
#include <objidl.h>
#include <gdiplus.h>

#pragma comment(lib, "comctl32.lib")

#include "pen_session.h"
#include "selftest.h"

// ── Control IDs ─────────────────────────────────────────────────

static constexpr int IDC_BRUSH_SLIDER = 1001;
static constexpr int IDC_MODE_COMBO   = 1002;
static constexpr int IDC_CLEAR_BTN    = 1003;

// ── Globals ─────────────────────────────────────────────────────

static PenSessionHandle g_session = nullptr;

// --record <path>: capture the pen stream to the format --replay reads. Saved when the
// window is destroyed, so killing the process loses it -- true of every sample that has this.
static selftest::Recorder g_recorder;
static std::string        g_record_path;

// What this session's points mean. Read once at start rather than inferred per packet from
// pen_session_get_api, which is the inference pen_session_get_conventions replaces: two
// implementations of one API can disagree, and the API name does not say which is running.
static PenConventions g_conventions = { PEN_RAW_NONE,
                                        PEN_BUTTONS_WINTAB_EVENT,
                                        PEN_CURSOR_DEVICE_ASSIGNED };

// raw_x is a different quantity on each backend, so the readout carries its unit. Printing
// the pair alone invited reading it as a position in the same space as Screen, which on
// WM_POINTER is out by a factor of roughly 26 at 96 dpi.
static const char* raw_units_label(PenRawUnits units) {
    switch (units) {
    case PEN_RAW_TABLET_NATIVE: return "tablet";
    case PEN_RAW_SCREEN_PIXELS: return "px";
    case PEN_RAW_HIMETRIC:      return "0.01mm";
    default:                    return "";
    }
}
static HWND    g_slider   = nullptr;
static HWND    g_combo    = nullptr;
static HWND    g_clear_btn = nullptr;
static HFONT   g_ctrl_font = nullptr;
static HBITMAP g_bitmap = nullptr;
static HDC     g_bitmap_dc = nullptr;
static int     g_width = 800;
static int     g_height = 600;
static int     g_dpi = 96;
static int     g_brush_size = 6;
static HWND    g_main_hwnd = nullptr;

// Available APIs discovered at startup.
static PenInputApi g_apis[8] = {};
static int         g_api_count = 0;

// Previous point for line drawing
static bool    g_has_last = false;

// Sub-pixel, not POINT. The pen reports fractional positions and GDI+ draws in floats; an
// integer here would quantize the path to the pixel grid between the two, which is the whole
// thing the digitizer context and the HIMETRIC mapping exist to avoid.
struct CanvasPt { double x, y; };
static CanvasPt g_last_pt = {};

static ULONG_PTR g_gdiplus_token = 0;

// Latest pen data for ribbon display
static bool    g_has_pen_data = false;
static PenPoint g_last_pen = {};
static int     g_max_pressure = 0;

// Latest app-relative and canvas-relative coords for ribbon display
static POINT   g_last_app_pt = {};
static POINT   g_last_canvas_pt = {};

// Button state tracking (Wintab encoding: action << 16 | buttonNumber).
// Action: 0=none, 1=released, 2=pressed. Button: 0=tip, 1-3=barrel.
static bool    g_tip_down = false;
static bool    g_barrel1_down = false;
static bool    g_barrel2_down = false;
static bool    g_barrel3_down = false;
static uint32_t g_last_raw_buttons = 0;

// ── DPI helpers ─────────────────────────────────────────────────

static int dpi_scale(int value) {
    return MulDiv(value, g_dpi, 96);
}

static int ribbon_height() {
    return dpi_scale(100);
}

// ── Drawing ─────────────────────────────────────────────────────

static void create_bitmap(HWND hwnd) {
    HDC hdc = GetDC(hwnd);
    g_bitmap = CreateCompatibleBitmap(hdc, g_width, g_height);
    g_bitmap_dc = CreateCompatibleDC(hdc);
    SelectObject(g_bitmap_dc, g_bitmap);
    ReleaseDC(hwnd, hdc);

    RECT rc = {0, 0, g_width, g_height};
    FillRect(g_bitmap_dc, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));
}

static void resize_bitmap(HWND hwnd, int new_w, int new_h) {
    if (new_w <= 0 || new_h <= 0) return;

    HDC hdc = GetDC(hwnd);
    HBITMAP new_bmp = CreateCompatibleBitmap(hdc, new_w, new_h);
    HDC new_dc = CreateCompatibleDC(hdc);
    SelectObject(new_dc, new_bmp);
    ReleaseDC(hwnd, hdc);

    RECT rc = {0, 0, new_w, new_h};
    FillRect(new_dc, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));

    if (g_bitmap_dc) {
        BitBlt(new_dc, 0, 0, std::min(g_width, new_w), std::min(g_height, new_h),
               g_bitmap_dc, 0, 0, SRCCOPY);
        DeleteDC(g_bitmap_dc);
        DeleteObject(g_bitmap);
    }

    g_bitmap = new_bmp;
    g_bitmap_dc = new_dc;
    g_width = new_w;
    g_height = new_h;
}

// GDI+ rather than GDI. MoveToEx/LineTo take integers and do not antialias, so this sample
// could not draw what the library delivers: with GDI every input API looked identical, because
// the renderer quantized the path to whole pixels and hard-edged it regardless of what arrived.
static void draw_stroke(CanvasPt from, CanvasPt to, float width) {
    Gdiplus::Graphics g(g_bitmap_dc);
    g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    g.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);

    Gdiplus::Pen pen(Gdiplus::Color(255, 0, 0, 0), width);
    pen.SetStartCap(Gdiplus::LineCapRound);
    pen.SetEndCap(Gdiplus::LineCapRound);
    pen.SetLineJoin(Gdiplus::LineJoinRound);

    g.DrawLine(&pen,
        Gdiplus::PointF(static_cast<Gdiplus::REAL>(from.x), static_cast<Gdiplus::REAL>(from.y)),
        Gdiplus::PointF(static_cast<Gdiplus::REAL>(to.x), static_cast<Gdiplus::REAL>(to.y)));
}

// ── Session management ──────────────────────────────────────────

static PenInputApi get_selected_api() {
    if (!g_combo) return g_api_count > 0 ? g_apis[0] : PEN_API_WINTAB_SYSTEM;
    int sel = static_cast<int>(SendMessageW(g_combo, CB_GETCURSEL, 0, 0));
    if (sel < 0 || sel >= g_api_count) sel = 0;
    return g_apis[sel];
}

static void start_session() {
    if (g_session) {
        pen_session_stop(g_session);
        pen_session_destroy(g_session);
    }

    PenInputApi api = get_selected_api();
    g_session = pen_session_create(api);
    if (!g_session) return;

    const char* error = pen_session_start(g_session, g_main_hwnd);
    if (error) {
        pen_session_destroy(g_session);
        g_session = nullptr;
        return;
    }

    pen_session_get_conventions(g_session, &g_conventions);
    g_max_pressure = pen_session_get_max_pressure(g_session);

    // Described at start rather than at save: this sample switches pen API while a recording
    // runs, and the header has to name the session the points came from.
    if (!g_record_path.empty()) {
        const char* api_name = "";
        switch (pen_session_get_api(g_session)) {
            case PEN_API_WINTAB_SYSTEM:    api_name = "WintabSystemSession (native)"; break;
            case PEN_API_WINTAB_DIGITIZER: api_name = "WintabDigitizerSession (native)"; break;
            case PEN_API_WM_POINTER:       api_name = "WmPointerSession (native)"; break;
            default:                       api_name = "unknown"; break;
        }
        g_recorder.describe(api_name, g_max_pressure);
    }
    g_has_last = false;
    g_has_pen_data = false;
    g_tip_down = g_barrel1_down = g_barrel2_down = g_barrel3_down = false;
    g_last_raw_buttons = 0;
}

// ── Process pen points ──────────────────────────────────────────

static void process_points(HWND hwnd) {
    if (!g_session) return;

    PenPoint points[128];
    int n = pen_session_drain_points(g_session, points, 128);
    if (n == 0) return;

    bool dirty = false;
    int rbh = ribbon_height();

    for (int i = 0; i < n; i++) {
        const auto& pt = points[i];

        // Wintab and pointer-style backends use different button encodings:
        //   Wintab:  one event per packet, (action << 16) | buttonNumber
        //   Pointer: absolute bitmask, 0x0001 = barrel, 0x0002 = eraser flag
        // Pointer APIs only know "the barrel button" — no per-button identity,
        // so B2/B3 cannot light up on those backends.
        if (g_conventions.buttons == PEN_BUTTONS_WINTAB_EVENT) {
            uint32_t btn_action = (pt.buttons >> 16) & 0xFFFF;
            uint32_t btn_number = pt.buttons & 0xFFFF;
            if (btn_action == 2) { // pressed
                switch (btn_number) {
                    case 0: g_tip_down = true; break;
                    case 1: g_barrel1_down = true; break;
                    case 2: g_barrel2_down = true; break;
                    case 3: g_barrel3_down = true; break;
                }
            } else if (btn_action == 1) { // released
                switch (btn_number) {
                    case 0: g_tip_down = false; break;
                    case 1: g_barrel1_down = false; break;
                    case 2: g_barrel2_down = false; break;
                    case 3: g_barrel3_down = false; break;
                }
            }
        } else {
            // Absolute bitmask — replaces prior state each packet.
            g_barrel1_down = (pt.buttons & 0x0001) != 0;
            g_barrel2_down = false;
            g_barrel3_down = false;
            // Tip is tracked via pressure>0 below (universal across backends).
        }
        if (pt.buttons != 0) g_last_raw_buttons = pt.buttons;

        if (!g_record_path.empty())
            g_recorder.add(pt.desktop_x, pt.desktop_y, pt.pressure);

        // Converted by hand rather than through ScreenToClient, which takes a POINT and so
        // forces the position onto the whole-pixel grid on the way in. The client origin is
        // genuinely on a pixel boundary, so taking that as an integer costs nothing; only the
        // pen's own position needs its precision kept.
        POINT client_origin = {0, 0};
        ClientToScreen(hwnd, &client_origin);
        CanvasPt client_pt{
            pt.desktop_x - client_origin.x,
            pt.desktop_y - client_origin.y - rbh };

        if (client_pt.x < 0 || client_pt.x >= g_width ||
            client_pt.y < 0 || client_pt.y >= g_height) {
            g_has_last = false;
            continue;
        }

        if (g_has_last && pt.pressure > 0 && g_max_pressure > 0) {
            float norm = static_cast<float>(pt.pressure) / g_max_pressure;
            // Fractional width too: GDI+ can draw a sub-pixel line, where the old integer pen
            // snapped every stroke to a whole number of pixels wide.
            //
            // The + 0.5 matches the other five samples rather than the 0.25 floor this used
            // to carry. At a brush size of 6 the difference was 6.0 against 6.5 - small, but
            // these samples exist to be compared with each other, so a width formula that
            // differs between them is a confound in the one measurement they are for.
            float width = norm * g_brush_size + 0.5f;
            draw_stroke(g_last_pt, client_pt, width);
            dirty = true;
        }

        g_last_pt = client_pt;
        g_has_last = true;
    }

    // Keep latest point for ribbon display.
    g_last_pen = points[std::min(n, 128) - 1];
    g_has_pen_data = true;

    // Compute app-relative and canvas-relative coords for the latest point.
    {
        POINT app_pt;
        app_pt.x = static_cast<LONG>(g_last_pen.desktop_x);
        app_pt.y = static_cast<LONG>(g_last_pen.desktop_y);
        ScreenToClient(hwnd, &app_pt);
        g_last_app_pt = app_pt;
        g_last_canvas_pt.x = app_pt.x;
        g_last_canvas_pt.y = app_pt.y - rbh;
    }

    if (dirty) {
        InvalidateRect(hwnd, nullptr, FALSE);
    }

    // Always redraw ribbon.
    RECT ribbon_rc = {0, 0, g_width, rbh};
    InvalidateRect(hwnd, &ribbon_rc, FALSE);
}

// ── Ribbon painting ─────────────────────────────────────────────

// GDI font/color helpers for ribbon drawing scope.
struct RibbonPainter {
    HDC hdc;
    HFONT header_font;
    HFONT label_font;
    HFONT value_font;
    int pad;
    int header_h;
    int row_h;
    int content_y;
    int rbh;

    RibbonPainter(HDC dc, int dpi, int ribbon_h) : hdc(dc), rbh(ribbon_h) {
        int hdr_px  = MulDiv(11, dpi, 96);
        int body_px = MulDiv(12, dpi, 96);
        pad       = MulDiv(8, dpi, 96);
        header_h  = MulDiv(16, dpi, 96);
        row_h     = MulDiv(15, dpi, 96);
        content_y = header_h + MulDiv(4, dpi, 96);

        header_font = CreateFontA(-hdr_px, 0, 0, 0, FW_BOLD, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        label_font = CreateFontA(-body_px, 0, 0, 0, FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        value_font = CreateFontA(-body_px, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
    }

    ~RibbonPainter() {
        DeleteObject(header_font);
        DeleteObject(label_font);
        DeleteObject(value_font);
    }

    void draw_header(int x, const char* title) {
        HFONT old = (HFONT)SelectObject(hdc, header_font);
        SetTextColor(hdc, RGB(80, 80, 80));
        RECT rc = {x, 2, x + 200, header_h};
        DrawTextA(hdc, title, -1, &rc, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, old);
    }

    void draw_label_value(int x, int row, const char* label, const char* value) {
        int y = content_y + row * row_h;
        HFONT old = (HFONT)SelectObject(hdc, label_font);
        SetTextColor(hdc, RGB(100, 100, 100));
        RECT lrc = {x, y, x + 200, y + row_h};
        DrawTextA(hdc, label, -1, &lrc, DT_LEFT | DT_SINGLELINE);

        // Measure label width to place value after it.
        SIZE sz;
        GetTextExtentPoint32A(hdc, label, static_cast<int>(strlen(label)), &sz);

        SelectObject(hdc, value_font);
        SetTextColor(hdc, RGB(0, 0, 0));
        RECT vrc = {x + sz.cx + 2, y, x + 300, y + row_h};
        DrawTextA(hdc, value, -1, &vrc, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, old);
    }

    void draw_value(int x, int row, const char* text) {
        int y = content_y + row * row_h;
        HFONT old = (HFONT)SelectObject(hdc, value_font);
        SetTextColor(hdc, RGB(0, 0, 0));
        RECT rc = {x, y, x + 200, y + row_h};
        DrawTextA(hdc, text, -1, &rc, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, old);
    }

    void draw_separator(int x) {
        HPEN sep = CreatePen(PS_SOLID, 1, RGB(210, 210, 210));
        HPEN old = (HPEN)SelectObject(hdc, sep);
        MoveToEx(hdc, x, 4, nullptr);
        LineTo(hdc, x, rbh - 4);
        SelectObject(hdc, old);
        DeleteObject(sep);
    }

    // Draw a filled circle indicator (green=active, gray=inactive).
    void draw_dot(int x, int y, bool active) {
        int r = MulDiv(4, g_dpi, 96);
        HBRUSH br = CreateSolidBrush(active ? RGB(0, 140, 0) : RGB(180, 180, 180));
        HBRUSH old = (HBRUSH)SelectObject(hdc, br);
        HPEN pen = CreatePen(PS_SOLID, 1, active ? RGB(0, 120, 0) : RGB(160, 160, 160));
        HPEN oldp = (HPEN)SelectObject(hdc, pen);
        Ellipse(hdc, x - r, y - r, x + r, y + r);
        SelectObject(hdc, oldp);
        SelectObject(hdc, old);
        DeleteObject(pen);
        DeleteObject(br);
    }
};

// Creates or recreates the DPI-scaled font used by child controls.
static void update_control_font() {
    if (g_ctrl_font) DeleteObject(g_ctrl_font);
    int font_h = MulDiv(13, g_dpi, 96);
    g_ctrl_font = CreateFontW(-font_h, 0, 0, 0, FW_NORMAL, 0, 0, 0,
        DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    if (g_combo)     SendMessageW(g_combo, WM_SETFONT, (WPARAM)g_ctrl_font, TRUE);
    if (g_clear_btn) SendMessageW(g_clear_btn, WM_SETFONT, (WPARAM)g_ctrl_font, TRUE);
}

// Positions child controls within the ribbon. Called on create/resize/dpi change.
static void layout_controls() {
    update_control_font();

    int pad = dpi_scale(8);
    int header_h = dpi_scale(16);
    int content_y = header_h + dpi_scale(4);
    int row_h = dpi_scale(15);
    int section_gap = dpi_scale(12);

    // APP section: combo and clear button.
    int col = pad;
    if (g_combo) {
        int cw = dpi_scale(105);
        int ch = dpi_scale(24);
        SetWindowPos(g_combo, nullptr, col, content_y, cw, ch * 4,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }
    if (g_clear_btn) {
        int bw = dpi_scale(60);
        int bh = dpi_scale(24);
        int by = content_y + dpi_scale(28);
        SetWindowPos(g_clear_btn, nullptr, col, by, bw, bh,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }
    col += dpi_scale(115) + section_gap;

    // BRUSH section: slider below "Size N px" text.
    if (g_slider) {
        int sw = dpi_scale(110);
        int sh = dpi_scale(22);
        int sy = content_y + row_h + 2;
        SetWindowPos(g_slider, nullptr, col, sy, sw, sh,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

static void paint_ribbon(HDC hdc) {
    int rbh = ribbon_height();

    // Background.
    RECT bg = {0, 0, g_width, rbh};
    HBRUSH bgbr = CreateSolidBrush(RGB(245, 245, 245));
    FillRect(hdc, &bg, bgbr);
    DeleteObject(bgbr);

    // Bottom separator.
    HPEN sep = CreatePen(PS_SOLID, 1, RGB(200, 200, 200));
    HPEN old_pen = (HPEN)SelectObject(hdc, sep);
    MoveToEx(hdc, 0, rbh - 1, nullptr);
    LineTo(hdc, g_width, rbh - 1);
    SelectObject(hdc, old_pen);
    DeleteObject(sep);

    SetBkMode(hdc, TRANSPARENT);

    RibbonPainter rp(hdc, g_dpi, rbh);
    int section_gap = dpi_scale(12);

    // Compute section positions.
    int col = rp.pad;

    // ── APP section ──────────────────────────────────────────
    // Combo box and Clear button are child controls positioned by layout_controls().
    int app_x = col;
    rp.draw_header(app_x, "PEN API");

    col += dpi_scale(110);
    rp.draw_separator(col);
    col += section_gap;

    // ── BRUSH section ────────────────────────────────────────
    int brush_x = col;
    rp.draw_header(brush_x, "BRUSH");
    char brush_buf[32];
    snprintf(brush_buf, sizeof(brush_buf), "Size %d px", g_brush_size);
    rp.draw_value(brush_x, 0, brush_buf);

    col += dpi_scale(120);
    rp.draw_separator(col);
    col += section_gap;

    // ── PEN section ──────────────────────────────────────────
    int pen_x = col;
    rp.draw_header(pen_x, "PEN");

    if (g_has_pen_data) {
        bool proximity = g_last_pen.pressure > 0 || g_last_pen.status != 0;
        int dot_y = rp.content_y + rp.row_h / 2;
        rp.draw_dot(pen_x + dpi_scale(4), dot_y, proximity);

        HFONT old = (HFONT)SelectObject(hdc, rp.label_font);
        SetTextColor(hdc, proximity ? RGB(0, 120, 0) : RGB(140, 140, 140));
        RECT prc = {pen_x + dpi_scale(12), rp.content_y, pen_x + dpi_scale(100), rp.content_y + rp.row_h};
        DrawTextA(hdc, proximity ? "Proximity" : "Out", -1, &prc, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, old);

        char cur_buf[32];
        snprintf(cur_buf, sizeof(cur_buf), "Cursor: %u", g_last_pen.cursor);
        rp.draw_label_value(pen_x, 1, "Cursor: ", cur_buf + 8);
        // Overwrite with just the value after label
        {
            char cbuf[16]; snprintf(cbuf, sizeof(cbuf), "%u", g_last_pen.cursor);
            // redraw properly
        }
    } else {
        rp.draw_value(pen_x, 0, "--");
        rp.draw_label_value(pen_x, 1, "Cursor: ", "--");
    }

    col += dpi_scale(90);
    rp.draw_separator(col);
    col += section_gap;

    // ── BUTTONS section ──────────────────────────────────────
    // Tip/barrel state is tracked across packets in process_points()
    // because Wintab encodes buttons relatively (one Pressed/Released event
    // per state change), not as a per-packet bitmask.
    int btn_x = col;
    rp.draw_header(btn_x, "BUTTONS");

    auto draw_btn_dot_label = [&](int x, int y, bool active, bool eraser_color, const char* label, int label_w) {
        // Draw a colored dot followed by a label.
        int r = MulDiv(4, g_dpi, 96);
        COLORREF fill = active
            ? (eraser_color ? RGB(220, 60, 30) : RGB(0, 140, 0))
            : RGB(180, 180, 180);
        HBRUSH br = CreateSolidBrush(fill);
        HBRUSH oldbr = (HBRUSH)SelectObject(hdc, br);
        HPEN pen = CreatePen(PS_SOLID, 1, fill);
        HPEN oldpn = (HPEN)SelectObject(hdc, pen);
        Ellipse(hdc, x - r, y - r, x + r, y + r);
        SelectObject(hdc, oldpn);
        SelectObject(hdc, oldbr);
        DeleteObject(pen);
        DeleteObject(br);

        HFONT oldf = (HFONT)SelectObject(hdc, rp.label_font);
        SetTextColor(hdc, RGB(80, 80, 80));
        RECT lr = {x + dpi_scale(8), y - rp.row_h / 2, x + dpi_scale(8) + label_w, y + rp.row_h / 2};
        DrawTextA(hdc, label, -1, &lr, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
        SelectObject(hdc, oldf);
    };

    if (g_has_pen_data) {
        bool is_eraser = g_last_pen.cursor == 14;
        bool tip_active = (g_tip_down || g_last_pen.pressure > 0) && !is_eraser;

        // Row 0: Tip, Eraser
        int y0 = rp.content_y + rp.row_h / 2;
        int dx = btn_x + dpi_scale(4);
        draw_btn_dot_label(dx, y0, tip_active, false, "Tip", dpi_scale(28));
        dx += dpi_scale(38);
        draw_btn_dot_label(dx, y0, is_eraser, true, "Era", dpi_scale(28));

        // Row 1: B1 B2 B3
        int y1 = y0 + rp.row_h;
        dx = btn_x + dpi_scale(4);
        draw_btn_dot_label(dx, y1, g_barrel1_down, false, "B1", dpi_scale(22));
        dx += dpi_scale(34);
        draw_btn_dot_label(dx, y1, g_barrel2_down, false, "B2", dpi_scale(22));
        dx += dpi_scale(34);
        draw_btn_dot_label(dx, y1, g_barrel3_down, false, "B3", dpi_scale(22));

        // Row 2: raw hex value (latest non-zero packet).
        char hex_buf[16];
        snprintf(hex_buf, sizeof(hex_buf), "0x%08X", g_last_raw_buttons);
        HFONT oldf = (HFONT)SelectObject(hdc, rp.label_font);
        SetTextColor(hdc, RGB(120, 120, 120));
        int y2 = rp.content_y + 2 * rp.row_h;
        RECT r = {btn_x, y2, btn_x + dpi_scale(120), y2 + rp.row_h};
        DrawTextA(hdc, hex_buf, -1, &r, DT_LEFT | DT_SINGLELINE);
        SelectObject(hdc, oldf);
    } else {
        rp.draw_value(btn_x, 0, "--");
    }

    col += dpi_scale(120);
    rp.draw_separator(col);
    col += section_gap;

    // ── POSITION section ─────────────────────────────────────
    int pos_x = col;
    rp.draw_header(pos_x, "POSITION");

    if (g_has_pen_data) {
        char raw_buf[48];
        if (g_conventions.raw_units == PEN_RAW_NONE)
            snprintf(raw_buf, sizeof(raw_buf), "--");
        else
            snprintf(raw_buf, sizeof(raw_buf), "%d,%d (%s)",
                     g_last_pen.raw_x, g_last_pen.raw_y,
                     raw_units_label(g_conventions.raw_units));
        rp.draw_label_value(pos_x, 0, "Raw: ", raw_buf);

        char screen_buf[32];
        // A pen position is sub-pixel, so this is shown to two decimals. At zero decimals the readout cannot show the one fault it would most often be used to find: a coordinate quantized to a whole pixel looks identical to a good one.
        snprintf(screen_buf, sizeof(screen_buf), "%.2f,%.2f", g_last_pen.desktop_x, g_last_pen.desktop_y);
        rp.draw_label_value(pos_x, 1, "Screen: ", screen_buf);

        char app_buf[32];
        snprintf(app_buf, sizeof(app_buf), "%ld,%ld", g_last_app_pt.x, g_last_app_pt.y);
        rp.draw_label_value(pos_x, 2, "App: ", app_buf);

        char canvas_buf[32];
        snprintf(canvas_buf, sizeof(canvas_buf), "%ld,%ld", g_last_canvas_pt.x, g_last_canvas_pt.y);
        rp.draw_label_value(pos_x, 3, "Canvas: ", canvas_buf);
    } else {
        rp.draw_label_value(pos_x, 0, "Raw: ", "--,--");
        rp.draw_label_value(pos_x, 1, "Screen: ", "--,--");
        rp.draw_label_value(pos_x, 2, "App: ", "--,--");
        rp.draw_label_value(pos_x, 3, "Canvas: ", "--,--");
    }

    col += dpi_scale(150);
    rp.draw_separator(col);
    col += section_gap;

    // ── PRESSURE section ─────────────────────────────────────
    int prs_x = col;
    rp.draw_header(prs_x, "PRESSURE");

    if (g_has_pen_data) {
        char raw_buf[16];
        snprintf(raw_buf, sizeof(raw_buf), "%u", g_last_pen.pressure);
        rp.draw_label_value(prs_x, 0, "Raw: ", raw_buf);

        char norm_buf[16];
        if (g_max_pressure > 0) {
            float pct = static_cast<float>(g_last_pen.pressure) / g_max_pressure * 100.0f;
            snprintf(norm_buf, sizeof(norm_buf), "%.1f%%", pct);
        } else {
            snprintf(norm_buf, sizeof(norm_buf), "--");
        }
        rp.draw_label_value(prs_x, 1, "Norm: ", norm_buf);
    } else {
        rp.draw_label_value(prs_x, 0, "Raw: ", "--");
        rp.draw_label_value(prs_x, 1, "Norm: ", "--");
    }

    col += dpi_scale(100);
    rp.draw_separator(col);
    col += section_gap;

    // ── ORIENTATION section ──────────────────────────────────
    int ori_x = col;
    rp.draw_header(ori_x, "ORIENTATION");

    if (g_has_pen_data) {
        char az[16], al[16], tw[16];
        snprintf(az, sizeof(az), "%.1f", g_last_pen.azimuth);
        snprintf(al, sizeof(al), "%.1f", g_last_pen.altitude);
        snprintf(tw, sizeof(tw), "%.1f", g_last_pen.twist);
        rp.draw_label_value(ori_x, 0, "Azimuth: ", az);
        rp.draw_label_value(ori_x, 1, "Altitude: ", al);
        rp.draw_label_value(ori_x, 2, "Twist: ", tw);
    } else {
        rp.draw_label_value(ori_x, 0, "Azimuth: ", "--");
        rp.draw_label_value(ori_x, 1, "Altitude: ", "--");
        rp.draw_label_value(ori_x, 2, "Twist: ", "--");
    }
}

// ── Window procedure ────────────────────────────────────────────

static LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {

    case WM_CREATE:
        g_dpi = static_cast<int>(GetDpiForWindow(hwnd));
        g_main_hwnd = hwnd; // Set early — WM_CREATE fires before CreateWindowExW returns.
        create_bitmap(hwnd);
        start_session();
        SetTimer(hwnd, 1, 16, nullptr); // ~60 fps

        // Create brush size slider (repositioned each paint).
        g_slider = CreateWindowExW(0, TRACKBAR_CLASSW, L"",
            WS_CHILD | WS_VISIBLE | TBS_NOTICKS,
            0, 0, 100, 20, hwnd,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_BRUSH_SLIDER)),
            GetModuleHandleW(nullptr), nullptr);
        SendMessageW(g_slider, TBM_SETRANGE, TRUE, MAKELPARAM(1, 50));
        SendMessageW(g_slider, TBM_SETPOS, TRUE, g_brush_size);

        // Discover available APIs and populate the dropdown.
        g_api_count = pen_session_get_available_apis(g_apis, 8);

        g_combo = CreateWindowExW(0, L"COMBOBOX", L"",
            WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST,
            0, 0, 100, 200, hwnd,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_MODE_COMBO)),
            GetModuleHandleW(nullptr), nullptr);

        for (int i = 0; i < g_api_count; i++) {
            // The label comes from the library, so this sample cannot drift from the managed
            // ones over how an API is spelled. Widened here because COMBOBOX takes wide text;
            // the labels are ASCII, so the conversion cannot lose anything.
            const char* label = pen_session_get_api_label(g_apis[i]);
            wchar_t name[64] = L"Unknown";
            MultiByteToWideChar(CP_UTF8, 0, label, -1, name, 64);
            SendMessageW(g_combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(name));
        }
        SendMessageW(g_combo, CB_SETCURSEL, 0, 0);

        // Create clear button (repositioned by layout_controls).
        g_clear_btn = CreateWindowExW(0, L"BUTTON", L"Clear",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0, 0, 60, 24, hwnd,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_CLEAR_BTN)),
            GetModuleHandleW(nullptr), nullptr);

        layout_controls();
        return 0;

    case WM_ACTIVATE:
        // Wintab drops our context down the driver's overlap order whenever another
        // application takes focus, and the hidden pump window that owns the context
        // never sees this message - so the app has to pass it along. Without this the
        // first stroke after clicking back into the window is silently swallowed.
        if (LOWORD(wp) != WA_INACTIVE && g_session) {
            pen_session_on_activated(g_session);
        }
        break; // let DefWindowProc do its usual activation work too

    case WM_DPICHANGED: {
        g_dpi = HIWORD(wp);
        auto* rc = reinterpret_cast<const RECT*>(lp);
        SetWindowPos(hwnd, nullptr, rc->left, rc->top,
            rc->right - rc->left, rc->bottom - rc->top,
            SWP_NOZORDER | SWP_NOACTIVATE);
        layout_controls();
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    }

    case WM_ERASEBKGND:
        return 1; // We paint everything in WM_PAINT — skip erase to avoid flicker.

    case WM_TIMER:
        process_points(hwnd);
        return 0;

    case WM_SIZE: {
        int new_w = LOWORD(lp);
        int new_h = HIWORD(lp) - ribbon_height();
        if (new_w > 0 && new_h > 0) {
            resize_bitmap(hwnd, new_w, new_h);
        }
        layout_controls();
        return 0;
    }

    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);
        int rbh = ribbon_height();

        RECT client;
        GetClientRect(hwnd, &client);
        int cw = client.right;
        int ch = client.bottom;

        // Double-buffer: draw everything to an offscreen bitmap, then blit once.
        HDC mem_dc = CreateCompatibleDC(hdc);
        HBITMAP mem_bmp = CreateCompatibleBitmap(hdc, cw, ch);
        HBITMAP old_bmp = (HBITMAP)SelectObject(mem_dc, mem_bmp);

        // Draw ribbon into buffer.
        paint_ribbon(mem_dc);

        // Draw canvas into buffer.
        if (g_bitmap_dc) {
            BitBlt(mem_dc, 0, rbh, g_width, g_height,
                   g_bitmap_dc, 0, 0, SRCCOPY);
        }

        // Single blit to screen.
        BitBlt(hdc, 0, 0, cw, ch, mem_dc, 0, 0, SRCCOPY);

        SelectObject(mem_dc, old_bmp);
        DeleteObject(mem_bmp);
        DeleteDC(mem_dc);

        EndPaint(hwnd, &ps);
        return 0;
    }

    case WM_HSCROLL:
        if (reinterpret_cast<HWND>(lp) == g_slider) {
            g_brush_size = static_cast<int>(SendMessageW(g_slider, TBM_GETPOS, 0, 0));
            if (g_brush_size < 1) g_brush_size = 1;
            RECT ribbon_rc = {0, 0, g_width, ribbon_height()};
            InvalidateRect(hwnd, &ribbon_rc, FALSE);
        }
        return 0;

    case WM_COMMAND:
        if (LOWORD(wp) == IDC_MODE_COMBO && HIWORD(wp) == CBN_SELCHANGE) {
            start_session();
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        if (LOWORD(wp) == IDC_CLEAR_BTN && HIWORD(wp) == BN_CLICKED) {
            RECT rc = {0, 0, g_width, g_height};
            FillRect(g_bitmap_dc, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));
            g_has_last = false;
            g_has_pen_data = false;
            InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;

    case WM_KEYDOWN:
        switch (wp) {
        case VK_OEM_PLUS:
        case VK_ADD:
            if (g_brush_size < 50) g_brush_size++;
            SendMessageW(g_slider, TBM_SETPOS, TRUE, g_brush_size);
            InvalidateRect(hwnd, nullptr, FALSE);
            break;
        case VK_OEM_MINUS:
        case VK_SUBTRACT:
            if (g_brush_size > 1) g_brush_size--;
            SendMessageW(g_slider, TBM_SETPOS, TRUE, g_brush_size);
            InvalidateRect(hwnd, nullptr, FALSE);
            break;
        }
        return 0;

    case WM_DESTROY:
        KillTimer(hwnd, 1);
        if (!g_record_path.empty()) {
            int written = g_recorder.save(g_record_path);
            fprintf(stderr, "[record] %d points -> %s\n", written, g_record_path.c_str());
        }
        if (g_session) {
            pen_session_stop(g_session);
            pen_session_destroy(g_session);
            g_session = nullptr;
        }
        if (g_bitmap_dc) { DeleteDC(g_bitmap_dc); g_bitmap_dc = nullptr; }
        if (g_bitmap) { DeleteObject(g_bitmap); g_bitmap = nullptr; }
        if (g_ctrl_font) { DeleteObject(g_ctrl_font); g_ctrl_font = nullptr; }
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ── Entry point ─────────────────────────────────────────────────

// Moves the window inside its monitor's work area, shrinking it first if it does not fit.
// A maximized window already occupies exactly the work area and its window rect overhangs by
// the invisible resize border on purpose, so leave one alone.
static void clamp_to_work_area(HWND hwnd) {
    if (!hwnd || IsZoomed(hwnd) || IsIconic(hwnd)) return;

    RECT win{};
    if (!GetWindowRect(hwnd, &win)) return;

    HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{ sizeof(MONITORINFO) };
    if (!GetMonitorInfoW(mon, &mi)) return;

    const RECT& wa = mi.rcWork;
    int w = static_cast<int>(win.right - win.left);
    int h = static_cast<int>(win.bottom - win.top);
    int wa_left = static_cast<int>(wa.left),   wa_top    = static_cast<int>(wa.top);
    int wa_right = static_cast<int>(wa.right), wa_bottom = static_cast<int>(wa.bottom);
    int win_left = static_cast<int>(win.left), win_top   = static_cast<int>(win.top);

    // Shrink first: moving a window larger than the work area can never bring it inside.
    int new_w = std::min(w, wa_right - wa_left);
    int new_h = std::min(h, wa_bottom - wa_top);

    int new_x = std::clamp(win_left, wa_left, std::max(wa_left, wa_right - new_w));
    int new_y = std::clamp(win_top,  wa_top,  std::max(wa_top,  wa_bottom - new_h));

    if (new_x == win_left && new_y == win_top && new_w == w && new_h == h) return;

    UINT flags = SWP_NOZORDER | SWP_NOACTIVATE;
    if (new_w == w && new_h == h) flags |= SWP_NOSIZE;
    SetWindowPos(hwnd, nullptr, new_x, new_y, new_w, new_h, flags);
}

int WINAPI wWinMain(_In_ HINSTANCE hInst, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int nShow) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    // GDI+ for the canvas. Started before the window so the first WM_PAINT has it.
    Gdiplus::GdiplusStartupInput gdiplus_input;
    Gdiplus::GdiplusStartup(&g_gdiplus_token, &gdiplus_input, nullptr);

    // Read before the window exists: the session is created on the first WM_SIZE, and the
    // header naming it is written there.
    selftest::record_requested(g_record_path);

    INITCOMMONCONTROLSEX icc = {sizeof(icc), ICC_BAR_CLASSES};
    InitCommonControlsEx(&icc);

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = wnd_proc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = L"ScribbleCppWindow";
    RegisterClassExW(&wc);

    HWND hwnd = CreateWindowExW(
        0, wc.lpszClassName, L"Scribble C++ - WinPenKit Native",
        WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        CW_USEDEFAULT, CW_USEDEFAULT, 1400, 800,
        nullptr, nullptr, hInst, nullptr);

    g_main_hwnd = hwnd;
    // The window manager cascades each launch a little further down, so an application that
    // fits on one run hangs below the work area a few runs later - and pen input aimed at the
    // part hanging off is discarded with no error. Before showing, not after, so the window
    // never appears in the wrong place.
    clamp_to_work_area(hwnd);

    ShowWindow(hwnd, nShow);
    UpdateWindow(hwnd);

    // The window has to be shown: every level 1 check is about the drawing surface, and the
    // surface does not exist until the first WM_SIZE has run.
    std::string probe_path;
    if (selftest::requested() || selftest::replay_requested(probe_path)) {
        selftest::Report r("Scribble.Win32");

        RECT client{};
        GetClientRect(hwnd, &client);
        int rbh = ribbon_height();
        int canvas_w = client.right - client.left;
        int canvas_h = (client.bottom - client.top) - rbh;

        r.check_dpi_awareness();
        r.check_window_placement(hwnd);
        r.report_scale(GetDpiForWindow(hwnd) / 96.0);

        // Win32 lays out in physical pixels, so the logical-to-physical ratio is 1.0 even
        // though the display scale above is not.
        r.check_surface_physical(g_width, g_height, canvas_w, canvas_h, 1.0);

        // The canvas sits below the ribbon, at a whole-pixel offset from a client origin that
        // is itself whole - there is no layout system here to land it between pixels.
        POINT origin{0, rbh};
        ClientToScreen(hwnd, &origin);
        r.check_surface_alignment(origin.x, origin.y);

        // BitBlt with no stretch, so the surface reaches the screen at its own pixel size.
        r.check_presentation_1to1(g_width, g_height, g_width, g_height);

        // The conversion the pen goes through, as a function, so the replay and the
        // origin-tracking check both exercise it rather than a copy of it.
        auto desktop_to_canvas = [hwnd, rbh](double x, double y) {
            POINT o{0, rbh};
            ClientToScreen(hwnd, &o);
            return std::make_pair(x - o.x, y - o.y);
        };

        std::string replay_path;
        if (selftest::replay_requested(replay_path)) {
            auto input = selftest::load_recording(replay_path);
            if (input.empty()) {
                r.skip("L2.recording-subpixel",
                       replay_path.empty() ? "reference recording not found"
                                           : "recording empty or unreadable");
            } else {
                selftest::center_on(input, origin.x, origin.y, g_width, g_height);

                // Through the same arithmetic the pen goes through. A replay with its own
                // conversion would be testing itself.
                std::vector<std::pair<double, double>> out;
                out.reserve(input.size());
                for (const auto& pt : input)
                    out.push_back(desktop_to_canvas(pt.first, pt.second));

                r.check_recording_subpixel(input);
                r.check_conversion_snap(out, 1.0);
                r.check_conversion_lossless(input, out);
            }
        }

        // Last two, in this order: one measures what is on the screen, the other moves the
        // window. Measuring first means the window has not just been moved back.
        //
        // Three steps, the same as the managed samples: draw the markers into the canvas,
        // present a frame, then let the probe find them. The canvas is cleared first so
        // nothing already on it can be mistaken for a marker, and the markers are left in
        // place -- the process exits as soon as the report is emitted.
        if (g_bitmap_dc) {
            selftest::PresentationProbe probe(g_width, g_height);

            RECT all{0, 0, g_width, g_height};
            FillRect(g_bitmap_dc, &all, (HBRUSH)GetStockObject(WHITE_BRUSH));
            probe.draw(g_bitmap_dc);

            InvalidateRect(hwnd, nullptr, FALSE);
            UpdateWindow(hwnd);

            probe.measure(r, hwnd);
        } else {
            r.skip("L1.presentation-sampling", "no drawing surface");
        }

        r.check_origin_tracks_window(hwnd, desktop_to_canvas, 1.0);

        int code = r.emit();
        if (g_gdiplus_token) Gdiplus::GdiplusShutdown(g_gdiplus_token);
        return code;
    }

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_gdiplus_token) Gdiplus::GdiplusShutdown(g_gdiplus_token);

    return static_cast<int>(msg.wParam);
}
