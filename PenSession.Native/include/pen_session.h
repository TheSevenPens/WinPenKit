#pragma once

// Unified Pen Session C API
//
// API-neutral abstraction over pen input backends (Wintab, WM_POINTER, etc.).
// Consumers create a session for a specific input API via the factory,
// then poll for PenPoints — same interface regardless of backend.
//
// Usage:
//   int count = 0;
//   PenInputApi* apis = pen_session_get_available_apis(&count);
//   PenSessionHandle session = pen_session_create(apis[0]);
//   pen_session_start(session, NULL);
//   PenPoint points[64];
//   int n = pen_session_drain_points(session, points, 64);
//   pen_session_destroy(session);

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

// ── Export/import macro ─────────────────────────────────────────

#ifdef WINTAB_SESSION_BUILDING
  #define PEN_API __declspec(dllexport)
#else
  #define PEN_API __declspec(dllimport)
#endif

// ── Opaque handle ───────────────────────────────────────────────

typedef struct PenSessionOpaque* PenSessionHandle;

// ── Input API enum ──────────────────────────────────────────────

typedef enum {
    PEN_API_WINTAB_SYSTEM    = 0,  // Wintab system context (screen pixels)
    PEN_API_WINTAB_DIGITIZER = 1,  // Wintab digitizer (hi-res tablet-native)
    PEN_API_WM_POINTER       = 2,  // Windows Pointer (Win32 subclassing)
    PEN_API_WINUI_POINTER    = 3,  // WinUI 3 XAML events (managed only)
    PEN_API_WPF_STYLUS       = 4,  // WPF stylus events (managed only)
    PEN_API_AVALONIA_POINTER = 5,  // Avalonia pointer events (managed only)
    PEN_API_WINFORMS_POINTER = 6   // WinForms NativeWindow WndProc (managed only)
} PenInputApi;

// ── Capabilities flags ──────────────────────────────────────────

typedef enum {
    PEN_CAP_NONE     = 0,
    PEN_CAP_PRESSURE = 1 << 0,
    PEN_CAP_TILT     = 1 << 1,
    PEN_CAP_TWIST    = 1 << 2,
    PEN_CAP_ZHEIGHT  = 1 << 3,
    PEN_CAP_BUTTONS  = 1 << 4,
    PEN_CAP_HIRES    = 1 << 5,
    PEN_CAP_ERASER   = 1 << 6
} PenCapabilities;

// ── PenPoint ────────────────────────────────────────────────────
//
// Universal pen data record. Desktop coordinates are in physical
// screen pixels (double for sub-pixel precision in digitizer mode).
// All orientation fields are in tenths of a degree.
//
// IMPORTANT: pkContext in the Wintab PACKET struct is HCTX (pointer-sized).
// See HOW_TO_USE.md gotcha #10 for why this matters for struct layout.

typedef struct {
    double   desktop_x;
    double   desktop_y;
    int32_t  raw_x;
    int32_t  raw_y;
    uint32_t pressure;
    int32_t  azimuth;     // spherical: tenths of degree (0-3600), clockwise from north
    int32_t  altitude;    // spherical: tenths of degree (0-900), 0=flat, 900=vertical
    int32_t  twist;       // barrel rotation: tenths of degree (0-3600)
    int32_t  tilt_x;      // planar: tenths of degree (-900 to +900), positive = tilt right
    int32_t  tilt_y;      // planar: tenths of degree (-900 to +900), positive = tilt toward user
    int32_t  z;
    uint32_t status;
    uint32_t buttons;
    uint32_t cursor;
    int32_t  source;      // PenInputApi that produced this point
} PenPoint;

// ── Discovery ───────────────────────────────────────────────────

// Returns the number of available APIs and fills the provided buffer.
// Pass NULL to just get the count.
PEN_API int pen_session_get_available_apis(PenInputApi* buffer, int max_count);

// ── Factory ─────────────────────────────────────────────────────

// Creates a session for the specified input API.
// Returns NULL if the API is not available.
PEN_API PenSessionHandle pen_session_create(PenInputApi api);

// Creates a session using the best available API.
// Prefers digitizer hi-res, then system, then WM_POINTER.
PEN_API PenSessionHandle pen_session_create_default(void);

// ── Lifecycle ───────────────────────────────────────────────────

// Starts the session. Returns NULL on success, or a static error string.
// app_hwnd: the application window handle. Required for WM_POINTER sessions
// (the session subclasses this window to intercept pointer messages).
// Pass NULL for Wintab sessions (they create their own hidden pump window).
PEN_API const char* pen_session_start(PenSessionHandle handle, void* app_hwnd);

// Stops the session (closes context, stops producing points).
PEN_API void pen_session_stop(PenSessionHandle handle);

// Destroys the session and frees all resources.
PEN_API void pen_session_destroy(PenSessionHandle handle);

// ── Output ──────────────────────────────────────────────────────

// Copies up to max_points PenPoints into the buffer.
// Returns the number of points copied. Thread-safe.
PEN_API int pen_session_drain_points(PenSessionHandle handle,
    PenPoint* buffer, int max_points);

// Returns non-zero if new data is available since the last drain.
PEN_API int pen_session_has_new_data(PenSessionHandle handle);

// ── Properties ──────────────────────────────────────────────────

PEN_API int pen_session_get_max_pressure(PenSessionHandle handle);
PEN_API int pen_session_is_running(PenSessionHandle handle);
PEN_API PenInputApi pen_session_get_api(PenSessionHandle handle);
PEN_API int pen_session_get_capabilities(PenSessionHandle handle);
PEN_API const char* pen_session_get_debug_info(PenSessionHandle handle);

// ── Mapping ─────────────────────────────────────────────────────

PEN_API void pen_session_refresh_mapping(PenSessionHandle handle);

// ── Diagnostics ─────────────────────────────────────────────────

PEN_API const char* pen_session_get_log_path(void);

#ifdef __cplusplus
}
#endif
