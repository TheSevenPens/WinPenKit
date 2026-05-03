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
    const char* debug_info() const { return debug_info_.c_str(); }

    static bool is_available();

private:
    bool running_ = false;
    HWND app_hwnd_ = nullptr;
    std::string debug_info_;

    GetPointerType_t get_pointer_type_ = nullptr;
    GetPointerPenInfo_t get_pointer_pen_info_ = nullptr;
    GetPointerPenInfoHistory_t get_pointer_pen_info_history_ = nullptr;

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
