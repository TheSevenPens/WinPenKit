#include "pch.h"
#include "canvas.h"

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
using namespace Microsoft::UI::Xaml::Input;
using namespace Microsoft::UI::Xaml::Media;
using namespace Windows::Foundation;

namespace {

// Held for the process lifetime. WinUI's Window is not kept alive by the dispatcher, so a
// local would close the moment OnLaunched returned.
Window g_window{ nullptr };
std::unique_ptr<scribble::Canvas> g_canvas;
std::optional<Point> g_lastPoint;
double g_brushSize = 6.0;

struct ScribbleApp : ApplicationT<ScribbleApp> {
    /// Reports a startup failure somewhere a person will actually see it.
    ///
    /// A WinRT exception escaping OnLaunched becomes 0xC000027B, a stowed exception, and the
    /// HRESULT and message that caused it are not in the exit code or the event log entry.
    /// One real fault here -- a resource dictionary that shadowed the theme brushes -- showed
    /// as nothing but that number until it was caught and printed.
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
            std::string m(e.what());
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
        //
        // Checked by running with and without: with, 0xC000027B every launch; without, the
        // window opens and Button and Slider render with their normal Fluent appearance.

        g_window = Window();
        g_window.Title(L"Scribble WinUI Native - WinPenKit");

        auto root = Grid();
        root.RowDefinitions().Append([] { RowDefinition r; r.Height(GridLengthHelper::Auto()); return r; }());
        root.RowDefinitions().Append([] { RowDefinition r; r.Height(GridLengthHelper::FromValueAndType(1, GridUnitType::Star)); return r; }());

        // A placeholder bar until the standard ribbon goes in. Kept so the canvas does not
        // start at the window's top edge, which is where the half-pixel origin faults live.
        auto bar = StackPanel();
        bar.Orientation(Orientation::Horizontal);
        bar.Spacing(12);
        bar.Padding(ThicknessHelper::FromLengths(12, 8, 12, 8));
        bar.Background(SolidColorBrush(winrt::Microsoft::UI::ColorHelper::FromArgb(255, 0xF3, 0xF3, 0xF3)));

        auto clearButton = Button();
        clearButton.Content(box_value(L"Clear"));
        clearButton.Click([](auto&&, auto&&) {
            if (g_canvas) g_canvas->clear();
            g_lastPoint.reset();
        });
        bar.Children().Append(clearButton);

        auto note = TextBlock();
        note.Text(L"Skia C API, C++/WinRT. Drag to draw.");
        note.VerticalAlignment(VerticalAlignment::Center);
        note.Opacity(0.7);
        bar.Children().Append(note);

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

        // IWindowNative is how a WinUI 3 Window yields its HWND. There is no managed
        // interop here; it is a plain COM interface on the Window itself.
        HWND hwnd = nullptr;
        if (auto native = g_window.try_as<::IWindowNative>()) {
            native->get_WindowHandle(&hwnd);
        }

        g_canvas = std::make_unique<scribble::Canvas>(image, host, hwnd);

        host.SizeChanged([](auto&&, auto&&) { if (g_canvas) g_canvas->ensureSurface(); });

        // Pointer events for now, so the Skia path can be proven before the WinPenKit session
        // is wired in. Mouse included deliberately: the canvas has to be testable without a
        // tablet, and the pen backends arrive next.
        host.PointerPressed([host](auto&&, PointerRoutedEventArgs const& e) {
            auto p = e.GetCurrentPoint(host);
            g_lastPoint = p.Position();
            host.CapturePointer(e.Pointer());
        });
        host.PointerMoved([host](auto&&, PointerRoutedEventArgs const& e) {
            auto p = e.GetCurrentPoint(host);
            if (!p.IsInContact()) return;
            if (!g_canvas) return;

            const double scale = g_canvas->rasterizationScale();
            const Point now = p.Position();
            if (g_lastPoint) {
                const float pressure = p.Properties().Pressure();
                const float width = static_cast<float>(pressure * 2.0 * g_brushSize + 0.5);
                g_canvas->drawSegment(
                    static_cast<float>(g_lastPoint->X * scale), static_cast<float>(g_lastPoint->Y * scale),
                    static_cast<float>(now.X * scale),          static_cast<float>(now.Y * scale),
                    width);
                g_canvas->present();
            }
            g_lastPoint = now;
        });
        host.PointerReleased([host](auto&&, PointerRoutedEventArgs const& e) {
            g_lastPoint.reset();
            host.ReleasePointerCapture(e.Pointer());
        });

        g_window.Content(root);
        g_window.Activate();
    }
};

} // namespace

int __stdcall wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    init_apartment(apartment_type::single_threaded);
    Application::Start([](auto&&) { make<ScribbleApp>(); });
    return 0;
}
