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
#include <array>
#include <fstream>
#include <iomanip>
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <cstdio>
#include <string>
#include <vector>
#include <utility>

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

    // -- Levels 2 and 3: the replayed stroke ---------------------
    //
    // A recording holds what the session produced, so replaying it exercises everything
    // downstream of the session and nothing inside it. An app that converts perfectly can
    // still be fed pre-quantized coordinates by its own session, and no replay will show
    // that; the recording-subpixel check exists so the boundary stays visible.

    // Mean angle between consecutive segments. Quantizing a path to a pixel grid leaves only a
    // handful of directions a short segment can point in, so it stops following the pen and
    // starts zigzagging - which shows up here and is invisible to almost everything else.
    static double mean_turn_angle(const std::vector<std::pair<double, double>>& pts) {
        double sum = 0;
        int n = 0;
        for (size_t i = 1; i + 1 < pts.size(); i++) {
            double ax = pts[i].first - pts[i - 1].first;
            double ay = pts[i].second - pts[i - 1].second;
            double bx = pts[i + 1].first - pts[i].first;
            double by = pts[i + 1].second - pts[i].second;
            double na = sqrt(ax * ax + ay * ay), nb = sqrt(bx * bx + by * by);
            if (na < 1e-9 || nb < 1e-9) continue;
            double c = (ax * bx + ay * by) / (na * nb);
            if (c > 1) c = 1;
            if (c < -1) c = -1;
            sum += acos(c) * 180.0 / 3.14159265358979323846;
            n++;
        }
        return n > 0 ? sum / n : 0.0;
    }

    // Whether the data being replayed is sub-pixel at all. Without it, a clean result below
    // could mean either a lossless conversion or one that had nothing left to lose.
    void check_recording_subpixel(const std::vector<std::pair<double, double>>& input) {
        if (input.size() < 3) { skip("L2.recording-subpixel", "recording too short"); return; }
        size_t integral = 0;
        for (const auto& pt : input)
            if (fabs(pt.first - floor(pt.first + 0.5)) < 1e-9 &&
                fabs(pt.second - floor(pt.second + 0.5)) < 1e-9) integral++;

        double pct = 100.0 * integral / input.size();
        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "%.1f%% of %zu recorded points are on whole pixels%s", pct, input.size(),
            pct < 5.0 ? "" : "  <- the recording is already quantized; nothing below can fail");
        check("L2.recording-subpixel", pct < 5.0, buf);
    }

    // A legitimate pen stream essentially never lands on whole device pixels, so a high
    // percentage here means an integer-typed API somewhere in the conversion.
    void check_conversion_snap(const std::vector<std::pair<double, double>>& out, double scale) {
        if (out.empty()) { skip("L3.conversion-snap", "no converted points"); return; }
        size_t snapped = 0;
        for (const auto& pt : out)
            if (fabs(pt.first * scale - floor(pt.first * scale + 0.5)) < 1e-6 &&
                fabs(pt.second * scale - floor(pt.second * scale + 0.5)) < 1e-6) snapped++;

        double pct = 100.0 * snapped / out.size();
        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "%.1f%% of converted points land on whole device pixels%s", pct,
            pct < 5.0 ? "" : "  <- an integer-typed API is truncating the position");
        check("L3.conversion-snap", pct < 5.0, buf);
    }

    // The strongest of the three, and the only one needing no threshold. The conversion is a
    // translation and a uniform scale, both of which preserve angles exactly, so a lossless
    // implementation reproduces the input's turn angle to the decimal. A fixed number would
    // have to be calibrated against how the stroke was drawn; this calibrates itself.
    void check_conversion_lossless(const std::vector<std::pair<double, double>>& in,
                                   const std::vector<std::pair<double, double>>& out) {
        double a = mean_turn_angle(in), b = mean_turn_angle(out);
        double delta = fabs(b - a);
        bool ok = delta < 0.05;

        char buf[256];
        _snprintf_s(buf, sizeof(buf), _TRUNCATE,
            "mean turn angle in %.2f deg, out %.2f deg (delta %.2f)%s", a, b, delta,
            ok ? "" : "  <- the conversion changed the shape of the path");
        check("L3.conversion-lossless", ok, buf);
    }

    // Every other check here converts points while the window holds still, and a canvas
    // origin that is simply wrong cancels out of all of them: the replay positions its input
    // relative to the origin the application reports, then the application subtracts the same
    // value back off. L1.surface-alignment does not close the gap either, because it asks
    // whether the origin is a whole number rather than whether it is the right one.
    //
    // So move the window a known distance and convert the same desktop point again. A
    // conversion that reads the origin fresh reports a canvas position shifted by exactly
    // that distance; one that cached the origin reports what it did before, because nothing
    // told it the window moved. Scribble.Wpf shipped with exactly that fault, and every
    // other check passed throughout.
    //
    // The window is moved and put back, so run this after the checks that measure where the
    // window sits.
    template <typename Convert>
    void check_origin_tracks_window(HWND hwnd, Convert convert, double scale) {
        const char* id = "L3.origin-tracks-window";

        if (!hwnd) { skip(id, "no window handle"); return; }
        if (IsZoomed(hwnd)) { skip(id, "window is maximized; moving it would restore it"); return; }
        if (scale <= 0) { skip(id, "canvas scale not reported"); return; }

        RECT before{};
        if (!GetWindowRect(hwnd, &before)) { skip(id, "GetWindowRect failed"); return; }

        // Odd numbers, so a conversion that happens to quantize cannot match by luck.
        const int dx = 37, dy = 23;
        const UINT move_only = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;

        // One fixed desktop point. Which point does not matter: the conversion is affine, so
        // every point shifts by the same amount.
        double probe_x = before.left + 64.0, probe_y = before.top + 64.0;

        auto a = convert(probe_x, probe_y);

        if (!SetWindowPos(hwnd, nullptr, before.left + dx, before.top + dy, 0, 0, move_only)) {
            skip(id, "SetWindowPos failed");
            return;
        }

        auto b = convert(probe_x, probe_y);

        bool restored = SetWindowPos(hwnd, nullptr, before.left, before.top, 0, 0, move_only) != FALSE;

        double err_x = fabs(b.first  - (a.first  - dx / scale));
        double err_y = fabs(b.second - (a.second - dy / scale));

        // Half a device pixel: wide enough for a conversion that rounds its origin, narrow
        // enough that a cached origin cannot pass.
        double tolerance = 0.5 / scale;
        bool ok = err_x < tolerance && err_y < tolerance;

        char buf[256];
        if (ok) {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "moved %d,%dpx; conversion followed%s", dx, dy,
                restored ? "" : "  (window not restored)");
        } else {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "moved %d,%dpx; conversion off by %.2f,%.2fpx"
                "  <- the canvas origin is cached and nothing refreshes it when the window moves%s",
                dx, dy, err_x * scale, err_y * scale,
                restored ? "" : "  (window not restored)");
        }
        check(id, ok, buf);
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

// -- Replay -------------------------------------------------------

// Loads a recording: desktopX,desktopY,pressure, with # comments and a header line.
inline std::vector<std::pair<double, double>> load_recording(const std::string& path) {
    std::vector<std::pair<double, double>> pts;
    FILE* f = nullptr;
    if (path.empty() || fopen_s(&f, path.c_str(), "r") != 0 || !f) return pts;

    char line[512];
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == 35 || line[0] == 10 || line[0] == 13) continue;   // '#', LF, CR
        double x = 0, y = 0;
        if (sscanf_s(line, "%lf,%lf", &x, &y) == 2) pts.emplace_back(x, y);
    }
    fclose(f);
    return pts;
}

// Shifts a recording so it sits inside a canvas. The shift is a whole number of pixels
// deliberately: a fractional one would change every coordinate's fractional part and so change
// the very thing being measured.
inline void center_on(std::vector<std::pair<double, double>>& pts,
                      double origin_x, double origin_y, double w, double h) {
    if (pts.empty()) return;
    double min_x = pts[0].first, max_x = min_x, min_y = pts[0].second, max_y = min_y;
    for (const auto& pt : pts) {
        if (pt.first < min_x) min_x = pt.first;
        if (pt.first > max_x) max_x = pt.first;
        if (pt.second < min_y) min_y = pt.second;
        if (pt.second > max_y) max_y = pt.second;
    }
    double dx = floor(origin_x + (w - (max_x - min_x)) / 2 - min_x + 0.5);
    double dy = floor(origin_y + (h - (max_y - min_y)) / 2 - min_y + 0.5);
    for (auto& pt : pts) { pt.first += dx; pt.second += dy; }
}

// Walks up from the executable looking for the bundled reference recording.
inline std::string find_default_recording() {
    wchar_t exe[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return std::string();

    std::wstring dir(exe);
    for (int i = 0; i < 8; i++) {
        size_t slash = dir.find_last_of(L"\\/");
        if (slash == std::wstring::npos) break;
        dir = dir.substr(0, slash);
        std::wstring candidate = dir + L"\\testdata\\reference-stroke.csv";
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES) {
            char narrow[MAX_PATH * 2];
            size_t converted = 0;
            wcstombs_s(&converted, narrow, candidate.c_str(), sizeof(narrow) - 1);
            return std::string(narrow);
        }
    }
    return std::string();
}

// --replay alone uses the bundled reference stroke; --replay <path> uses your own, which is how
// a stream captured from real hardware gets checked against the same assertions.
inline bool replay_requested(std::string& path) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;

    bool found = false;
    for (int i = 1; i < argc; i++) {
        if (_wcsicmp(argv[i], L"--replay") != 0) continue;
        found = true;
        if (i + 1 < argc && wcsncmp(argv[i + 1], L"--", 2) != 0) {
            char narrow[MAX_PATH * 2];
            size_t converted = 0;
            wcstombs_s(&converted, narrow, argv[i + 1], sizeof(narrow) - 1);
            path = narrow;
        }
        break;
    }
    LocalFree(argv);
    if (found && path.empty()) path = find_default_recording();
    return found;
}

// ── Recording ───────────────────────────────────────────────────
//
// Writes the same format load_recording reads, so a stream captured here can be replayed
// through these checks or compared against testdata. The managed samples share
// WinPenKit.Diagnostics.StrokeRecorder; this binding sees only the C ABI, so it writes the
// few lines itself.

inline bool record_requested(std::string& path) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;

    bool found = false;
    for (int i = 1; i < argc; i++) {
        if (_wcsicmp(argv[i], L"--record") != 0) continue;
        if (i + 1 < argc && wcsncmp(argv[i + 1], L"--", 2) != 0) {
            char narrow[MAX_PATH * 2];
            size_t converted = 0;
            wcstombs_s(&converted, narrow, argv[i + 1], sizeof(narrow) - 1);
            path = narrow;
            found = true;
        }
        break;
    }
    LocalFree(argv);
    // The path is required. A recorder that chose its own filename would overwrite the
    // previous capture, which is the one thing a person drawing a comparison pair cannot
    // afford.
    return found;
}

/// Which clock a recording's time column is counted on.
///
/// Declared here rather than taken from pen_session.h, because Scribble.Qt shares this header
/// and includes no WinPenKit header at all -- that independence is the whole point of that
/// sample. The values and the names match `WinPenKit.PenTimestampSource` and the C
/// `PenTimestampSource` deliberately, so one reader parses a file from any of the seven
/// samples; Scribble.Win32 holds the static_assert that keeps the two in step, because it is
/// the one translation unit that sees both.
enum class TimestampSource {
    None = 0,
    PerformanceCounter = 1,
    SystemTicks = 2,
    DeviceTicks = 3,
};

inline const char* timestamp_source_name(TimestampSource ts) {
    switch (ts) {
        case TimestampSource::PerformanceCounter: return "PerformanceCounter";
        case TimestampSource::SystemTicks:        return "SystemTicks";
        case TimestampSource::DeviceTicks:        return "DeviceTicks";
        default:                                  return "None";
    }
}

struct Recorder {
    struct Sample { double x, y; uint32_t pressure; int64_t time_us; };
    std::vector<Sample> points;
    std::string source;
    int max_pressure = 0;
    TimestampSource timestamp = TimestampSource::None;
    bool spans_sessions = false;

    /// Name the session these points come from, and its pressure range. Called when a
    /// session starts, not when the file is written: this sample switches pen API while a
    /// recording runs, and one maximum cannot describe two devices.
    void describe(const char* src, int max_p,
                  TimestampSource ts = TimestampSource::None) {
        // The clock counts as part of the identity, not just the name and the range. Two
        // sessions on the same backend with the same pressure range are still two origins, and
        // treating them as one saves a subtraction across them with no warning attached.
        if (!points.empty() && (source != src || max_pressure != max_p || timestamp != ts)) {
            spans_sessions = true;
            return;
        }
        source = src ? src : "";
        max_pressure = max_p;
        timestamp = ts;
    }

    void add(double x, double y, uint32_t pressure, int64_t time_us) {
        if (pressure == 0) return;  // hover carries no stroke
        points.push_back({x, y, pressure, time_us});
    }

    /// Returns the number of points written, or 0 when there was nothing to write -- an
    /// empty file must never stand in for a stroke nobody drew.
    int save(const std::string& path) const {
        if (points.empty()) return 0;

        std::ofstream f(path, std::ios::binary);
        if (!f) return 0;

        f << "# Pen stroke recorded from a live session, in desktop pixels.\n";
        if (!source.empty()) f << "# Source: " << source << "\n";
        f << "# MaxPressure: " << max_pressure << "\n";
        if (spans_sessions)
            f << "# WARNING: the pen API changed while this was recording. The values above "
                 "describe the session the first points came from; later points came from "
                 "another. Do not normalise pressure from this file, and do not read the "
                 "time column across the change -- the two sessions count from different "
                 "origins, so a gap that spans them measures nothing.\n";
        f << "# Captured: " << points.size() << " points\n";

        // The time column is relative to the first point, so the file needs no epoch and
        // carries no machine uptime. What it does need is the clock, because that sets what a
        // gap of zero means.
        f << "# Timestamp: " << timestamp_source_name(timestamp) << "\n";
        f << "desktopX,desktopY,pressure,timeUs\n";

        // Round-trip precision, deliberately: a recorder that quantized its own output would
        // report every session as quantized.
        f << std::setprecision(17);
        const int64_t origin = points.front().time_us;
        for (const auto& p : points)
            f << p.x << ',' << p.y << ',' << p.pressure << ','
              << (timestamp == TimestampSource::None ? 0 : p.time_us - origin) << '\n';

        return static_cast<int>(points.size());
    }
};

// ── Presentation sampling ───────────────────────────────────────
//
// check_presentation_1to1 above compares the host's size against the surface's pixel count.
// Both can be right while the framework draws part of the surface across the whole host.
// That was issue 70 in the Avalonia sample: a 2700px bitmap, a host covering 2700 device
// pixels, and the top-left 1200x600 pixels stretched across them. Strokes landed 2.25 times
// too far from the canvas origin, and the check passed before the fix and after it.
//
// This measures pixels instead. Two markers a known distance apart in the surface must land
// that same distance apart on the screen. Being a ratio of two distances, it needs neither
// the canvas origin nor the display scale -- both cancel.
//
// The same measurement as WinPenKit.Diagnostics.PresentationProbe, reimplemented for the same
// reason the rest of this header is: the native sample has no managed runtime under it. The
// check id and the line format match, so one script reads all six samples.
//
// This sample presents with BitBlt and no stretch, so a failure here would mean Windows
// itself scaled the window. Worth having anyway: the check that cannot fail today is the one
// that catches the change that makes it possible.

struct PresentationMarker {
    int x, y, size;
    BYTE r, g, b;

    double centre_x() const { return x + size / 2.0; }
    double centre_y() const { return y + size / 2.0; }
};

class PresentationProbe {
public:
    // Markers at 8% and 33% along both axes. A quarter of the surface separates them, which
    // is a long enough baseline that centroid noise is nowhere near the tolerance, and close
    // enough to the origin that both still land inside the host when the surface is magnified
    // up to about 3x -- so a magnified surface reports its factor rather than "not found".
    PresentationProbe(int surface_w, int surface_h)
        : surface_w_(surface_w), surface_h_(surface_h) {
        int size = (std::min)(surface_w, surface_h) / 40;
        if (size < 12) size = 12;

        first_  = {int(surface_w * 0.08), int(surface_h * 0.08), size, 255, 0, 255};
        second_ = {int(surface_w * 0.33), int(surface_h * 0.33), size, 0, 255, 255};
    }

    const PresentationMarker& first() const { return first_; }
    const PresentationMarker& second() const { return second_; }

    // Fills both markers into a device context. The caller presents afterwards.
    void draw(HDC dc) const {
        fill(dc, first_);
        fill(dc, second_);
    }

    // For a caller that does not paint through a device context. Scribble.Qt draws into a
    // QImage with QPainter, so it takes the rectangles and fills them itself.
    void draw_with(const std::function<void(int x, int y, int size,
                                            int r, int g, int b)>& fill_rect) const {
        fill_rect(first_.x, first_.y, first_.size, first_.r, first_.g, first_.b);
        fill_rect(second_.x, second_.y, second_.size, second_.r, second_.g, second_.b);
    }

    // Watches the window until both markers appear and two consecutive readings agree, then
    // records L1.presentation-sampling.
    //
    // Pumps the message queue between captures rather than returning to the caller's loop.
    // The managed samples await instead; this one runs its checks before the message loop
    // exists, so the pump has to live here. Either way the window keeps painting, which is
    // the part that matters.
    // `drive_frame` is called between captures and must let the window paint. It defaults
    // to pumping the Win32 queue, which is right for a plain Win32 application. A toolkit
    // with its own event loop should pass that loop's equivalent instead -- Scribble.Qt
    // passes QCoreApplication::processEvents, so Qt's queued events are delivered rather
    // than only the messages this pump happens to see.
    void measure(Report& rep, HWND hwnd, int timeout_ms = 5000,
                 const std::function<void()>& drive_frame = pump) const {
        const char* id = "L1.presentation-sampling";

        if (!hwnd) { rep.skip(id, "no window handle"); return; }

        double want_dx = second_.centre_x() - first_.centre_x();
        double want_dy = second_.centre_y() - first_.centre_y();
        if (want_dx < 1 || want_dy < 1) {
            char b[128];
            _snprintf_s(b, sizeof(b), _TRUNCATE,
                "surface %dx%d is too small to place markers in", surface_w_, surface_h_);
            rep.skip(id, b);
            return;
        }

        Shot shot;
        Centroid a{}, b{};
        bool have_a = false, have_b = false, settled = false;
        double prev_dx = 1e30, prev_dy = 1e30;
        int attempts = 0;

        DWORD deadline = GetTickCount() + DWORD(timeout_ms);
        while (GetTickCount() < deadline) {
            attempts++;
            if (capture(hwnd, shot)) {
                have_a = find(shot, first_.r, first_.g, first_.b, a);
                have_b = find(shot, second_.r, second_.g, second_.b, b);

                if (have_a && have_b) {
                    // Both markers visible is not enough. Windows animates a window open by
                    // compositing it scaled up to its final size, so a capture taken during
                    // that reads a few per cent small. Two consecutive readings that agree
                    // mean nothing is still moving, which is a property of the measurement
                    // rather than a guess about how long an animation lasts.
                    double dx = b.x - a.x, dy = b.y - a.y;
                    if (std::fabs(dx - prev_dx) < 0.5 && std::fabs(dy - prev_dy) < 0.5) {
                        settled = true;
                        break;
                    }
                    prev_dx = dx; prev_dy = dy;
                }
            }
            drive_frame();
            Sleep(50);
        }

        char buf[512];
        if (!have_a || !have_b) {
            // Which one is missing says what went wrong. Nothing at all means nothing was
            // drawn where this could see it. The near marker alone means the surface is
            // magnified far enough to have carried the far one outside its host.
            const char* why =
                (!have_a && !have_b)
                    ? "neither marker reached the screen  <- the window is hidden or obscured,"
                      " or no frame was presented"
                    : !have_a
                        ? "only the far marker reached the screen  <- unexpected; the near"
                          " marker sits closer to the surface origin and should always be in view"
                        : "only the near marker reached the screen  <- the surface is magnified"
                          " enough to carry the far marker outside its host, by more than about 3x";
            _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%s (after %d captures)", why, attempts);
            rep.check(id, false, buf);
            return;
        }

        if (!settled) {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "the window never stopped moving after %d captures  <- it is still animating,"
                " resizing, or being redrawn, and no reading can be trusted while that is true",
                attempts);
            rep.check(id, false, buf);
            return;
        }

        double got_dx = b.x - a.x, got_dy = b.y - a.y;
        double rx = got_dx / want_dx, ry = got_dy / want_dy;

        // One per cent. The markers are tens of pixels across and their centroids land well
        // inside a pixel, so this sits far above the measurement's noise and far below any
        // scaling error worth reporting: the smallest one seen in practice was 2.25x.
        bool ok = std::fabs(rx - 1.0) < 0.01 && std::fabs(ry - 1.0) < 0.01;

        if (ok) {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "markers %.0fx%.0f surface px apart appeared %.1fx%.1f device px apart"
                " (x %.3f, y %.3f; %lld/%lld px matched, %d drawn)",
                want_dx, want_dy, got_dx, got_dy, rx, ry, a.n, b.n, first_.size * first_.size);
        } else {
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "markers %.0fx%.0f surface px apart appeared %.1fx%.1f device px apart"
                " (x %.3f, y %.3f; %lld/%lld px matched, %d drawn)"
                "  <- the surface is sampled at %.2fx horizontally and %.2fx vertically, so"
                " ink lands that far from where the pen was",
                want_dx, want_dy, got_dx, got_dy, rx, ry, a.n, b.n,
                first_.size * first_.size, rx, ry);
        }
        rep.check(id, ok, buf);
    }

private:
    struct Shot {
        int w = 0, h = 0;
        std::vector<BYTE> bgra;
    };

    struct Centroid {
        double x = 0, y = 0;
        long long n = 0;
    };

    static void fill(HDC dc, const PresentationMarker& m) {
        HBRUSH brush = CreateSolidBrush(RGB(m.r, m.g, m.b));
        RECT rc{m.x, m.y, m.x + m.size, m.y + m.size};
        FillRect(dc, &rc, brush);
        DeleteObject(brush);
    }

    // Lets the window finish painting and finish any open animation. Without this the loop
    // below captures the same stale frame until it times out.
    static void pump() {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    // From the screen rather than through PrintWindow. PrintWindow asks the window to render
    // again into a device context, and what this check is about is what the compositor put on
    // the display -- a second render is a different measurement wearing the same name.
    static bool capture(HWND hwnd, Shot& out) {
        RECT wr{};
        if (!GetWindowRect(hwnd, &wr)) return false;
        int w = wr.right - wr.left, h = wr.bottom - wr.top;
        if (w <= 0 || h <= 0) return false;

        HDC screen = GetDC(nullptr);
        if (!screen) return false;

        bool ok = false;
        HDC mem = CreateCompatibleDC(screen);
        HBITMAP dib = nullptr;
        HGDIOBJ old = nullptr;
        void* bits = nullptr;

        if (mem) {
            BITMAPINFO bi{};
            bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;      // top-down, so row 0 is the top of the window
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;

            dib = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
            if (dib && bits) {
                old = SelectObject(mem, dib);
                if (BitBlt(mem, 0, 0, w, h, screen, wr.left, wr.top, SRCCOPY | CAPTUREBLT)) {
                    out.w = w;
                    out.h = h;
                    out.bgra.resize(size_t(w) * h * 4);
                    memcpy(out.bgra.data(), bits, out.bgra.size());
                    ok = true;
                }
            }
        }

        if (old) SelectObject(mem, old);
        if (dib) DeleteObject(dib);
        if (mem) DeleteDC(mem);
        ReleaseDC(nullptr, screen);
        return ok;
    }

    // The centroid of every pixel close to the given colour. False when too few match to be a
    // marker rather than a stray blend along some edge.
    static bool find(const Shot& s, BYTE r, BYTE g, BYTE b, Centroid& out) {
        const int tolerance = 48;
        long long sum_x = 0, sum_y = 0, n = 0;

        for (int y = 0; y < s.h; y++) {
            const BYTE* row = s.bgra.data() + size_t(y) * s.w * 4;
            for (int x = 0; x < s.w; x++) {
                const BYTE* p = row + size_t(x) * 4;
                if (std::abs(int(p[2]) - int(r)) > tolerance) continue;
                if (std::abs(int(p[1]) - int(g)) > tolerance) continue;
                if (std::abs(int(p[0]) - int(b)) > tolerance) continue;
                sum_x += x; sum_y += y; n++;
            }
        }

        if (n < 16) return false;
        out.x = double(sum_x) / n;
        out.y = double(sum_y) / n;
        out.n = n;
        return true;
    }

    int surface_w_, surface_h_;
    PresentationMarker first_{}, second_{};
};


// ---- Window placement -----------------------------------------

// Moves the window inside its monitor's work area, shrinking it first if it does not fit.
// A maximized window already occupies exactly the work area and its window rect overhangs by
// the invisible resize border on purpose, so leave one alone.
inline void clamp_to_work_area(HWND hwnd) {
    if (!hwnd || IsZoomed(hwnd) || IsIconic(hwnd)) return;

    RECT win{};
    if (!GetWindowRect(hwnd, &win)) return;

    HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{ sizeof(MONITORINFO) };
    if (!GetMonitorInfoW(mon, &mi)) return;

    const RECT& wa = mi.rcWork;
    int w = static_cast<int>(win.right - win.left);
    int h = static_cast<int>(win.bottom - win.top);
    int wa_left = static_cast<int>(wa.left),   wa_top    = static_cast<int>(wa.top);
    int wa_right = static_cast<int>(wa.right), wa_bottom = static_cast<int>(wa.bottom);
    int win_left = static_cast<int>(win.left), win_top   = static_cast<int>(win.top);

    // Shrink first: moving a window larger than the work area can never bring it inside.
    int new_w = std::min(w, wa_right - wa_left);
    int new_h = std::min(h, wa_bottom - wa_top);

    int new_x = std::clamp(win_left, wa_left, std::max(wa_left, wa_right - new_w));
    int new_y = std::clamp(win_top,  wa_top,  std::max(wa_top,  wa_bottom - new_h));

    if (new_x == win_left && new_y == win_top && new_w == w && new_h == h) return;

    UINT flags = SWP_NOZORDER | SWP_NOACTIVATE;
    if (new_w == w && new_h == h) flags |= SWP_NOSIZE;
    SetWindowPos(hwnd, nullptr, new_x, new_y, new_w, new_h, flags);
}

}  // namespace selftest
