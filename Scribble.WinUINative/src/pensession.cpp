#include "pch.h"
#include "pensession.h"

namespace scribble {

HWND inputTargetWindow(HWND topLevel) {
    if (!topLevel) return nullptr;

    struct Found { HWND hwnd = nullptr; } found;

    ::EnumChildWindows(topLevel, [](HWND child, LPARAM param) -> BOOL {
        wchar_t cls[128]{};
        ::GetClassNameW(child, cls, 128);
        if (wcscmp(cls, L"InputSiteWindowClass") == 0) {
            reinterpret_cast<Found*>(param)->hwnd = child;
            return FALSE;   // stop; there is only one
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&found));

    return found.hwnd ? found.hwnd : topLevel;
}

std::string PenSession::start(PenInputApi api, HWND hwnd) {
    stop();

    handle_ = pen_session_create(api);
    if (!handle_) return "this API is not available on this machine";

    if (const char* err = pen_session_start(handle_, hwnd)) {
        pen_session_destroy(handle_);
        handle_ = nullptr;
        return err;
    }

    api_ = api;

    // Read once after start, not inferred from the API. A digitizer session whose hi-res
    // context failed reports screen pixels, and that is not known until the context is open.
    pen_session_get_conventions(handle_, &conventions_);

    maxPressure_ = pen_session_get_max_pressure(handle_);
    if (maxPressure_ <= 0) maxPressure_ = 1;   // never divide by what the driver did not say

    tip_ = barrel1_ = barrel2_ = barrel3_ = false;
    lastRawButtons_ = 0;
    return {};
}

void PenSession::stop() {
    if (!handle_) return;
    pen_session_stop(handle_);
    pen_session_destroy(handle_);
    handle_ = nullptr;
}

const char* PenSession::apiLabel() const {
    return pen_session_get_api_label(api_);
}

int PenSession::drain(PenPoint* out, int max) {
    if (!handle_ || !out || max <= 0) return 0;
    return pen_session_drain_points(handle_, out, max);
}

void PenSession::applyButtons(const PenPoint& pt) {
    if (conventions_.buttons == PEN_BUTTONS_WINTAB_EVENT) {
        // One event per packet: (action << 16) | buttonNumber, with 2 pressed and 1 released.
        // Packets carrying no event are 0 and state is held between them, so this must not
        // clear anything on a zero.
        const uint32_t action = (pt.buttons >> 16) & 0xFFFF;
        const uint32_t number = pt.buttons & 0xFFFF;
        if (action == 2 || action == 1) {
            const bool down = action == 2;
            switch (number) {
                case 0: tip_     = down; break;
                case 1: barrel1_ = down; break;
                case 2: barrel2_ = down; break;
                case 3: barrel3_ = down; break;
                default: break;
            }
        }
    } else {
        // A bitmask replaced every packet: bit 0 barrel, bit 1 eraser. No per-button identity,
        // so a second or third side switch cannot be told from the first -- B2 and B3 are
        // false because the backend cannot say, not because they are up.
        barrel1_ = (pt.buttons & 0x0001) != 0;
        barrel2_ = false;
        barrel3_ = false;
        // Tip comes from pressure, which is the one tip signal every backend agrees on.
        tip_ = pt.pressure > 0;
    }

    if (pt.buttons != 0) lastRawButtons_ = pt.buttons;
}

} // namespace scribble
