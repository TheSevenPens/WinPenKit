#pragma once

// Unified Pen Session C API
//
// API-neutral abstraction over pen input backends (Wintab, WM_POINTER, etc.).
// Consumers create a session for a specific input API via the factory,
// then poll for PenPoints — same interface regardless of backend.
//
// Usage:
//   PenInputApi apis[8];
//   int count = pen_session_get_available_apis(apis, 8);
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
    PEN_CAP_ERASER   = 1 << 6,
    // Desktop-wide capture: the session can report points anywhere on screen, not only over
    // the application window. Wintab only.
    PEN_CAP_GLOBAL_CAPTURE = 1 << 7,
    PEN_CAP_PROXIMITY = 1 << 8
} PenCapabilities;

// ── PenPoint ────────────────────────────────────────────────────
//
// Universal pen data record. Desktop coordinates are in physical
// screen pixels (double for sub-pixel precision in digitizer mode).
// All orientation fields are in degrees (double).
//
// IMPORTANT: pkContext in the Wintab PACKET struct is HCTX (pointer-sized).
// See HOW_TO_USE.md gotcha #10 for why this matters for struct layout.

typedef struct {
    double   desktop_x;
    double   desktop_y;
    // Raw position in the units pen_session_get_conventions reports: tablet units from a
    // Wintab digitizer context, screen pixels from a Wintab system context or a digitizer
    // that fell back, hundredths of a millimetre from WM_POINTER (ptHimetricLocationRaw).
    // A diagnostic, not a position.
    int32_t  raw_x;
    int32_t  raw_y;
    uint32_t pressure;
    double   azimuth;     // spherical: degrees (0.0-360.0), clockwise from north
    double   altitude;    // spherical: degrees (0.0-90.0), 0=flat, 90=vertical
    double   twist;       // barrel rotation: degrees (0.0-360.0)
    double   tilt_x;      // planar: degrees (-90.0 to +90.0), positive = tilt right
    double   tilt_y;      // planar: degrees (-90.0 to +90.0), positive = tilt toward user
    int32_t  z;
    uint32_t status;
    uint32_t buttons;
    uint32_t cursor;
    int32_t  source;      // PenInputApi that produced this point
} PenPoint;

// ── Discovery ───────────────────────────────────────────────────

// Returns the number of available APIs and fills the provided buffer.
// Pass NULL to just get the count.
//
// Only the framework-agnostic APIs are ever reported. The four managed-only values
// exist in PenInputApi because a PenPoint's source field can carry them, not because
// this binding can create one.
PEN_API int pen_session_get_available_apis(PenInputApi* buffer, int max_count);

// The short name to show for an API in a dropdown, as a static ASCII string that the
// caller does not free. Never NULL: an unrecognised value returns "Unknown".
//
// Here so that one spelling of "Wintab (high-res)" serves every binding. It was six
// independent spellings across C#, C++ and Rust before this existed, which is the same
// fault pen_session_get_conventions fixed for units.
PEN_API const char* pen_session_get_api_label(PenInputApi api);

// ── Factory ─────────────────────────────────────────────────────

// Creates a session for the specified input API.
// Returns NULL if the API is not available.
PEN_API PenSessionHandle pen_session_create(PenInputApi api);

// Creates a session using the best available API.
// Prefers digitizer hi-res, then system, then WM_POINTER.
PEN_API PenSessionHandle pen_session_create_default(void);

// ── Lifecycle ───────────────────────────────────────────────────

// Starts the session. Returns NULL on success, or a static error string.
//
// app_hwnd: the application window handle.
//
//   WM_POINTER  required. The session subclasses this window to intercept
//               pointer messages.
//   Wintab      optional, and it sets the default capture region. Wintab reads
//               the whole desktop, so a session given a window reports only
//               points over that window, and a session given NULL reports the
//               pen anywhere on screen -- including over other applications.
//               Wintab still creates its own hidden pump window either way.
//
// Passing a window matches the managed binding, where a null CaptureRegion means
// window-scoped. Passing NULL keeps the desktop-wide behaviour this function had
// before the region existed.
PEN_API const char* pen_session_start(PenSessionHandle handle, void* app_hwnd);

// Stops the session (closes context, stops producing points).
PEN_API void pen_session_stop(PenSessionHandle handle);

// ── Conventions ─────────────────────────────────────────────────
//
// Some PenPoint fields mean different things depending on which backend filled
// them in. These say which convention is in force, so a consumer does not have
// to infer it from pen_session_get_api.
//
// Values match the managed WinPenKit enums one for one, so a number means the
// same thing on both surfaces.

typedef enum {
    PEN_RAW_NONE          = 0,  // no device-native position; raw_x and raw_y are 0
    PEN_RAW_TABLET_NATIVE = 1,  // the tablet's own coordinate space
    PEN_RAW_SCREEN_PIXELS = 2,  // physical screen pixels, mapped by the driver
    PEN_RAW_HIMETRIC      = 3   // hundredths of a millimetre
} PenRawUnits;

typedef enum {
    // One event per packet, (action << 16) | buttonNumber. Packets with no event
    // carry 0 and state is held between them.
    PEN_BUTTONS_WINTAB_EVENT  = 0,
    // A bitmask replaced every packet: bit 0 barrel, bit 1 eraser. No per-button
    // identity.
    PEN_BUTTONS_POINTER_FLAGS = 1
} PenButtonEncoding;

typedef enum {
    PEN_CURSOR_NORMALISED      = 0,  // 13 tip, 14 eraser, written by the session
    PEN_CURSOR_DEVICE_ASSIGNED = 1   // the driver's own number, passed through
} PenCursorNumbering;

typedef struct {
    PenRawUnits        raw_units;
    PenButtonEncoding  buttons;
    PenCursorNumbering cursor;
} PenConventions;

// Fills out with what this session's points mean. A null handle yields
// PEN_RAW_NONE and the Wintab encodings, which is what an unusable session
// reports rather than a claim about a device.
//
// Call after pen_session_start: a digitizer whose hi-res context failed reports
// PEN_RAW_SCREEN_PIXELS, and that is not known until the context is opened.
PEN_API void pen_session_get_conventions(PenSessionHandle handle, PenConventions* out);

// ── Capture region (Wintab only) ────────────────────────────────
//
// Which part of the desktop a Wintab session reports points from. WM_POINTER
// sessions are scoped to the window they subclass and ignore these.
//
// The default is the window passed to pen_session_start, or unbounded when that
// was NULL. The window rectangle is read live, so moving or resizing the window
// is followed without another call.

// Report only points over this window. NULL means unbounded.
PEN_API void pen_session_set_capture_window(PenSessionHandle handle, void* hwnd);

// Report only points inside this desktop rectangle, in physical screen pixels.
// Left and top are inclusive, right and bottom exclusive.
PEN_API void pen_session_set_capture_rect(PenSessionHandle handle,
                                          int left, int top, int right, int bottom);

// Report points anywhere on the desktop.
PEN_API void pen_session_set_capture_unbounded(PenSessionHandle handle);

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

// ── Focus ───────────────────────────────────────────────────────

// Tells the session that the application window has just been activated.
// Call from the real window's WM_ACTIVATE handler.
//
// Only Wintab needs this, and it needs it badly. Wintab contexts sit in an
// overlap order and the driver delivers packets to whichever one is on top;
// when another application takes focus yours drops down that order and nothing
// puts it back. The symptom is that the first stroke after returning to the app
// is silently swallowed while every stroke after it draws normally.
//
// The notification has to come from the application: a Wintab context is bound
// to a hidden pump window that never sees WM_ACTIVATE itself.
//
// A no-op for WM_POINTER sessions, which have Windows routing input by window.
PEN_API void pen_session_on_activated(PenSessionHandle handle);

// ── Diagnostics ─────────────────────────────────────────────────

PEN_API const char* pen_session_get_log_path(void);

#ifdef __cplusplus
}
#endif
