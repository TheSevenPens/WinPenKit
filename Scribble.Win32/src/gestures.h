// Which Windows system gestures and pen feedback a window opts out of.
//
// Windows asks a window with WM_TABLET_QUERYSYSTEMGESTURESTATUS which system gestures it wants,
// and the window answers with a mask of the ones to switch off. By default a window receives all
// of them. A reimplementation of WinPenKit.TabletGestures rather than a binding to it, for the
// same reason selftest.h is: the native samples have no managed runtime under them. The values,
// the environment variable and the check id (L0.gesture-query, in selftest.h) are the same.
//
// A drawing surface wants none of three things: the press-and-hold gesture (a held pen becomes a
// right click, after a ring), the circle Windows draws when a pen button goes down, and the wave
// it draws on pen up. Values and descriptions are from Microsoft's page for the message
// (tpcshrd.h): TABLET_DISABLE_PRESSANDHOLD 0x1, TABLET_DISABLE_PENTAPFEEDBACK 0x8 ("UI feedback
// on pen up (waves)") and TABLET_DISABLE_PENBARRELFEEDBACK 0x10 ("UI feedback on pen button down
// (circle)").
//
// The answer can be overridden from the environment (WINPENKIT_TABLET_GESTURE_FLAGS,
// hexadecimal) so each flag can be tried alone; 0 answers "disable nothing", Windows' default and
// the baseline for measuring what Windows does. The number of times Windows has asked is kept,
// because whether the window is asked at all is the first thing to know when a ring does not go
// away.
//
// Shared the way selftest.h is: Scribble.Win32 answers in its window procedure, Scribble.Qt in
// nativeEvent, Scribble.WinUINative in a window subclass. Header-only; needs nothing but
// <windows.h>.

#pragma once

#include <windows.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <string>

namespace gestures {

// WM_TABLET_QUERYSYSTEMGESTURESTATUS = WM_TABLET_DEFBASE (0x02C0) + 12.
constexpr UINT kQueryMessage = 0x02CC;

constexpr unsigned kDisablePressAndHold = 0x00000001;
constexpr unsigned kDisablePenTapFeedback = 0x00000008;
constexpr unsigned kDisablePenBarrelFeedback = 0x00000010;

// What the window answers unless overridden.
constexpr unsigned kDefault = kDisablePressAndHold | kDisablePenTapFeedback | kDisablePenBarrelFeedback;

constexpr const char* kOverrideVariable = "WINPENKIT_TABLET_GESTURE_FLAGS";

// Hexadecimal text (an optional 0x first) to a mask. Anything that is not hexadecimal, or is
// blank, gives `fallback`, so a typo never silently switches the flags off.
inline unsigned parse_override(const char* text, unsigned fallback) {
    if (!text) return fallback;
    while (*text == ' ' || *text == '\t') ++text;
    if (text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) text += 2;
    if (*text == '\0') return fallback;
    unsigned long long value = 0;
    const char* p = text;
    for (; *p && *p != ' ' && *p != '\t'; ++p) {
        int digit;
        if (*p >= '0' && *p <= '9') digit = *p - '0';
        else if (*p >= 'a' && *p <= 'f') digit = *p - 'a' + 10;
        else if (*p >= 'A' && *p <= 'F') digit = *p - 'A' + 10;
        else return fallback;
        value = value * 16 + static_cast<unsigned>(digit);
        if (value > 0xFFFFFFFFull) return fallback;
    }
    while (*p == ' ' || *p == '\t') ++p;
    return *p == '\0' ? static_cast<unsigned>(value) : fallback;
}

// The mask the window answers with.
inline unsigned flags() {
    static const unsigned value = [] {
        char buf[64];
        DWORD n = GetEnvironmentVariableA(kOverrideVariable, buf, sizeof(buf));
        return parse_override(n > 0 && n < sizeof(buf) ? buf : nullptr, kDefault);
    }();
    return value;
}

inline std::atomic<long long>& query_counter() {
    static std::atomic<long long> count{0};
    return count;
}

// How many times Windows has asked this process's windows which gestures they want.
inline long long queries() { return query_counter().load(); }

// The decision: true (with the answer in *result) for the gesture query, which is counted; false
// for every other message. The mask is a parameter so it can be tested.
inline bool answer(UINT msg, unsigned mask, LRESULT* result) {
    if (msg != kQueryMessage) return false;
    query_counter().fetch_add(1);
    *result = static_cast<LRESULT>(mask);
    return true;
}

// Text for a window title: how often Windows has asked and what the window answers.
inline std::string title_suffix() {
    char buf[96];
    std::snprintf(buf, sizeof(buf), "gesture queries %lld, answer 0x%X", queries(), flags());
    return buf;
}

// The cursor number with its name for the two known values ("13 (pen)", "14 (eraser)"); any
// other number is returned bare, because a guessed name would read as a measurement.
inline std::string describe_cursor(unsigned cursor) {
    char buf[32];
    if (cursor == 13) std::snprintf(buf, sizeof(buf), "%u (pen)", cursor);
    else if (cursor == 14) std::snprintf(buf, sizeof(buf), "%u (eraser)", cursor);
    else std::snprintf(buf, sizeof(buf), "%u", cursor);
    return buf;
}

} // namespace gestures
