#include "wintab_session_impl.h"
#include <cstdio>
#include <cmath>
#include <algorithm>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

namespace wintab {

// ── Window class name for the hidden message pump window ────────

static constexpr const wchar_t* PUMP_CLASS_NAME = L"WintabSessionPumpWindow";
static constexpr UINT WM_PUMP_QUIT = WM_USER + 1;

// ── Construction / Destruction ──────────────────────────────────

WintabSessionImpl::WintabSessionImpl() {
    if (!loader_.load()) return;
    max_pressure_ = get_max_pressure_from_driver();
}

WintabSessionImpl::~WintabSessionImpl() {
    stop();
}

// ── Query helpers ───────────────────────────────────────────────

bool WintabSessionImpl::get_default_sys_context(LOGCONTEXTA& lc) {
    memset(&lc, 0, sizeof(lc));
    UINT size = loader_.wt_info(WTI_DEFSYSCTX, 0, &lc);
    return size > 0;
}

int WintabSessionImpl::get_max_pressure_from_driver() {
    AXIS pressure_axis = {};
    UINT size = loader_.wt_info(WTI_DEVICES, DVC_NPRESSURE, &pressure_axis);
    if (size > 0) return pressure_axis.axMax;
    return 0;
}

// ── Lifecycle ───────────────────────────────────────────────────

const char* WintabSessionImpl::start(WintabResolution resolution) {
    if (!loader_.is_loaded())
        return "Wintab not found. Is the tablet driver installed?";

    use_digitizer_ = (resolution == WINTAB_RESOLUTION_DIGITIZER);

    // Start the message pump thread FIRST — we need its HWND for WTOpen.
    running_ = true;
    pump_hwnd_ = nullptr;
    pump_thread_ = std::thread(&WintabSessionImpl::pump_thread_func, this);

    // Wait for the pump window to be created.
    while (pump_hwnd_.load() == nullptr && running_.load()) {
        Sleep(1);
    }

    if (!pump_hwnd_.load()) {
        stop();
        return "Failed to create message pump window.";
    }

    // Open the Wintab context on the pump window.
    const char* error = nullptr;
    if (use_digitizer_) {
        error = open_digitizer_hires();
    } else {
        if (!open_system_context())
            error = "Failed to open system context.";
    }

    if (error) {
        stop();
        return error;
    }

    return nullptr; // success
}

void WintabSessionImpl::stop() {
    // Close the Wintab context first.
    context_.close();

    // Stop the message pump thread.
    HWND hwnd = pump_hwnd_.load();
    if (hwnd) {
        PostMessageW(hwnd, WM_PUMP_QUIT, 0, 0);
    }

    if (pump_thread_.joinable()) {
        pump_thread_.join();
    }

    pump_hwnd_ = nullptr;
    running_ = false;
}

// ── Context creation ────────────────────────────────────────────

static void configure_packet_data(LOGCONTEXTA& lc) {
    // Request all standard packet fields — must match our PACKET struct layout.
    lc.lcPktData = PK_PKTBITS_ALL;
    lc.lcPktMode = 0; // absolute mode for all fields
    lc.lcMoveMask = PK_PKTBITS_ALL;
    lc.lcBtnDnMask = 0xFFFFFFFF;
    lc.lcBtnUpMask = 0xFFFFFFFF;
}

bool WintabSessionImpl::open_system_context() {
    Logger::log("=== OpenSystemContext ===");

    LOGCONTEXTA lc;
    if (!get_default_sys_context(lc)) {
        Logger::log("FAIL: WTInfo(WTI_DEFSYSCTX) failed");
        return false;
    }

    lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
    configure_packet_data(lc);
    if (lc.lcOutExtY > 0) lc.lcOutExtY = -lc.lcOutExtY;

    log_context("Before open", lc);

    HCTX ctx = loader_.wt_open(pump_hwnd_.load(), &lc, TRUE);
    if (!ctx) {
        Logger::log("System Open FAILED");
        return false;
    }

    // Read back actual values.
    loader_.wt_get(ctx, &lc);
    log_context("After open", lc);

    context_ = ContextGuard(ctx, loader_.wt_close);

    char buf[256];
    snprintf(buf, sizeof(buf), "[System] Out:%ld,%ld  Sys:%ld,%ld/%ld,%ld",
        lc.lcOutExtX, lc.lcOutExtY,
        lc.lcSysOrgX, lc.lcSysOrgY, lc.lcSysExtX, lc.lcSysExtY);
    debug_info_ = buf;

    return true;
}

const char* WintabSessionImpl::open_digitizer_hires() {
    Logger::log("=== OpenDigitizerHiRes ===");

    // Read system defaults for the mapping.
    LOGCONTEXTA sys_defaults;
    if (!get_default_sys_context(sys_defaults)) {
        Logger::log("FAIL: WTInfo(WTI_DEFSYSCTX) failed");
        return "Failed to get system context defaults.";
    }

    log_context("SysDefaults", sys_defaults);
    cache_system_mapping(sys_defaults);

    // Open with tablet-native output range.
    LOGCONTEXTA lc;
    if (!get_default_sys_context(lc)) {
        return "Failed to get context for digitizer.";
    }

    lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
    configure_packet_data(lc);
    strncpy_s(lc.lcName, "HiRes Digitizer", LCNAMELEN - 1);

    // Override output to tablet-native range.
    lc.lcOutOrgX = sys_defaults.lcInOrgX;
    lc.lcOutOrgY = sys_defaults.lcInOrgY;
    lc.lcOutExtX = sys_defaults.lcInExtX;
    lc.lcOutExtY = sys_defaults.lcInExtY;

    log_context("BeforeOpen (HiRes)", lc);

    HCTX ctx = loader_.wt_open(pump_hwnd_.load(), &lc, TRUE);
    if (!ctx) {
        Logger::log("HiRes Open FAILED — falling back to screen pixels");

        // Fallback: screen-pixel output.
        if (!get_default_sys_context(lc)) return "Failed to get fallback context.";

        lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
        configure_packet_data(lc);
        strncpy_s(lc.lcName, "Digitizer (fallback)", LCNAMELEN - 1);
        if (lc.lcOutExtY > 0) lc.lcOutExtY = -lc.lcOutExtY;

        log_context("Fallback BeforeOpen", lc);

        ctx = loader_.wt_open(pump_hwnd_.load(), &lc, TRUE);
        if (!ctx) {
            Logger::log("Fallback Open also FAILED");
            return "Fallback context also failed to open.";
        }

        loader_.wt_get(ctx, &lc);
        log_context("Fallback AfterOpen", lc);

        context_ = ContextGuard(ctx, loader_.wt_close);
        use_digitizer_ = false; // treat as system mode

        debug_info_ = "HiRes FAILED — screen-pixel fallback.";
        return nullptr;
    }

    loader_.wt_get(ctx, &lc);
    log_context("AfterOpen (HiRes)", lc);

    context_ = ContextGuard(ctx, loader_.wt_close);

    char buf[512];
    snprintf(buf, sizeof(buf),
        "[DigitizerHiRes] Mapping In:%ld,%ld -> Sys:%ld,%ld/%ld,%ld  Out:%ld,%ld",
        map_in_ext_x_, map_in_ext_y_,
        map_sys_org_x_, map_sys_org_y_, map_sys_ext_x_, map_sys_ext_y_,
        lc.lcOutExtX, lc.lcOutExtY);
    debug_info_ = buf;

    return nullptr;
}

void WintabSessionImpl::cache_system_mapping(const LOGCONTEXTA& lc) {
    map_in_org_x_  = lc.lcInOrgX;
    map_in_org_y_  = lc.lcInOrgY;
    map_in_ext_x_  = lc.lcInExtX;
    map_in_ext_y_  = lc.lcInExtY;
    map_sys_org_x_ = lc.lcSysOrgX;
    map_sys_org_y_ = lc.lcSysOrgY;
    map_sys_ext_x_ = lc.lcSysExtX;

    // Negate SysExtY: tablet origin is bottom-left (Y up),
    // screen origin is top-left (Y down).
    map_sys_ext_y_ = -std::abs(lc.lcSysExtY);
}

void WintabSessionImpl::refresh_mapping() {
    LOGCONTEXTA lc;
    if (get_default_sys_context(lc)) {
        cache_system_mapping(lc);
    }
}

// ── Output ──────────────────────────────────────────────────────

int WintabSessionImpl::drain_points(PenPoint* buffer, int max_points) {
    has_new_data_ = false;

    std::lock_guard<std::mutex> lock(points_mutex_);
    int count = std::min(max_points, static_cast<int>(points_.size()));
    if (count > 0) {
        memcpy(buffer, points_.data(), count * sizeof(PenPoint));
        points_.erase(points_.begin(), points_.begin() + count);
    }
    return count;
}

// ── Message pump thread ─────────────────────────────────────────

void WintabSessionImpl::pump_thread_func() {
    // Register window class (once per process).
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WintabSessionImpl::wnd_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = PUMP_CLASS_NAME;
    RegisterClassExW(&wc);

    // Create a hidden top-level window (NOT HWND_MESSAGE).
    // The Wacom driver doesn't deliver WT_PACKET to message-only windows.
    HWND hwnd = CreateWindowExW(
        0, PUMP_CLASS_NAME, L"", WS_OVERLAPPED,
        0, 0, 0, 0,
        nullptr, nullptr, wc.hInstance, nullptr);

    if (!hwnd) {
        Logger::log("Failed to create pump window: %lu", GetLastError());
        running_ = false;
        return;
    }

    // Store 'this' pointer so WndProc can find us.
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    pump_hwnd_ = hwnd;

    Logger::log("Pump thread started, hwnd=%p", hwnd);

    // Message loop.
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        if (msg.message == WM_PUMP_QUIT) break;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    DestroyWindow(hwnd);
    pump_hwnd_ = nullptr;
    Logger::log("Pump thread stopped");
}

LRESULT CALLBACK WintabSessionImpl::wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WT_PACKET) {
        auto* self = reinterpret_cast<WintabSessionImpl*>(
            GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (self) {
            self->on_packet(wp);
        }
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ── Packet handling ─────────────────────────────────────────────

void WintabSessionImpl::on_packet(WPARAM serial) {
    if (!context_) return;

    PACKET pkt = {};
    if (!loader_.wt_packet(context_.get(), static_cast<UINT>(serial), &pkt)) return;
    if (!pkt.pkContext) return;

    double desktop_x, desktop_y;

    if (use_digitizer_) {
        desktop_x = scale_axis(pkt.pkX,
            map_in_org_x_, map_in_ext_x_, map_sys_org_x_, map_sys_ext_x_);
        desktop_y = scale_axis(pkt.pkY,
            map_in_org_y_, map_in_ext_y_, map_sys_org_y_, map_sys_ext_y_);
    } else {
        desktop_x = static_cast<double>(pkt.pkX);
        desktop_y = static_cast<double>(pkt.pkY);
    }

    // Log button/cursor changes.
    if (pkt.pkButtons != last_buttons_ || pkt.pkCursor != last_cursor_) {
        Logger::log("Button change: 0x%08X -> 0x%08X  Cursor: %u -> %u  Pressure: %u",
            last_buttons_, pkt.pkButtons, last_cursor_, pkt.pkCursor, pkt.pkNormalPressure);
        last_buttons_ = pkt.pkButtons;
        last_cursor_ = pkt.pkCursor;
    }

    // Convert Azimuth/Altitude (spherical, Wintab tenths) → TiltX/TiltY (planar, degrees).
    double tilt_x = 0.0, tilt_y = 0.0;
    {
        double tilt_mag = 90.0 - pkt.pkOrientation.orAltitude / 10.0; // degrees from vertical
        double az_rad = pkt.pkOrientation.orAzimuth / 10.0 * M_PI / 180.0;
        tilt_x = -tilt_mag * std::sin(az_rad);
        tilt_y =  tilt_mag * std::cos(az_rad);
    }

    PenPoint pt = {};
    pt.desktop_x = desktop_x;
    pt.desktop_y = desktop_y;
    pt.raw_x     = pkt.pkX;
    pt.raw_y     = pkt.pkY;
    pt.pressure  = pkt.pkNormalPressure;
    pt.azimuth   = pkt.pkOrientation.orAzimuth / 10.0;
    pt.altitude  = pkt.pkOrientation.orAltitude / 10.0;
    pt.twist     = pkt.pkOrientation.orTwist / 10.0;
    pt.tilt_x    = tilt_x;
    pt.tilt_y    = tilt_y;
    pt.z         = pkt.pkZ;
    pt.status    = pkt.pkStatus;
    pt.buttons   = pkt.pkButtons;
    pt.cursor    = pkt.pkCursor;
    pt.source    = use_digitizer_ ? PEN_API_WINTAB_DIGITIZER : PEN_API_WINTAB_SYSTEM;

    {
        std::lock_guard<std::mutex> lock(points_mutex_);
        points_.push_back(pt);
    }

    has_new_data_ = true;
}

// ── Diagnostics ─────────────────────────────────────────────────

void WintabSessionImpl::log_context(const char* label, const LOGCONTEXTA& lc) {
    Logger::log("%s: Options=0x%08X Device=%u PktData=0x%08X MoveMask=0x%08X",
        label, lc.lcOptions, lc.lcDevice, lc.lcPktData, lc.lcMoveMask);
    Logger::log("  InOrg=(%ld,%ld) InExt=(%ld,%ld)",
        lc.lcInOrgX, lc.lcInOrgY, lc.lcInExtX, lc.lcInExtY);
    Logger::log("  OutOrg=(%ld,%ld) OutExt=(%ld,%ld)",
        lc.lcOutOrgX, lc.lcOutOrgY, lc.lcOutExtX, lc.lcOutExtY);
    Logger::log("  SysOrg=(%ld,%ld) SysExt=(%ld,%ld)",
        lc.lcSysOrgX, lc.lcSysOrgY, lc.lcSysExtX, lc.lcSysExtY);
}

} // namespace wintab
