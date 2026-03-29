#include "wm_pointer_session_impl.h"

#pragma comment(lib, "comctl32.lib")
#include <cstdio>
#include <cmath>
#include <algorithm>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

namespace wintab {

// ── Static availability check ────────────────────────────────────

bool WmPointerSessionImpl::is_available() {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (!user32) return false;
    return GetProcAddress(user32, "GetPointerPenInfo") != nullptr;
}

// ── Construction / Destruction ───────────────────────────────────

WmPointerSessionImpl::WmPointerSessionImpl() {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        get_pointer_type_ = reinterpret_cast<GetPointerType_t>(
            GetProcAddress(user32, "GetPointerType"));
        get_pointer_pen_info_ = reinterpret_cast<GetPointerPenInfo_t>(
            GetProcAddress(user32, "GetPointerPenInfo"));
        get_pointer_pen_info_history_ = reinterpret_cast<GetPointerPenInfoHistory_t>(
            GetProcAddress(user32, "GetPointerPenInfoHistory"));
    }
}

WmPointerSessionImpl::~WmPointerSessionImpl() {
    stop();
}

// ── Lifecycle ────────────────────────────────────────────────────

const char* WmPointerSessionImpl::start(HWND app_hwnd) {
    if (!get_pointer_type_ || !get_pointer_pen_info_)
        return "WM_POINTER API not available on this system.";

    if (!app_hwnd)
        return "WM_POINTER requires an application window handle.";

    app_hwnd_ = app_hwnd;

    // Subclass the app window to intercept WM_POINTER messages.
    if (!SetWindowSubclass(app_hwnd_, subclass_proc, SUBCLASS_ID,
                           reinterpret_cast<DWORD_PTR>(this))) {
        return "Failed to subclass application window.";
    }

    running_ = true;

    char buf[256];
    snprintf(buf, sizeof(buf), "[WM_POINTER] Subclassed hwnd=%p", app_hwnd_);
    debug_info_ = buf;
    Logger::log("WM_POINTER session started, hwnd=%p", app_hwnd_);

    return nullptr;
}

void WmPointerSessionImpl::stop() {
    if (app_hwnd_ && running_) {
        RemoveWindowSubclass(app_hwnd_, subclass_proc, SUBCLASS_ID);
        Logger::log("WM_POINTER session stopped");
    }
    running_ = false;
    app_hwnd_ = nullptr;
}

// ── Output ───────────────────────────────────────────────────────

int WmPointerSessionImpl::drain_points(PenPoint* buffer, int max_points) {
    has_new_data_ = false;

    std::lock_guard<std::mutex> lock(points_mutex_);
    int count = std::min(max_points, static_cast<int>(points_.size()));
    if (count > 0) {
        memcpy(buffer, points_.data(), count * sizeof(PenPoint));
        points_.erase(points_.begin(), points_.begin() + count);
    }
    return count;
}

// ── Subclass proc ────────────────────────────────────────────────

LRESULT CALLBACK WmPointerSessionImpl::subclass_proc(
    HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
    UINT_PTR uIdSubclass, DWORD_PTR dwRefData)
{
    (void)uIdSubclass;

    if (uMsg == WM_POINTERUPDATE || uMsg == WM_POINTERDOWN || uMsg == WM_POINTERUP) {
        auto* self = reinterpret_cast<WmPointerSessionImpl*>(dwRefData);
        if (self && self->running_) {
            self->on_pointer_message(uMsg, wParam, lParam);
        }
    }

    if (uMsg == WM_NCDESTROY) {
        RemoveWindowSubclass(hWnd, subclass_proc, SUBCLASS_ID);
    }

    return DefSubclassProc(hWnd, uMsg, wParam, lParam);
}

// ── Message handler ──────────────────────────────────────────────

void WmPointerSessionImpl::on_pointer_message(UINT msg, WPARAM wp, LPARAM lp) {
    (void)msg;
    (void)lp;

    UINT32 pointer_id = GET_POINTERID_WPARAM(wp);

    // Only handle pen input.
    POINTER_INPUT_TYPE pointer_type = 0;
    if (!get_pointer_type_(pointer_id, &pointer_type)) return;
    if (pointer_type != PT_PEN) return;

    // For WM_POINTERUPDATE with coalesced events, use history to recover them.
    if (msg == WM_POINTERUPDATE && get_pointer_pen_info_history_) {
        POINTER_PEN_INFO history[64];
        UINT32 count = 64;
        if (get_pointer_pen_info_history_(pointer_id, &count, history) && count > 1) {
            std::lock_guard<std::mutex> lock(points_mutex_);
            for (int i = static_cast<int>(count) - 1; i >= 0; i--) {
                auto& pi = history[i];
                int tx = (pi.penMask & PEN_MASK_TILT_X) ? pi.tiltX * 10 : 0;
                int ty = (pi.penMask & PEN_MASK_TILT_Y) ? pi.tiltY * 10 : 0;
                int az = 0, alt = 900;
                tilt_to_spherical(tx, ty, az, alt);

                PenPoint pt = {};
                pt.desktop_x = static_cast<double>(pi.pointerInfo.ptPixelLocationRaw.x);
                pt.desktop_y = static_cast<double>(pi.pointerInfo.ptPixelLocationRaw.y);
                pt.raw_x     = pi.pointerInfo.ptPixelLocationRaw.x;
                pt.raw_y     = pi.pointerInfo.ptPixelLocationRaw.y;
                pt.pressure  = (pi.penMask & PEN_MASK_PRESSURE) ? pi.pressure : 0;
                pt.azimuth   = az;
                pt.altitude  = alt;
                pt.twist     = (pi.penMask & PEN_MASK_ROTATION) ? static_cast<int>(pi.rotation) * 10 : 0;
                pt.tilt_x    = tx;
                pt.tilt_y    = ty;
                pt.buttons   = ((pi.penFlags & PEN_FLAG_BARREL) ? 0x0001u : 0u) |
                               ((pi.penFlags & PEN_FLAG_ERASER) ? 0x0002u : 0u);
                pt.cursor    = (pi.penFlags & PEN_FLAG_INVERTED) ? 14 : 13;
                pt.source    = PEN_API_WM_POINTER;
                points_.push_back(pt);
            }
            has_new_data_ = true;
            return;
        }
    }

    // Single point path (non-coalesced, or DOWN/UP, or history fallback).
    POINTER_PEN_INFO pen_info = {};
    if (!get_pointer_pen_info_(pointer_id, &pen_info)) return;

    double desktop_x = static_cast<double>(pen_info.pointerInfo.ptPixelLocationRaw.x);
    double desktop_y = static_cast<double>(pen_info.pointerInfo.ptPixelLocationRaw.y);

    // Pressure (0-1024).
    uint32_t pressure = 0;
    if (pen_info.penMask & PEN_MASK_PRESSURE)
        pressure = pen_info.pressure;

    // Native TiltX/TiltY (degrees from driver, stored as tenths of degree).
    int native_tilt_x = 0, native_tilt_y = 0;
    if (pen_info.penMask & PEN_MASK_TILT_X) native_tilt_x = pen_info.tiltX * 10;
    if (pen_info.penMask & PEN_MASK_TILT_Y) native_tilt_y = pen_info.tiltY * 10;

    // Convert TiltX/TiltY → Azimuth/Altitude (spherical, tenths of degree).
    int azimuth = 0, altitude = 900;
    if (pen_info.penMask & (PEN_MASK_TILT_X | PEN_MASK_TILT_Y))
        tilt_to_spherical(native_tilt_x, native_tilt_y, azimuth, altitude);

    // Twist/rotation.
    int twist = 0;
    if (pen_info.penMask & PEN_MASK_ROTATION)
        twist = static_cast<int>(pen_info.rotation) * 10; // degrees → tenths

    // Buttons: encode eraser/barrel in a simple bitmask.
    uint32_t buttons = 0;
    if (pen_info.penFlags & PEN_FLAG_BARREL) buttons |= 0x0001;
    if (pen_info.penFlags & PEN_FLAG_ERASER) buttons |= 0x0002;

    // Cursor: map inverted flag to eraser cursor type for consistency with Wintab.
    uint32_t cursor = 13; // pen tip
    if (pen_info.penFlags & PEN_FLAG_INVERTED) cursor = 14; // eraser

    // Status: encode in-contact/in-range.
    uint32_t status = 0;
    // Note: WM_POINTER proximity semantics differ from Wintab.
    // We set the proximity bit when pen is in range but NOT in contact.

    PenPoint pt = {};
    pt.desktop_x = desktop_x;
    pt.desktop_y = desktop_y;
    pt.raw_x     = pen_info.pointerInfo.ptPixelLocationRaw.x;
    pt.raw_y     = pen_info.pointerInfo.ptPixelLocationRaw.y;
    pt.pressure  = pressure;
    pt.azimuth   = azimuth;
    pt.altitude  = altitude;
    pt.twist     = twist;
    pt.tilt_x    = native_tilt_x;
    pt.tilt_y    = native_tilt_y;
    pt.z         = 0; // WM_POINTER does not report Z height
    pt.status    = status;
    pt.buttons   = buttons;
    pt.cursor    = cursor;
    pt.source    = PEN_API_WM_POINTER;

    {
        std::lock_guard<std::mutex> lock(points_mutex_);
        points_.push_back(pt);
    }

    has_new_data_ = true;
}

// ── Tilt conversion ──────────────────────────────────────────────
//
// Input: TiltX/TiltY in tenths of degree (-900 to +900).
// Output: Azimuth (0-3600 tenths of degree), Altitude (0-900 tenths).
//
// Azimuth = compass direction of the tilt (clockwise from north/up).
// Altitude = angle from the tablet surface (0=flat, 900=vertical).

void WmPointerSessionImpl::tilt_to_spherical(
    int tiltX, int tiltY, int& azimuth, int& altitude)
{
    double tx = static_cast<double>(tiltX);
    double ty = static_cast<double>(tiltY);

    double tilt_magnitude = std::sqrt(tx * tx + ty * ty); // tenths of degree

    // Altitude: 900 (vertical) minus the tilt magnitude.
    altitude = static_cast<int>(900.0 - tilt_magnitude);
    if (altitude < 0) altitude = 0;
    if (altitude > 900) altitude = 900;

    // Azimuth: compass direction of the tilt vector.
    // atan2(-tiltX, tiltY) gives angle from Y+ axis (north), clockwise.
    if (tilt_magnitude > 5.0) { // ~0.5 degrees threshold
        double angle_rad = std::atan2(-tx, ty);
        int angle_tenths = static_cast<int>(angle_rad * 1800.0 / M_PI);
        azimuth = ((angle_tenths % 3600) + 3600) % 3600;
    } else {
        azimuth = 0;
    }
}

} // namespace wintab
