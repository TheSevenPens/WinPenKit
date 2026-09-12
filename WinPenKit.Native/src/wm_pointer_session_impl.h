#pragma once

// WM_POINTER pen input session implementation.
// Subclasses the app's window to intercept WM_POINTER* messages.
// Converts to PenPoint and enqueues to a thread-safe buffer.

#include "pen_session.h"
#include "log.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <commctrl.h>

#include <mutex>
#include <vector>
#include <atomic>
#include <cmath>
#include <string>

// Function pointers for dynamic loading.
typedef BOOL (WINAPI *GetPointerType_t)(UINT32, POINTER_INPUT_TYPE*);
typedef BOOL (WINAPI *GetPointerPenInfo_t)(UINT32, POINTER_PEN_INFO*);
typedef BOOL (WINAPI *GetPointerPenInfoHistory_t)(UINT32, UINT32*, POINTER_PEN_INFO*);
typedef BOOL (WINAPI *GetPointerDeviceRects_t)(HANDLE, RECT*, RECT*);

namespace wintab {

class WmPointerSessionImpl {
public:
    WmPointerSessionImpl();
    ~WmPointerSessionImpl();

    WmPointerSessionImpl(const WmPointerSessionImpl&) = delete;
    WmPointerSessionImpl& operator=(const WmPointerSessionImpl&) = delete;

    const char* start(HWND app_hwnd);
    void stop();

    int drain_points(PenPoint* buffer, int max_points);
    bool has_new_data() const { return has_new_data_.load(); }

    int  max_pressure() const { return 1024; } // WM_POINTER fixed range
    bool is_running() const { return running_; }

    void refresh_mapping() {} // no mapping needed for screen-pixel output

    /// Whether positions are carrying sub-pixel precision rather than whole pixels.
    bool is_hi_res() const { return hi_res_; }
    void on_activated() {}    // Windows routes pointer input by window - nothing to reclaim
    const char* debug_info() const { return debug_info_.c_str(); }

    static bool is_available();

private:
    bool running_ = false;
    HWND app_hwnd_ = nullptr;
    std::string debug_info_;

    GetPointerType_t get_pointer_type_ = nullptr;
    GetPointerPenInfo_t get_pointer_pen_info_ = nullptr;
    GetPointerPenInfoHistory_t get_pointer_pen_info_history_ = nullptr;
    GetPointerDeviceRects_t get_pointer_device_rects_ = nullptr;

    // POINTER_INFO carries the position twice: ptPixelLocationRaw in whole screen pixels, and
    // ptHimetricLocationRaw in 0.01mm units - about 7x finer on a typical display. Reading the
    // pixel one discards the precision on arrival. Measured on a Wacom over Windows Ink, the
    // median turn between consecutive segments was 11.31 degrees from the pixel field against
    // 2.54 from the himetric one; 11.31 is atan(1/5), the signature of a path forced onto an
    // integer grid at the ~2px steps a tablet reports.
    HANDLE rects_for_ = nullptr;
    RECT device_rect_ = {}, display_rect_ = {};
    bool hi_res_ = false;

    // Device position to screen position, sub-pixel where the device allows it.
    void resolve_position(const POINTER_INFO& info, double& x, double& y);

    std::mutex points_mutex_;
    std::vector<PenPoint> points_;
    std::atomic<bool> has_new_data_{false};

    static LRESULT CALLBACK subclass_proc(
        HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
        UINT_PTR uIdSubclass, DWORD_PTR dwRefData);

    static constexpr UINT_PTR SUBCLASS_ID = 0xAE5E5510;

    void on_pointer_message(UINT msg, WPARAM wp, LPARAM lp);

    static void tilt_to_spherical(double tiltX, double tiltY,
                                   double& azimuth, double& altitude);
};

} // namespace wintab
