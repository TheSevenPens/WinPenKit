// Machine-checkable acceptance checks, and the report format they print in.
//
// Deliberately a reimplementation of WinPenKit.Diagnostics.SelfTest rather than a binding to
// it: the whole point of the native sample is that it has no managed runtime under it. The
// check ids, the line format and the exit code are identical, so a script or an agent reads
// this sample the same way it reads the others.
//
// Level 0 is the environment and level 1 the drawing surface. Neither needs a tablet, a pen,
// or a person, which is what makes them worth automating. Levels 2 and 3 - the pen stream and
// the coordinate conversion - need input, so they are not part of a launch-time self test.
//
// Most of level 1 is close to tautological here, and that is worth knowing rather than
// mistaking for a strong pass: Win32 lays out in physical pixels, so a canvas cannot be sized
// in the wrong unit and a client origin is always whole. The checks that catch real bugs in
// the scaled frameworks are structurally unable to fail in this one.

#pragma once

#include <windows.h>
#include <shellapi.h>   // CommandLineToArgvW
#include <cstdio>
#include <string>
#include <vector>

namespace selftest {

struct Result {
    std::string id;
    bool pass;
    std::string detail;
};

class Report {
public:
    explicit Report(std::string app_name) : app_name_(std::move(app_name)) {}

    // `detail` is recorded whether the check passed or failed. A passing check that prints
    // nothing is indistinguishable from a check that never ran.
    void check(const char* id, bool pass, const std::string& detail) {
        results_.push_back({id, pass, detail});
    }

    void skip(const char* id, const std::string& why) {
        results_.push_back({id, false, "could not run: " + why});
    }

    bool all_passed() const {
        if (results_.empty()) return false;
        for (const auto& r : results_) if (!r.pass) return false;
        return true;
    }

    std::string format() const {
        size_t width = 0;
        for (const auto& r : results_) if (r.id.size() > width) width = r.id.size();

        std::string out = "SELFTEST " + app_name_ + "\n";
        int passed = 0;
        for (const auto& r : results_) {
            if (r.pass) passed++;
            std::string id = r.id;
            id.resize(width, ' ');
            out += std::string("[") + (r.pass ? "PASS" : "FAIL") + "] " + id + "  " + r.detail + "\n";
        }
        out += "RESULT " + std::to_string(passed) + "/" +
               std::to_string(results_.size()) + " passed\n";
        return out;
    }

    // Prints the report and returns the process exit code: 0 only when everything passed.
    // A GUI subsystem process has no console of its own, so attach to the parent's when there
    // is one - otherwise running this from a terminal succeeds silently with nothing to read.
    int emit() const {
        std::string text = format();

        // Try the inherited handle first. When stdout is a pipe or a file - which is how a
        // script or an agent will run this - it is already valid and AttachConsole would not
        // help. Only fall back to attaching when there is no handle at all, which is the
        // case for a launch straight from a terminal.
        if (!write_to(GetStdHandle(STD_OUTPUT_HANDLE), text)) {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
                write_to(GetStdHandle(STD_OUTPUT_HANDLE), text);
            else
                OutputDebugStringA(text.c_str());
        }
        return all_passed() ? 0 : 1;
    }

private:
    static bool write_to(HANDLE h, const std::string& text) {
        if (!h || h == INVALID_HANDLE_VALUE) return false;
        DWORD written = 0;
        return WriteFile(h, text.c_str(), (DWORD)text.size(), &written, nullptr) != 0 &&
               written == text.size();
    }

public:

    // ── Level 0: environment ─────────────────────────────────────

    // Anything short of Per-Monitor V2 and Win32 hands back virtualized coordinates that do
    // not match what the pen reports, so every later measurement is of the wrong thing.
    void check_dpi_awareness() {
        DPI_AWARENESS_CONTEXT ctx = GetThreadDpiAwarenessContext();
        if (!ctx) { skip("L0.dpi-awareness", "GetThreadDpiAwarenessContext returned null"); return; }

        bool v2 = AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        const char* name =
            v2 ? "PerMonitorV2"
            : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) ? "PerMonitor (not V2)"
            : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE) ? "System"
            : AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_UNAWARE) ? "Unaware"
            : "unrecognised";
        check("L0.dpi-awareness", v2, name);
    }

    // Pen input is delivered by absolute screen position, so any part of the window off its
    // monitor - or under the taskbar - receives nothing while the window keeps running and
    // painting, which reads as a bug in whatever is being tested.
    void check_window_placement(HWND hwnd) {
        if (!hwnd) { skip("L0.window-placement", "no window handle"); return; }

        RECT client{};
        if (!GetClientRect(hwnd, &client)) { skip("L0.window-placement", "GetClientRect failed"); return; }

        POINT origin{0, 0};
        if (!ClientToScreen(hwnd, &origin)) { skip("L0.window-placement", "ClientToScreen failed"); return; }

        HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi{sizeof(MONITORINFO)};
        if (!GetMonitorInfoW(mon, &mi)) { skip("L0.window-placement", "GetMonitorInfo failed"); return; }

        int w = client.right - client.left, h = client.bottom - client.top;
        const RECT& wa = mi.rcWork;
        bool inside = origin.x >= wa.left && origin.y >= wa.top &&
                      origin.x + w <= wa.right && origin.y + h <= wa.bottom;

        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "client %dx%d at %ld,%ld; work area %ldx%ld at %ld,%ld%s",
            w, h, origin.x, origin.y,
            wa.right - wa.left, wa.bottom - wa.top, wa.left, wa.top,
            inside ? "" : "  <- input outside the work area is silently discarded");
        check("L0.window-placement", inside, buf);
    }

    void report_scale(double scale) {
        char buf[64];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%.2fx", scale);
        check("L0.scale", scale > 0, buf);
    }

    // ── Level 1: surface ─────────────────────────────────────────

    // `scale` is the ratio of the framework's layout unit to a device pixel, which is not
    // always the display scale. Win32 lays out in physical pixels, so it is 1.0 here even at
    // 2.25x; WPF lays out in DIPs, where it is the display scale. Conflating the two makes
    // this check pass on a broken surface.
    void check_surface_physical(int bitmap_w, int bitmap_h,
                                double logical_w, double logical_h, double scale) {
        int want_w = (int)(logical_w * scale + 0.999999);
        int want_h = (int)(logical_h * scale + 0.999999);
        bool ok = bitmap_w == want_w && bitmap_h == want_h;

        char buf[320];
        char tail[96] = "";
        if (!ok && want_w > 0)
            _snprintf_s(tail, sizeof(tail), _TRUNCATE,
                        "  <- rendering at %.0f%% of display resolution",
                        100.0 * bitmap_w / want_w);

        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "bitmap %dx%d, expected %dx%d (= ceil(%.0fx%.0f logical x %.2f))%s",
            bitmap_w, bitmap_h, want_w, want_h, logical_w, logical_h, scale, tail);
        check("L1.surface-physical", ok, buf);
    }

    // A fractional offset makes the surface get resampled to draw it between pixel rows,
    // softening every edge at once while coordinates and resolution both still measure
    // correct. Both axes are reported separately because the error is routinely
    // one-dimensional.
    void check_surface_alignment(double origin_x, double origin_y) {
        double fx = origin_x - (double)(long long)(origin_x + 0.5);
        double fy = origin_y - (double)(long long)(origin_y + 0.5);
        if (fx < 0) fx = -fx;
        if (fy < 0) fy = -fy;
        bool ok = fx < 0.01 && fy < 0.01;

        const char* which = ok ? "" :
            (fx >= 0.01 && fy >= 0.01) ? "  <- fractional on both axes; the whole surface is resampled to draw it"
            : (fx >= 0.01) ? "  <- fractional on x; the whole surface is resampled to draw it"
            : "  <- fractional on y; the whole surface is resampled to draw it";

        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE, "origin %.2f,%.2fpx%s", origin_x, origin_y, which);
        check("L1.surface-alignment", ok, buf);
    }

    // Presenting a correctly sized surface into a differently sized rect scales it back off
    // the pixel grid, which undoes the point of sizing it physically.
    void check_presentation_1to1(int bitmap_w, int bitmap_h,
                                 double presented_w, double presented_h) {
        double dw = presented_w - bitmap_w, dh = presented_h - bitmap_h;
        if (dw < 0) dw = -dw;
        if (dh < 0) dh = -dh;
        bool ok = dw < 0.5 && dh < 0.5;

        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "bitmap %dx%d presented at %.1fx%.1f device px%s",
            bitmap_w, bitmap_h, presented_w, presented_h,
            ok ? "" : "  <- magnified or shrunk on the way to the screen");
        check("L1.presentation-1to1", ok, buf);
    }

private:
    std::string app_name_;
    std::vector<Result> results_;
};

// Whether the command line asks for a self test.
inline bool requested() {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool found = false;
    for (int i = 1; i < argc; i++)
        if (_wcsicmp(argv[i], L"--selftest") == 0) { found = true; break; }
    LocalFree(argv);
    return found;
}

}  // namespace selftest
