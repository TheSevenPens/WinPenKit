#include "pch.h"
#include "canvas.h"
#include "pensession.h"
#include "xamlpointer.h"
#include "ribbon.h"
#include "selftestrun.h"

#include "selftest.h"

#include <microsoft.ui.xaml.window.h>   // IWindowNative

#include <algorithm>
#include <thread>

// Scribble.WinUINative -- WinUI 3 through C++/WinRT, with no managed runtime anywhere.
//
// The UI is built in code rather than XAML markup. Two reasons, in order of weight:
//
//   1. Scribble.Qt builds its ribbon the same way, in C++, so the two native samples stay
//      structurally parallel and a reader comparing them is comparing input handling rather
//      than markup dialects.
//   2. XAML markup in C++ needs an .idl per XAML type, the XAML compiler, and generated
//      headers, none of which this sample would exercise for its own sake.
//
// The trade is that this is not a template for markup-first WinUI. Every control here is the
// same Microsoft.UI.Xaml type a .xaml file would produce; only the construction differs.

using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;
using namespace Microsoft::UI::Xaml::Media;
using namespace Windows::Foundation;

namespace {

// Held for the process lifetime. WinUI's Window is not kept alive by the dispatcher, so a
// local would close the moment OnLaunched returned.
Window g_window{ nullptr };
std::unique_ptr<scribble::Canvas> g_canvas;
std::unique_ptr<scribble::PenSession> g_pen;
std::unique_ptr<scribble::XamlPointerSource> g_xamlPointer;

// Which backend the user picked. WinUI's pointer input does not come through WinPenKit, so
// "which session is running" is not a single object -- see XamlPointerSource for why.
enum class Backend { None, Native, XamlPointer };
Backend g_backend = Backend::None;
UIElement g_canvasHost{ nullptr };
DispatcherTimer g_timer{ nullptr };

std::optional<Point> g_lastCanvasPoint;
HWND g_hwnd = nullptr;
std::unique_ptr<scribble::Ribbon> g_ribbon;
scribble::PenReadout g_readout;

// --record <path> and --selftest [--replay <path>], parsed once in wWinMain and read here.
selftest::Recorder g_recorder;
std::string g_recordPath;
std::string g_replayPath;
bool g_selfTestRequested = false;
PenInputApi g_requestedApi = PEN_API_WINUI_POINTER;
DispatcherTimer g_selfTestTimer{ nullptr };

std::wstring widen(const char* s) {
    if (!s) return L"unknown";
    const int n = ::MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return L"unknown";
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

/// Opens a session and reports what actually happened.
///
/// The running API is read back from the session rather than taken from the request. A
/// digitizer context that failed still runs, as something else, and a readout showing what was
/// asked for rather than what was obtained is the fault PenConventions exists to prevent.
void startSession(PenInputApi api) {
    g_lastCanvasPoint.reset();
    g_pen->stop();
    g_xamlPointer->detach();
    g_requestedApi = api;
    scribble::settings::saveApi(api);

    // Described when a session starts, not when the file is written. This sample switches
    // backend while a recording runs, and the header has to name the session the points came
    // from -- a maximum read at save time would scale the earlier half by the later device's
    // range.
    const auto describe = [](const char* name, int maxP, selftest::TimestampSource ts) {
        if (!g_recordPath.empty()) g_recorder.describe(name, maxP, ts);
    };

    if (api == PEN_API_WINUI_POINTER) {
        g_xamlPointer->attach(g_canvasHost, g_hwnd);
        g_backend = Backend::XamlPointer;
        // SystemTicks: these points come from WinUI's own events, whose timestamps track
        // GetTickCount64 -- the same clock WinPenKit's framework backends report.
        describe("XamlPointerSource (WinUI native)",
                 scribble::XamlPointerSource::kMaxPressure,
                 selftest::TimestampSource::SystemTicks);
        g_ribbon->setStatus(winrt::hstring{
            L"XAML events, max " + std::to_wstring(scribble::XamlPointerSource::kMaxPressure) });
        return;
    }

    const std::string err = g_pen->start(api, g_hwnd);
    if (!err.empty()) {
        g_backend = Backend::None;
        g_ribbon->setStatus(winrt::hstring{ L"failed: " + widen(err.c_str()) });
        g_ribbon->clearReadout();
        return;
    }

    g_backend = Backend::Native;

    // The clock the session reports, not one assumed from the API. Both Wintab contexts run on
    // pkTime, but reading it from the session is what keeps this honest if that changes.
    describe(g_pen->apiLabel(), g_pen->maxPressure(),
             static_cast<selftest::TimestampSource>(g_pen->conventions().timestamp));

    // The running API, read back from the session. A digitizer whose hi-res context failed is
    // still running, as something else, and reporting the request rather than the result is
    // the fault PenConventions exists to prevent.
    g_ribbon->setStatus(winrt::hstring{
        widen(g_pen->apiLabel()) + L", max " + std::to_wstring(g_pen->maxPressure()) });
}

/// Drains whatever the session has and draws it.
///
/// Polled rather than pushed: the C ABI hands points out through pen_session_drain_points and
/// offers no callback, so this mirrors Scribble.Win32's timer. One present per batch rather
/// than per segment, because each present copies the whole surface.
void tick() {
    if (!g_canvas || g_backend == Backend::None) return;

    PenPoint points[128];
    const int n = (g_backend == Backend::XamlPointer)
                ? g_xamlPointer->drain(points, 128)
                : g_pen->drain(points, 128);
    if (n == 0) return;

    const Point origin = g_canvas->originOnDesktop();

    // Per backend, because the two do not share a session. Taking this from the native
    // session while the XAML source is running reads its unstarted default of 1, which
    // turns a pressure of 850 into a stroke 5100 pixels wide: the canvas goes solid
    // black and reads as a rendering fault rather than a division by the wrong scale.
    const int maxP = (g_backend == Backend::XamlPointer)
                   ? scribble::XamlPointerSource::kMaxPressure
                   : g_pen->maxPressure();

    POINT clientOrigin{ 0, 0 };
    if (g_hwnd) ::ClientToScreen(g_hwnd, &clientOrigin);
    bool drew = false;

    for (int i = 0; i < n; ++i) {
        const PenPoint& pt = points[i];
        if (g_backend == Backend::Native) g_pen->applyButtons(pt);

        // Subtracted by hand rather than through ScreenToClient, which takes a POINT and so
        // forces the position onto the whole-pixel grid on the way in. The canvas origin is
        // genuinely on a pixel boundary; only the pen's own position needs its precision kept.
        const Point canvasPt{
            static_cast<float>(pt.desktop_x - origin.X),
            static_cast<float>(pt.desktop_y - origin.Y)
        };

        // A point outside the canvas breaks the stroke rather than clamping to the edge, which
        // would draw a line along the border the pen never travelled.
        if (canvasPt.X < 0 || canvasPt.X >= g_canvas->surfaceWidth() ||
            canvasPt.Y < 0 || canvasPt.Y >= g_canvas->surfaceHeight()) {
            g_lastCanvasPoint.reset();
            continue;
        }

        if (!g_recordPath.empty())
            g_recorder.add(pt.desktop_x, pt.desktop_y, pt.pressure, pt.timestamp_us);

        if (g_lastCanvasPoint && pt.pressure > 0) {
            const float norm = static_cast<float>(pt.pressure) / static_cast<float>(maxP);
            // + 0.5, and a width in physical pixels, matching every other sample. These
            // samples exist to be compared with each other, so a width formula that differs
            // between them is a confound in the one measurement they are for.
            const float width = norm * static_cast<float>(g_ribbon->brushSize()) + 0.5f;
            g_canvas->drawSegment(g_lastCanvasPoint->X, g_lastCanvasPoint->Y,
                                  canvasPt.X, canvasPt.Y, width);
            drew = true;
        }

        g_lastCanvasPoint = canvasPt;

        // The readout describes the most recent point, so it is filled every time rather than
        // only when something was drawn: a hover that moves and draws nothing is exactly what
        // the proximity row is for.
        g_readout.hasData     = true;
        g_readout.inProximity = (pt.status & 0x0001) != 0 || pt.pressure > 0;
        g_readout.screenX = pt.desktop_x;
        g_readout.screenY = pt.desktop_y;
        g_readout.appX    = pt.desktop_x - clientOrigin.x;
        g_readout.appY    = pt.desktop_y - clientOrigin.y;
        g_readout.canvasX = canvasPt.X;
        g_readout.canvasY = canvasPt.Y;
        g_readout.rawX = pt.raw_x;
        g_readout.rawY = pt.raw_y;
        g_readout.rawUnits = (g_backend == Backend::XamlPointer)
                           ? PEN_RAW_NONE
                           : g_pen->conventions().raw_units;
        g_readout.rawPressure = pt.pressure;
        g_readout.maxPressure = maxP;
        g_readout.azimuth = pt.azimuth;
        g_readout.altitude = pt.altitude;
        g_readout.twist = pt.twist;
        g_readout.tiltX = pt.tilt_x;
        g_readout.tiltY = pt.tilt_y;
        g_readout.cursor = pt.cursor;
        g_readout.rawButtons = pt.buttons;

        if (g_backend == Backend::Native) {
            g_readout.tip     = g_pen->tipDown() || pt.pressure > 0;
            g_readout.barrel1 = g_pen->barrel1();
            g_readout.barrel2 = g_pen->barrel2();
            g_readout.barrel3 = g_pen->barrel3();
        } else {
            // A pointer bitmask carries no per-button identity, so B2 and B3 read false
            // because the backend cannot say, not because they are up.
            g_readout.tip     = pt.pressure > 0;
            g_readout.barrel1 = (pt.buttons & 0x0001) != 0;
            g_readout.barrel2 = false;
            g_readout.barrel3 = false;
        }
        g_readout.eraser = pt.cursor == 14u || (pt.buttons & 0x0002) != 0;
    }

    if (g_ribbon) g_ribbon->setReadout(g_readout);
    if (drew) g_canvas->present();
}

struct ScribbleApp : ApplicationT<ScribbleApp> {
    /// Reports a startup failure somewhere a person will actually see it.
    ///
    /// A WinRT exception escaping OnLaunched becomes 0xC000027B, a stowed exception, and the
    /// HRESULT and message that caused it are in neither the exit code nor the event log
    /// entry. One real fault here -- a resource dictionary that shadowed the theme brushes --
    /// showed as nothing but that number until it was caught and printed.
    static void note(std::wstring const& text) {
        ::OutputDebugStringW((text + L"\n").c_str());
        const int n = ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (n <= 0) return;
        std::string utf8(static_cast<size_t>(n), '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, utf8.data(), n, nullptr, nullptr);
        fprintf(stderr, "[startup] %s\n", utf8.c_str());
        fflush(stderr);
    }

    void OnLaunched(LaunchActivatedEventArgs const& args) {
        try {
            launch(args);
        } catch (winrt::hresult_error const& e) {
            note(L"hresult_error 0x" + std::to_wstring(static_cast<uint32_t>(e.code()))
                 + L" : " + std::wstring(e.message()));
            throw;
        } catch (std::exception const& e) {
            const std::string m(e.what());
            note(L"std::exception " + std::wstring(m.begin(), m.end()));
            throw;
        }
    }

    void launch(LaunchActivatedEventArgs const&) {
        // No XamlControlsResources here, deliberately.
        //
        // A markup-first WinUI project puts <XamlControlsResources/> in App.xaml, and the
        // obvious translation is to merge one into Application.Resources. That crashes this
        // app at startup: "Cannot find a resource with the given key:
        // AcrylicBackgroundFillColorDefaultBrush", surfacing as 0xC000027B with no message.
        //
        // Windows App SDK 1.7 already loads the WinUI theme resources for an application that
        // declares none, and merging XamlControlsResources on top shadows them with a set that
        // does not carry the system backdrop brushes. Leaving it out is not "skipping the
        // styling" -- the controls below are fully styled without it.

        g_window = Window();
        g_window.Title(L"Scribble WinUI Native - WinPenKit");

        auto root = Grid();
        root.RowDefinitions().Append([] { RowDefinition r; r.Height(GridLengthHelper::Auto()); return r; }());
        root.RowDefinitions().Append([] { RowDefinition r; r.Height(GridLengthHelper::FromValueAndType(1, GridUnitType::Star)); return r; }());

        PenInputApi available[8];
        const int nativeCount = pen_session_get_available_apis(available, 8);

        // The native list plus WinUI's own pointer path, and minus the native WM_POINTER
        // session -- which starts cleanly on a WinUI window and then delivers nothing. Offering
        // it would be offering a backend measured not to work here.
        std::vector<PenInputApi> apis;
        for (int i = 0; i < nativeCount; ++i)
            if (available[i] != PEN_API_WM_POINTER) apis.push_back(available[i]);
        apis.push_back(PEN_API_WINUI_POINTER);

        g_ribbon = std::make_unique<scribble::Ribbon>(
            apis,
            [](PenInputApi api) { startSession(api); },
            [] {
                if (g_canvas) g_canvas->clear();
                g_lastCanvasPoint.reset();
                g_readout = scribble::PenReadout{};
                if (g_ribbon) g_ribbon->clearReadout();
            },
            [](double) { /* brush size is read from the ribbon when a segment is drawn */ });

        auto ribbonRoot = g_ribbon->root();
        Grid::SetRow(ribbonRoot, 0);
        root.Children().Append(ribbonRoot);

        // The Image is pinned top-left inside a clipping Border, so a surface larger than its
        // host is clipped rather than scaled. Centring it instead would put the canvas origin
        // on a half pixel at odd sizes.
        auto host = Border();
        host.Background(SolidColorBrush(winrt::Microsoft::UI::ColorHelper::FromArgb(255, 0xFF, 0xFF, 0xFF)));
        auto image = Image();
        image.HorizontalAlignment(HorizontalAlignment::Left);
        image.VerticalAlignment(VerticalAlignment::Top);
        // Fill, not None. With Stretch::None WinUI draws one bitmap pixel per *logical* unit,
        // so on a 2.25x display a surface sized in physical pixels renders 2.25x too large and
        // is clipped to the element. Fill maps the whole bitmap onto the element, and since
        // Canvas sizes the element to pixels/scale, that lands one bitmap pixel on one physical
        // pixel. This is the L1.presentation-1to1 fault, made by hand.
        image.Stretch(Stretch::Fill);
        host.Child(image);
        Grid::SetRow(host, 1);
        root.Children().Append(host);

        // IWindowNative is how a WinUI 3 Window yields its HWND. There is no managed interop
        // here; it is a plain COM interface on the Window itself.
        if (auto native = g_window.try_as<::IWindowNative>()) {
            native->get_WindowHandle(&g_hwnd);
        }

        g_canvasHost = host;
        g_canvas = std::make_unique<scribble::Canvas>(image, host, g_hwnd);
        g_pen = std::make_unique<scribble::PenSession>();
        g_xamlPointer = std::make_unique<scribble::XamlPointerSource>();

        host.SizeChanged([](auto&&, auto&&) { if (g_canvas) g_canvas->ensureSurface(); });

        // Wintab contexts drop down the overlap order when another application takes focus and
        // nothing puts them back. Without this the first stroke after returning to the window
        // is silently swallowed while every stroke after it draws normally.
        g_window.Activated([](auto&&, WindowActivatedEventArgs const& e) {
            if (e.WindowActivationState() != WindowActivationState::Deactivated && g_pen)
                g_pen->onActivated();
        });

        // 60 Hz. The C ABI has no callback, so points accumulate in the session until drained;
        // polling slower than the device reports costs latency but loses no points.
        g_timer = DispatcherTimer();
        g_timer.Interval(std::chrono::milliseconds(16));
        g_timer.Tick([](auto&&, auto&&) { tick(); });
        g_timer.Start();

        // Written on close rather than incrementally: a partial file that looks complete is
        // worse than none, and the whole stroke is in memory anyway.
        g_window.Closed([](auto&&, auto&&) {
            if (g_recordPath.empty()) return;
            const int written = g_recorder.save(g_recordPath);
            fprintf(stderr, "[record] %d points -> %s\n", written, g_recordPath.c_str());
            fflush(stderr);
        });

        g_window.Content(root);
        g_window.Activate();

        // The saved choice if there is one and it is still offered, otherwise WinUI Pointer --
        // the last entry, and the one that works with no tablet attached.
        PenInputApi initial = apis.back();
        if (auto saved = scribble::settings::savedApi()) {
            if (std::find(apis.begin(), apis.end(), *saved) != apis.end()) initial = *saved;
        }
        const int initialIndex = static_cast<int>(
            std::find(apis.begin(), apis.end(), initial) - apis.begin());

        // selectApi does not raise the selection event, so the session is opened explicitly
        // rather than relying on a side effect of setting the index.
        g_ribbon->selectApi(initialIndex);
        startSession(initial);

        if (g_selfTestRequested) {
            // After the first layout, not here: the surface does not exist until the canvas
            // host has been measured, and every surface check would report zero.
            g_selfTestTimer = DispatcherTimer();
            g_selfTestTimer.Interval(std::chrono::milliseconds(800));
            g_selfTestTimer.Tick([](auto&&, auto&&) {
                g_selfTestTimer.Stop();

                // On its own thread, so the UI thread is free to present the frames the
                // presentation probe is waiting for. Running the checks in this callback made
                // that probe report "neither marker reached the screen" on every attempt,
                // because while the callback ran none had been.
                auto queue = winrt::Microsoft::UI::Dispatching::DispatcherQueue::GetForCurrentThread();
                std::thread([queue] {
                    auto runOnUi = [queue](std::function<void()> work) {
                        winrt::handle done{ ::CreateEventW(nullptr, TRUE, FALSE, nullptr) };
                        const bool queued = queue.TryEnqueue([&work, h = done.get()] {
                            work();
                            ::SetEvent(h);
                        });
                        // A wait rather than an infinite one: a UI thread that has stopped
                        // servicing its queue would otherwise hang the checks with no output
                        // at all, which is worse than a failed check.
                        if (queued) ::WaitForSingleObject(done.get(), 5000);
                    };

                    const int code = scribble::runSelfTest(
                        *g_canvas, g_hwnd, *g_pen,
                        g_backend == Backend::XamlPointer, g_requestedApi, g_replayPath,
                        runOnUi);
                    ::ExitProcess(static_cast<UINT>(code));
                }).detach();
            });
            g_selfTestTimer.Start();
        }
    }
};

} // namespace

int __stdcall wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    // Parsed through the shared helpers, so the flags spell the same way here as in the other
    // samples. A sample whose --record took a different shape would not be comparable with the
    // ones it exists to be compared against.
    //
    // Both called, not short-circuited. Written as `requested() || replay_requested(path)`
    // this silently skipped the replay: --selftest made the left side true, the right side
    // never ran, the path stayed empty, and the run reported 9/9 having quietly left out the
    // three conversion checks that replaying is for. A green result with fewer checks in it is
    // the worst way for this to fail.
    const bool plain = selftest::requested();
    const bool replay = selftest::replay_requested(g_replayPath);
    g_selfTestRequested = plain || replay;
    selftest::record_requested(g_recordPath);

    init_apartment(apartment_type::single_threaded);
    Application::Start([](auto&&) { make<ScribbleApp>(); });
    return 0;
}
