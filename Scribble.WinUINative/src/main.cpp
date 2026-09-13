#include "pch.h"
#include "canvas.h"
#include "pensession.h"
#include "xamlpointer.h"

#include <microsoft.ui.xaml.window.h>   // IWindowNative

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
double g_brushSize = 6.0;
HWND g_hwnd = nullptr;
TextBlock g_status{ nullptr };

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

    if (api == PEN_API_WINUI_POINTER) {
        g_xamlPointer->attach(g_canvasHost, g_hwnd);
        g_backend = Backend::XamlPointer;
        g_status.Text(L"WinUI Pointer (XAML events)  |  max pressure "
                      + std::to_wstring(scribble::XamlPointerSource::kMaxPressure));
        return;
    }

    const std::string err = g_pen->start(api, g_hwnd);
    if (!err.empty()) {
        g_backend = Backend::None;
        g_status.Text(L"Session failed: " + widen(err.c_str()));
        return;
    }

    g_backend = Backend::Native;
    g_status.Text(widen(g_pen->apiLabel())
                  + L"  |  max pressure " + std::to_wstring(g_pen->maxPressure()));
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

        if (g_lastCanvasPoint && pt.pressure > 0) {
            const float norm = static_cast<float>(pt.pressure) / static_cast<float>(maxP);
            // + 0.5, and a width in physical pixels, matching every other sample. These
            // samples exist to be compared with each other, so a width formula that differs
            // between them is a confound in the one measurement they are for.
            const float width = norm * static_cast<float>(g_brushSize) + 0.5f;
            g_canvas->drawSegment(g_lastCanvasPoint->X, g_lastCanvasPoint->Y,
                                  canvasPt.X, canvasPt.Y, width);
            drew = true;
        }

        g_lastCanvasPoint = canvasPt;
    }

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

        // A placeholder bar until the standard seven-section ribbon goes in. Kept so the canvas
        // does not start at the window's top edge, which is where the half-pixel origin faults
        // live.
        auto bar = StackPanel();
        bar.Orientation(Orientation::Horizontal);
        bar.Spacing(12);
        bar.Padding(ThicknessHelper::FromLengths(12, 8, 12, 8));
        bar.Background(SolidColorBrush(winrt::Microsoft::UI::ColorHelper::FromArgb(255, 0xF3, 0xF3, 0xF3)));

        // Only the APIs this binding can actually create. The managed-only values exist in the
        // enum because a point's source field can carry them, not because this can open one.
        PenInputApi available[8];
        const int nativeCount = pen_session_get_available_apis(available, 8);

        // The native list plus WinUI's own pointer path, and minus the native WM_POINTER
        // session -- which starts cleanly on a WinUI window and then delivers nothing. Offering
        // it would be offering a backend measured not to work here.
        std::vector<PenInputApi> apis;
        for (int i = 0; i < nativeCount; ++i)
            if (available[i] != PEN_API_WM_POINTER) apis.push_back(available[i]);
        apis.push_back(PEN_API_WINUI_POINTER);
        const int count = static_cast<int>(apis.size());

        auto apiBox = ComboBox();
        apiBox.MinWidth(220);
        apiBox.VerticalAlignment(VerticalAlignment::Center);
        for (auto a : apis) {
            const std::wstring label = (a == PEN_API_WINUI_POINTER)
                                     ? L"WinUI Pointer"
                                     : widen(pen_session_get_api_label(a));
            apiBox.Items().Append(box_value(winrt::hstring{ label }));
        }
        apiBox.SelectionChanged([apis](IInspectable const& sender, auto&&) {
            const int idx = sender.as<ComboBox>().SelectedIndex();
            if (idx >= 0 && idx < static_cast<int>(apis.size())) startSession(apis[idx]);
        });
        bar.Children().Append(apiBox);

        auto clearButton = Button();
        clearButton.Content(box_value(L"Clear"));
        clearButton.VerticalAlignment(VerticalAlignment::Center);
        clearButton.Click([](auto&&, auto&&) {
            if (g_canvas) g_canvas->clear();
            g_lastCanvasPoint.reset();
        });
        bar.Children().Append(clearButton);

        g_status = TextBlock();
        g_status.Text(L"no session");
        g_status.VerticalAlignment(VerticalAlignment::Center);
        g_status.Opacity(0.7);
        bar.Children().Append(g_status);

        Grid::SetRow(bar, 0);
        root.Children().Append(bar);

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

        g_window.Content(root);
        g_window.Activate();

        // The last entry is WinUI Pointer, the one that works without a tablet attached.
        if (count > 0) apiBox.SelectedIndex(count - 1);
    }
};

} // namespace

int __stdcall wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    init_apartment(apartment_type::single_threaded);
    Application::Start([](auto&&) { make<ScribbleApp>(); });
    return 0;
}
