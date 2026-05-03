// Unified Pen Session C ABI exports.
// Bridges pen_session.h to the internal WintabSessionImpl and WmPointerSessionImpl.
// The factory creates sessions pre-configured for a specific API.

#include "pen_session.h"
#include "wintab_session_impl.h"
#include "wm_pointer_session_impl.h"

using wintab::WintabSessionImpl;
using wintab::WmPointerSessionImpl;
using wintab::Logger;

// ── Internal wrapper ────────────────────────────────────────────
// Holds either a Wintab or WM_POINTER session, dispatched by api tag.

struct PenSessionOpaque {
    PenInputApi api;
    int capabilities;
    WintabSessionImpl* wintab = nullptr;
    WmPointerSessionImpl* pointer = nullptr;

    ~PenSessionOpaque() {
        delete wintab;
        delete pointer;
    }
};

// ── Discovery ───────────────────────────────────────────────────

extern "C" {

int pen_session_get_available_apis(PenInputApi* buffer, int max_count) {
    int count = 0;

    // Check Wintab.
    {
        WintabSessionImpl probe;
        if (probe.is_wintab_loaded()) {
            if (buffer && count < max_count) buffer[count] = PEN_API_WINTAB_SYSTEM;
            count++;
            if (buffer && count < max_count) buffer[count] = PEN_API_WINTAB_DIGITIZER;
            count++;
        }
    }

    // Check WM_POINTER.
    if (WmPointerSessionImpl::is_available()) {
        if (buffer && count < max_count) buffer[count] = PEN_API_WM_POINTER;
        count++;
    }

    return count;
}

// ── Factory ─────────────────────────────────────────────────────

PenSessionHandle pen_session_create(PenInputApi api) {
    auto* s = new (std::nothrow) PenSessionOpaque();
    if (!s) return nullptr;

    s->api = api;

    switch (api) {
    case PEN_API_WINTAB_SYSTEM:
        s->wintab = new (std::nothrow) WintabSessionImpl();
        s->capabilities = PEN_CAP_PRESSURE | PEN_CAP_TILT | PEN_CAP_TWIST |
                          PEN_CAP_ZHEIGHT | PEN_CAP_BUTTONS | PEN_CAP_ERASER;
        break;

    case PEN_API_WINTAB_DIGITIZER:
        s->wintab = new (std::nothrow) WintabSessionImpl();
        s->capabilities = PEN_CAP_PRESSURE | PEN_CAP_TILT | PEN_CAP_TWIST |
                          PEN_CAP_ZHEIGHT | PEN_CAP_BUTTONS | PEN_CAP_ERASER |
                          PEN_CAP_HIRES;
        break;

    case PEN_API_WM_POINTER:
        s->pointer = new (std::nothrow) WmPointerSessionImpl();
        s->capabilities = PEN_CAP_PRESSURE | PEN_CAP_TILT | PEN_CAP_BUTTONS |
                          PEN_CAP_ERASER;
        break;

    default:
        delete s;
        return nullptr;
    }

    return reinterpret_cast<PenSessionHandle>(s);
}

PenSessionHandle pen_session_create_default(void) {
    PenInputApi apis[8];
    int count = pen_session_get_available_apis(apis, 8);

    for (int i = 0; i < count; i++)
        if (apis[i] == PEN_API_WINTAB_DIGITIZER)
            return pen_session_create(PEN_API_WINTAB_DIGITIZER);
    for (int i = 0; i < count; i++)
        if (apis[i] == PEN_API_WINTAB_SYSTEM)
            return pen_session_create(PEN_API_WINTAB_SYSTEM);
    for (int i = 0; i < count; i++)
        if (apis[i] == PEN_API_WM_POINTER)
            return pen_session_create(PEN_API_WM_POINTER);

    return nullptr;
}

// ── Lifecycle ───────────────────────────────────────────────────

const char* pen_session_start(PenSessionHandle handle, void* app_hwnd) {
    if (!handle) return "Invalid session handle.";
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);

    if (s->wintab) {
        WintabResolution res = (s->api == PEN_API_WINTAB_DIGITIZER)
            ? WINTAB_RESOLUTION_DIGITIZER
            : WINTAB_RESOLUTION_SCREEN;
        return s->wintab->start(res);
    }

    if (s->pointer) {
        return s->pointer->start(static_cast<HWND>(app_hwnd));
    }

    return "No backend initialized.";
}

void pen_session_stop(PenSessionHandle handle) {
    if (!handle) return;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) s->wintab->stop();
    if (s->pointer) s->pointer->stop();
}

void pen_session_destroy(PenSessionHandle handle) {
    if (!handle) return;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    delete s;
}

// ── Output ──────────────────────────────────────────────────────

int pen_session_drain_points(PenSessionHandle handle, PenPoint* buffer, int max_points) {
    if (!handle || !buffer || max_points <= 0) return 0;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) return s->wintab->drain_points(buffer, max_points);
    if (s->pointer) return s->pointer->drain_points(buffer, max_points);
    return 0;
}

int pen_session_has_new_data(PenSessionHandle handle) {
    if (!handle) return 0;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) return s->wintab->has_new_data() ? 1 : 0;
    if (s->pointer) return s->pointer->has_new_data() ? 1 : 0;
    return 0;
}

// ── Properties ──────────────────────────────────────────────────

int pen_session_get_max_pressure(PenSessionHandle handle) {
    if (!handle) return 0;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) return s->wintab->max_pressure();
    if (s->pointer) return s->pointer->max_pressure();
    return 0;
}

int pen_session_is_running(PenSessionHandle handle) {
    if (!handle) return 0;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) return s->wintab->is_running() ? 1 : 0;
    if (s->pointer) return s->pointer->is_running() ? 1 : 0;
    return 0;
}

PenInputApi pen_session_get_api(PenSessionHandle handle) {
    if (!handle) return PEN_API_WINTAB_SYSTEM;
    return reinterpret_cast<PenSessionOpaque*>(handle)->api;
}

int pen_session_get_capabilities(PenSessionHandle handle) {
    if (!handle) return PEN_CAP_NONE;
    return reinterpret_cast<PenSessionOpaque*>(handle)->capabilities;
}

const char* pen_session_get_debug_info(PenSessionHandle handle) {
    if (!handle) return "";
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) return s->wintab->debug_info();
    if (s->pointer) return s->pointer->debug_info();
    return "";
}

// ── Mapping ─────────────────────────────────────────────────────

void pen_session_refresh_mapping(PenSessionHandle handle) {
    if (!handle) return;
    auto* s = reinterpret_cast<PenSessionOpaque*>(handle);
    if (s->wintab) s->wintab->refresh_mapping();
    if (s->pointer) s->pointer->refresh_mapping();
}

// ── Diagnostics ─────────────────────────────────────────────────

const char* pen_session_get_log_path(void) {
    static std::string path = Logger::get_log_path();
    return path.c_str();
}

} // extern "C"
