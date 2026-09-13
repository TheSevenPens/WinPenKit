#pragma once

// Internal C++ implementation of WintabSession.
// Consumers see only the C ABI in pen_session.h.

#include "wintab/wintab.h"
#include "pen_session.h"

// Internal resolution enum (was in the old wintab_session.h).
typedef enum {
    WINTAB_RESOLUTION_SCREEN    = 0,
    WINTAB_RESOLUTION_DIGITIZER = 1
} WintabResolution;
#include "wintab_loader.h"
#include "scale_axis.h"
#include "log.h"

#include <mutex>
#include <vector>
#include <thread>
#include <atomic>
#include <string>
#include <cstring>

namespace wintab {

// RAII wrapper for a Wintab context handle.
class ContextGuard {
public:
    ContextGuard() = default;
    ContextGuard(HCTX ctx, WTCLOSE_FUNC close_fn)
        : ctx_(ctx), close_fn_(close_fn) {}
    ~ContextGuard() { close(); }

    // Non-copyable, movable
    ContextGuard(const ContextGuard&) = delete;
    ContextGuard& operator=(const ContextGuard&) = delete;
    ContextGuard(ContextGuard&& other) noexcept
        : ctx_(other.ctx_), close_fn_(other.close_fn_) {
        other.ctx_ = nullptr;
    }

    ContextGuard& operator=(ContextGuard&& other) noexcept {
        if (this != &other) {
            close();
            ctx_ = other.ctx_;
            close_fn_ = other.close_fn_;
            other.ctx_ = nullptr;
        }
        return *this;
    }

    void close() {
        if (ctx_ && close_fn_) {
            close_fn_(ctx_);
            Logger::log("Context closed: %p", ctx_);
        }
        ctx_ = nullptr;
    }

    HCTX get() const { return ctx_; }
    explicit operator bool() const { return ctx_ != nullptr; }

private:
    HCTX ctx_ = nullptr;
    WTCLOSE_FUNC close_fn_ = nullptr;
};

class WintabSessionImpl {
public:
    WintabSessionImpl();
    ~WintabSessionImpl();

    // Non-copyable
    WintabSessionImpl(const WintabSessionImpl&) = delete;
    WintabSessionImpl& operator=(const WintabSessionImpl&) = delete;

    const char* start(WintabResolution resolution, HWND app_hwnd);

    // ── Capture region ──────────────────────────────────────────
    //
    // Wintab reads the whole desktop, so without a region a session reports the pen over
    // other applications' windows. The managed binding defaults to the application window;
    // this had no region at all, which is issue 41.
    void set_capture_window(HWND hwnd);
    void set_capture_rect(int left, int top, int right, int bottom);
    void set_capture_unbounded();
    void stop();

    int drain_points(PenPoint* buffer, int max_points);
    bool has_new_data() const { return has_new_data_.load(); }

    int  max_pressure() const { return max_pressure_; }
    bool is_running() const { return running_.load(); }
    bool is_digitizer_mode() const { return use_digitizer_; }

    /// Which resolution was asked for, regardless of what the fallback settled on.
    bool was_digitizer_requested() const { return requested_digitizer_; }

    void refresh_mapping();
    void on_activated();
    const char* debug_info() const { return debug_info_.c_str(); }
    bool is_wintab_loaded() const { return loader_.is_loaded(); }

private:
    // ── Wintab state ────────────────────────────────────────────
    WintabLoader loader_;
    ContextGuard context_;
    bool use_digitizer_ = false;

    // Set once at start and never cleared. use_digitizer_ flips to false when the hi-res
    // context fails to open, and the point's source has to keep naming the session that is
    // running rather than the resolution it ended up with.
    bool requested_digitizer_ = false;
    // ── Capture region ──────────────────────────────────────────
    //
    // Read on the pump thread, written from the caller's, so it is guarded. The window
    // rectangle is queried per point rather than cached, which is how the managed
    // WindowRegion does it, and is what makes a moved window followed for free.
    enum class RegionMode { Window, Rect, Unbounded };

    mutable std::mutex region_mutex_;
    RegionMode region_mode_ = RegionMode::Unbounded;
    HWND region_hwnd_ = nullptr;
    RECT region_rect_ = {};

    // True when a point at this desktop position should be reported. Fails open: an unknown
    // window or a failed GetWindowRect reports the point rather than dropping it, matching
    // PenCaptureRegion.Window on the managed side.
    bool region_contains(double x, double y) const;
    int max_pressure_ = 0;

    // Cached system mapping for digitizer ScaleAxis conversion
    int32_t map_in_org_x_ = 0, map_in_org_y_ = 0;
    int32_t map_in_ext_x_ = 0, map_in_ext_y_ = 0;
    int32_t map_sys_org_x_ = 0, map_sys_org_y_ = 0;
    int32_t map_sys_ext_x_ = 0, map_sys_ext_y_ = 0;

    // ── Message pump thread ─────────────────────────────────────
    std::thread pump_thread_;
    std::atomic<bool> running_{false};
    std::atomic<HWND> pump_hwnd_{nullptr};

    void pump_thread_func();
    void on_packet(WPARAM serial);
    static LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);

    // ── Thread-safe output ──────────────────────────────────────
    std::mutex points_mutex_;
    std::vector<PenPoint> points_;
    std::atomic<bool> has_new_data_{false};

    // Button/cursor change tracking for logging
    // pkTime is a uint32 of milliseconds, so it wraps to zero after about 49.7 days of
    // uptime. Left alone, a stroke drawn across that boundary yields a difference wrong by the
    // whole range -- silently, which is worse than no timestamp at all. These extend it.
    // Detection is a backward jump of more than half the range; pen packets arrive
    // milliseconds apart, so nothing legitimate moves backward.
    int64_t  pk_time_high_ = 0;
    int64_t  pk_time_last_ = -1;

    uint32_t last_buttons_ = 0;
    uint32_t last_cursor_ = 0;

    // ── Debug ───────────────────────────────────────────────────
    std::string debug_info_;

    // ── Context creation ────────────────────────────────────────
    bool open_system_context();
    const char* open_digitizer_hires();
    void cache_system_mapping(const LOGCONTEXTA& lc);
    void log_context(const char* label, const LOGCONTEXTA& lc);

    // ── Query helpers ───────────────────────────────────────────
    bool get_default_sys_context(LOGCONTEXTA& lc);
    int  get_max_pressure_from_driver();
};

} // namespace wintab
