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
        get_pointer_device_rects_ = reinterpret_cast<GetPointerDeviceRects_t>(
            GetProcAddress(user32, "GetPointerDeviceRects"));
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

// ── Sub-pixel positions ──────────────────────────────────────
//
// POINTER_INFO carries the position twice: ptPixelLocationRaw in whole screen pixels, and
// ptHimetricLocationRaw in 0.01mm units - about 7x finer on a typical display. Both arrive in
// every message, so reading the pixel one throws the precision away on arrival.
//
// The HIMETRIC value is expressed in the DEVICE's rect rather than in screen space, despite what
// the field name suggests, so turning it into a screen position is a normalization between the
// device rect and the display rect - not a conversion from 0.01mm to pixels. Getting that wrong
// puts strokes in the wrong place entirely rather than merely imprecisely.
void WmPointerSessionImpl::resolve_position(const POINTER_INFO& info, double& x, double& y)
{
    if (info.sourceDevice != rects_for_) {
        rects_for_ = info.sourceDevice;
        hi_res_ = get_pointer_device_rects_ && info.sourceDevice
            && get_pointer_device_rects_(info.sourceDevice, &device_rect_, &display_rect_)
            && device_rect_.right > device_rect_.left
            && device_rect_.bottom > device_rect_.top;
        Logger::log("Pointer device rects: hi-res=%d device=%ldx%ld display=%ldx%ld",
            hi_res_ ? 1 : 0,
            device_rect_.right - device_rect_.left, device_rect_.bottom - device_rect_.top,
            display_rect_.right - display_rect_.left, display_rect_.bottom - display_rect_.top);
    }

    if (!hi_res_) {
        x = static_cast<double>(info.ptPixelLocationRaw.x);
        y = static_cast<double>(info.ptPixelLocationRaw.y);
        return;
    }

    x = display_rect_.left + static_cast<double>(info.ptHimetricLocationRaw.x - device_rect_.left)
        / (device_rect_.right - device_rect_.left) * (display_rect_.right - display_rect_.left);
    y = display_rect_.top + static_cast<double>(info.ptHimetricLocationRaw.y - device_rect_.top)
        / (device_rect_.bottom - device_rect_.top) * (display_rect_.bottom - display_rect_.top);
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
                double tx = (pi.penMask & PEN_MASK_TILT_X) ? static_cast<double>(pi.tiltX) : 0.0;
                double ty = (pi.penMask & PEN_MASK_TILT_Y) ? static_cast<double>(pi.tiltY) : 0.0;
                double az = 0.0, alt = 90.0;
                tilt_to_spherical(tx, ty, az, alt);

                PenPoint pt = {};
                double hx = 0.0, hy = 0.0;
                resolve_position(pi.pointerInfo, hx, hy);
                pt.desktop_x = hx;
                pt.desktop_y = hy;
                // ptHimetricLocationRaw, not ptPixelLocationRaw: only the former is
                // device-native. See resolve_position above.
                pt.raw_x     = pi.pointerInfo.ptHimetricLocationRaw.x;
                pt.raw_y     = pi.pointerInfo.ptHimetricLocationRaw.y;
                pt.pressure  = (pi.penMask & PEN_MASK_PRESSURE) ? pi.pressure : 0;
                pt.azimuth   = az;
                pt.altitude  = alt;
                pt.twist     = (pi.penMask & PEN_MASK_ROTATION) ? static_cast<double>(pi.rotation) : 0.0;
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

    double desktop_x = 0.0, desktop_y = 0.0;
    resolve_position(pen_info.pointerInfo, desktop_x, desktop_y);

    // Pressure (0-1024).
    uint32_t pressure = 0;
    if (pen_info.penMask & PEN_MASK_PRESSURE)
        pressure = pen_info.pressure;

    // Native TiltX/TiltY in degrees from driver.
    double native_tilt_x = 0.0, native_tilt_y = 0.0;
    if (pen_info.penMask & PEN_MASK_TILT_X) native_tilt_x = static_cast<double>(pen_info.tiltX);
    if (pen_info.penMask & PEN_MASK_TILT_Y) native_tilt_y = static_cast<double>(pen_info.tiltY);

    // Convert TiltX/TiltY → Azimuth/Altitude (spherical, degrees).
    double azimuth = 0.0, altitude = 90.0;
    if (pen_info.penMask & (PEN_MASK_TILT_X | PEN_MASK_TILT_Y))
        tilt_to_spherical(native_tilt_x, native_tilt_y, azimuth, altitude);

    // Twist/rotation in degrees.
    double twist = 0.0;
    if (pen_info.penMask & PEN_MASK_ROTATION)
        twist = static_cast<double>(pen_info.rotation);

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
    // ptHimetricLocationRaw, not ptPixelLocationRaw: only the former is device-native.
    pt.raw_x     = pen_info.pointerInfo.ptHimetricLocationRaw.x;
    pt.raw_y     = pen_info.pointerInfo.ptHimetricLocationRaw.y;
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
// Input: TiltX/TiltY in degrees (-90 to +90).
// Output: Azimuth (0-360 degrees), Altitude (0-90 degrees).
//
// Azimuth = compass direction of the tilt (clockwise from north/up).
// Altitude = angle from the tablet surface (0=flat, 90=vertical).

void WmPointerSessionImpl::tilt_to_spherical(
    double tiltX, double tiltY, double& azimuth, double& altitude)
{
    double tilt_magnitude = std::sqrt(tiltX * tiltX + tiltY * tiltY);

    // Altitude: 90 (vertical) minus the tilt magnitude.
    altitude = 90.0 - tilt_magnitude;
    if (altitude < 0.0) altitude = 0.0;
    if (altitude > 90.0) altitude = 90.0;

    // Azimuth: compass direction of the tilt vector.
    // atan2(-tiltX, tiltY) gives angle from Y+ axis (north), clockwise.
    if (tilt_magnitude > 0.5) { // degrees threshold
        double angle_rad = std::atan2(-tiltX, tiltY);
        double angle_deg = angle_rad * 180.0 / M_PI;
        azimuth = std::fmod(std::fmod(angle_deg, 360.0) + 360.0, 360.0);
    } else {
        azimuth = 0.0;
    }
}

} // namespace wintab
