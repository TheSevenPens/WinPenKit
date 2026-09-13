#include "pch.h"
#include "selftestrun.h"
#include "xamlpointer.h"

#include "selftest.h"

#include <utility>

namespace scribble {

namespace {

/// Waits, rather than pumping.
///
/// The probe calls this between captures while it waits for a frame to reach the screen. On
/// the other samples it pumps the message queue, because there the checks run on the thread
/// that owns the window. Here they do not: the UI thread is left alone precisely so it can
/// present, and pumping its queue from another thread would do nothing anyway.
void waitOneFrame() {
    Sleep(16);
}

const wchar_t* apiSettingName(PenInputApi api) {
    switch (api) {
        case PEN_API_WINTAB_SYSTEM:    return L"wintab";
        case PEN_API_WINTAB_DIGITIZER: return L"wintab-hires";
        case PEN_API_WINUI_POINTER:    return L"winui-pointer";
        default:                       return L"";
    }
}

} // namespace

namespace settings {

constexpr const wchar_t* kKey = L"Software\\TheSevenPens\\Scribble.WinUINative";
constexpr const wchar_t* kValue = L"penApi";

std::optional<PenInputApi> savedApi() {
    wchar_t buf[64]{};
    DWORD bytes = sizeof(buf);
    const LSTATUS st = ::RegGetValueW(HKEY_CURRENT_USER, kKey, kValue,
                                      RRF_RT_REG_SZ, nullptr, buf, &bytes);
    if (st != ERROR_SUCCESS) return std::nullopt;

    const std::wstring v(buf);
    if (v == L"wintab")        return PEN_API_WINTAB_SYSTEM;
    if (v == L"wintab-hires")  return PEN_API_WINTAB_DIGITIZER;
    if (v == L"winui-pointer") return PEN_API_WINUI_POINTER;

    // An unrecognised value is discarded rather than mapped onto a default. A stored setting
    // that silently becomes a different backend is how a sample ends up reporting one API
    // while running another.
    return std::nullopt;
}

void saveApi(PenInputApi api) {
    const wchar_t* name = apiSettingName(api);
    if (!*name) return;   // nothing worth remembering about an API this sample cannot open

    HKEY key{};
    if (::RegCreateKeyExW(HKEY_CURRENT_USER, kKey, 0, nullptr, 0,
                          KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return;
    ::RegSetValueExW(key, kValue, 0, REG_SZ,
                     reinterpret_cast<const BYTE*>(name),
                     static_cast<DWORD>((wcslen(name) + 1) * sizeof(wchar_t)));
    ::RegCloseKey(key);
}

} // namespace settings

int runSelfTest(Canvas& canvas, HWND hwnd, PenSession& pen, bool xamlPointerBackend,
                PenInputApi requested, const std::string& replayPath,
                const std::function<void(std::function<void()>)>& runOnUi) {
    selftest::Report r("Scribble.WinUINative");

    // Every read of a XAML property goes through the UI thread. ActualWidth, XamlRoot and
    // TransformToVisual are not free to touch from here.
    double scale = 1.0;
    int sw = 0, sh = 0;
    winrt::Windows::Foundation::Point origin{};
    runOnUi([&] {
        scale = canvas.rasterizationScale();
        sw = canvas.surfaceWidth();
        sh = canvas.surfaceHeight();
        origin = canvas.originOnDesktop();
    });

    r.check_dpi_awareness();
    r.check_window_placement(hwnd);
    r.report_scale(scale);

    // Which backend this run is actually on, against the one that was asked for. A check
    // rather than a report, for the reason Scribble.Qt learned the hard way: a report that
    // printed the request and compared it against nothing let that sample claim Wintab for a
    // day while running on WM_POINTER.
    {
        const bool running = xamlPointerBackend || pen.running();
        const PenInputApi live = xamlPointerBackend ? PEN_API_WINUI_POINTER : pen.api();
        const bool matched = running && live == requested;

        std::string detail = "requested ";
        detail += requested == PEN_API_WINUI_POINTER ? "WinUI Pointer"
                                                     : pen_session_get_api_label(requested);
        detail += ", obtained ";
        detail += !running ? "nothing"
                : (live == PEN_API_WINUI_POINTER ? "WinUI Pointer"
                                                 : pen_session_get_api_label(live));
        if (!matched)
            detail += "  <- anything recorded on this run is on the backend that was obtained";
        r.check("L0.pen-api", matched, detail);
    }

    // WinUI lays out in effective pixels, so the logical-to-physical ratio is the
    // rasterization scale -- the same relationship WPF, Avalonia and Qt have, and not the 1.0
    // that Win32 and WinForms report.
    r.check_surface_physical(sw, sh,
                             sw / scale, sh / scale, scale);

    r.check_surface_alignment(origin.X, origin.Y);
    r.check_presentation_1to1(sw, sh, sw, sh);

    // The conversion the pen goes through, reached from this thread. Marshalled rather than
    // reimplemented: a check with its own arithmetic tests itself.
    auto desktop_to_canvas = [&canvas, &runOnUi](double x, double y) {
        std::pair<double, double> out{};
        runOnUi([&] { out = canvas.desktopToCanvas(x, y); });
        return out;
    };

    if (!replayPath.empty()) {
        auto input = selftest::load_recording(replayPath);
        if (input.empty()) {
            r.skip("L2.recording-subpixel", "recording empty or unreadable");
        } else {
            selftest::center_on(input, origin.X, origin.Y, sw, sh);

            std::vector<std::pair<double, double>> out;
            out.reserve(input.size());
            for (const auto& pt : input) out.push_back(desktop_to_canvas(pt.first, pt.second));

            r.check_recording_subpixel(input);
            r.check_conversion_snap(out, 1.0);
            r.check_conversion_lossless(input, out);
        }
    }

    // Measures what reached the screen, so it runs before the check below moves the window.
    if (sw > 0 && sh > 0) {
        selftest::PresentationProbe probe(sw, sh);

        runOnUi([&] {
            canvas.clear();
            probe.draw_with([&canvas](int x, int y, int size, int rr, int gg, int bb) {
                canvas.fillSurfaceRect(x, y, size, rr, gg, bb);
            });
            canvas.markDirty();
            canvas.present();
        });

        probe.measure(r, hwnd, 5000, waitOneFrame);
    } else {
        r.skip("L1.presentation-sampling", "no drawing surface");
    }

    r.check_origin_tracks_window(hwnd, desktop_to_canvas, 1.0);

    return r.emit();
}

} // namespace scribble
