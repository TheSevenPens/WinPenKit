#include "pch.h"
#include "ribbon.h"
#include "xamlpointer.h"

#include <winrt/Microsoft.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Text.h>

#include <cmath>
#include <format>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
using namespace winrt::Microsoft::UI::Xaml::Media;
// No using-directive for Shapes: its Ellipse collides with the GDI Ellipse() that
// windows.h declares, and the ambiguity is reported at the use rather than the import.
using XamlEllipse = winrt::Microsoft::UI::Xaml::Shapes::Ellipse;
using namespace winrt::Windows::Foundation;

namespace {

using winrt::Microsoft::UI::ColorHelper;

SolidColorBrush brush(uint8_t r, uint8_t g, uint8_t b) {
    return SolidColorBrush(ColorHelper::FromArgb(255, r, g, b));
}

/// Values are monospaced so a number that changes width does not shift the ones beside it. A
/// readout that jitters horizontally while the pen moves is unreadable at speed, which is when
/// it matters.
constexpr wchar_t kMono[] = L"Consolas";

} // namespace

namespace scribble {

hstring apiDisplayName(PenInputApi api) {
    if (api == PEN_API_WINUI_POINTER) return L"WinUI Pointer";
    const char* label = pen_session_get_api_label(api);
    if (!label) return L"Unknown";
    const int n = ::MultiByteToWideChar(CP_UTF8, 0, label, -1, nullptr, 0);
    std::wstring w(static_cast<size_t>(n > 0 ? n - 1 : 0), L'\0');
    if (n > 0) ::MultiByteToWideChar(CP_UTF8, 0, label, -1, w.data(), n);
    return hstring{ w };
}

TextBlock Ribbon::makeValue(hstring const& initial) {
    TextBlock t;
    t.Text(initial);
    t.FontFamily(FontFamily(kMono));
    t.FontSize(12);
    return t;
}

TextBlock Ribbon::makeCaption(hstring const& text) {
    TextBlock t;
    t.Text(text);
    t.FontSize(12);
    t.Opacity(0.6);
    t.Margin(ThicknessHelper::FromLengths(0, 0, 6, 0));
    return t;
}

XamlEllipse Ribbon::makeDot() {
    XamlEllipse e;
    e.Width(8);
    e.Height(8);
    e.Fill(brush(0xC8, 0xC8, 0xC8));
    e.VerticalAlignment(VerticalAlignment::Center);
    e.Margin(ThicknessHelper::FromLengths(0, 0, 4, 0));
    return e;
}

void Ribbon::setDot(XamlEllipse const& dot, bool on, bool eraserColour) {
    if (!dot) return;
    if (!on) { dot.Fill(brush(0xC8, 0xC8, 0xC8)); return; }
    dot.Fill(eraserColour ? brush(0xD0, 0x50, 0x20) : brush(0x18, 0x80, 0x30));
}

Border Ribbon::makeSection(hstring const& header, UIElement const& body) {
    auto stack = StackPanel();
    stack.Spacing(4);

    auto h = TextBlock();
    h.Text(header);
    h.FontSize(11);
    h.Opacity(0.55);
    h.CharacterSpacing(80);
    h.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
    stack.Children().Append(h);
    stack.Children().Append(body);

    auto border = Border();
    border.Child(stack);
    border.Padding(ThicknessHelper::FromLengths(12, 8, 12, 8));
    // A separator on one edge rather than a box per section: seven boxed cards in a row read
    // as seven unrelated things, where these are one readout.
    border.BorderThickness(ThicknessHelper::FromLengths(0, 0, 1, 0));
    border.BorderBrush(brush(0xDC, 0xDC, 0xDC));
    return border;
}

Ribbon::Ribbon(std::vector<PenInputApi> apis,
               std::function<void(PenInputApi)> onApiSelected,
               std::function<void()> onClear,
               std::function<void(double)> onBrushSize)
    : apis_(std::move(apis)), onApiSelected_(std::move(onApiSelected)) {

    root_ = StackPanel();
    root_.Orientation(Orientation::Horizontal);
    root_.Background(brush(0xF6, 0xF6, 0xF6));

    // ── PEN API ─────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(4);

        api_ = ComboBox();
        api_.MinWidth(190);
        for (auto a : apis_) api_.Items().Append(box_value(apiDisplayName(a)));
        api_.SelectionChanged([this](IInspectable const& sender, auto&&) {
            if (suppressSelection_) return;
            const int idx = sender.as<ComboBox>().SelectedIndex();
            if (idx >= 0 && idx < static_cast<int>(apis_.size()) && onApiSelected_)
                onApiSelected_(apis_[idx]);
        });
        body.Children().Append(api_);

        auto row = StackPanel();
        row.Orientation(Orientation::Horizontal);
        row.Spacing(8);

        auto clear = Button();
        clear.Content(box_value(L"Clear"));
        clear.Click([onClear](auto&&, auto&&) { if (onClear) onClear(); });
        row.Children().Append(clear);

        status_ = makeValue(L"no session");
        status_.VerticalAlignment(VerticalAlignment::Center);
        status_.Opacity(0.7);
        row.Children().Append(status_);

        body.Children().Append(row);
        root_.Children().Append(makeSection(L"PEN API", body));
    }

    // ── BRUSH ───────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(4);

        brushLabel_ = makeValue(L"Size 6 px");
        body.Children().Append(brushLabel_);

        auto slider = Slider();
        slider.Minimum(1);
        slider.Maximum(40);
        slider.Value(brushSize_);
        slider.Width(140);
        slider.ValueChanged([this, onBrushSize](auto&&, winrt::Microsoft::UI::Xaml::Controls::Primitives::RangeBaseValueChangedEventArgs const& e) {
            brushSize_ = e.NewValue();
            brushLabel_.Text(hstring{ std::format(L"Size {} px", static_cast<int>(brushSize_)) });
            if (onBrushSize) onBrushSize(brushSize_);
        });
        body.Children().Append(slider);

        root_.Children().Append(makeSection(L"BRUSH", body));
    }

    // ── PEN ─────────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(4);

        auto prox = StackPanel();
        prox.Orientation(Orientation::Horizontal);
        proxDot_ = makeDot();
        prox.Children().Append(proxDot_);
        proxText_ = makeCaption(L"Out");
        prox.Children().Append(proxText_);
        body.Children().Append(prox);

        auto cur = StackPanel();
        cur.Orientation(Orientation::Horizontal);
        cur.Children().Append(makeCaption(L"Cursor:"));
        cursor_ = makeValue(L"--");
        cur.Children().Append(cursor_);
        body.Children().Append(cur);

        root_.Children().Append(makeSection(L"PEN", body));
    }

    // ── BUTTONS ─────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(4);

        auto makeRow = [this](std::vector<std::pair<XamlEllipse*, const wchar_t*>> const& items) {
            auto row = StackPanel();
            row.Orientation(Orientation::Horizontal);
            row.Spacing(2);
            for (auto& [dot, label] : items) {
                *dot = makeDot();
                row.Children().Append(*dot);
                auto c = makeCaption(label);
                c.Margin(ThicknessHelper::FromLengths(0, 0, 10, 0));
                row.Children().Append(c);
            }
            return row;
        };

        body.Children().Append(makeRow({ { &dotTip_, L"Tip" }, { &dotEraser_, L"Era" } }));
        body.Children().Append(makeRow({ { &dotB1_, L"B1" }, { &dotB2_, L"B2" }, { &dotB3_, L"B3" } }));

        buttonsHex_ = makeValue(L"0x00000000");
        body.Children().Append(buttonsHex_);

        root_.Children().Append(makeSection(L"BUTTONS", body));
    }

    // ── POSITION ────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(2);
        auto line = [this, &body](const wchar_t* caption, TextBlock& out) {
            auto row = StackPanel();
            row.Orientation(Orientation::Horizontal);
            auto c = makeCaption(caption);
            c.MinWidth(54);
            row.Children().Append(c);
            out = makeValue(L"--,--");
            row.Children().Append(out);
            body.Children().Append(row);
        };
        line(L"Raw:", posRaw_);
        line(L"Screen:", posScreen_);
        line(L"App:", posApp_);
        line(L"Canvas:", posCanvas_);
        root_.Children().Append(makeSection(L"POSITION", body));
    }

    // ── PRESSURE ────────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(2);
        auto line = [this, &body](const wchar_t* caption, TextBlock& out) {
            auto row = StackPanel();
            row.Orientation(Orientation::Horizontal);
            auto c = makeCaption(caption);
            c.MinWidth(44);
            row.Children().Append(c);
            out = makeValue(L"--");
            row.Children().Append(out);
            body.Children().Append(row);
        };
        line(L"Raw:", prsRaw_);
        line(L"Norm:", prsNorm_);
        root_.Children().Append(makeSection(L"PRESSURE", body));
    }

    // ── ORIENTATION ─────────────────────────────────────────────
    {
        auto body = StackPanel();
        body.Spacing(2);
        auto line = [this, &body](const wchar_t* caption, TextBlock& out) {
            auto row = StackPanel();
            row.Orientation(Orientation::Horizontal);
            auto c = makeCaption(caption);
            c.MinWidth(62);
            row.Children().Append(c);
            out = makeValue(L"--");
            row.Children().Append(out);
            body.Children().Append(row);
        };
        line(L"Azimuth:", oriAz_);
        line(L"Altitude:", oriAl_);
        line(L"Twist:", oriTw_);
        root_.Children().Append(makeSection(L"ORIENTATION", body));
    }
}

void Ribbon::selectApi(int index) {
    if (!api_ || index < 0 || index >= static_cast<int>(apis_.size())) return;
    // Without this the restore raises the same event a click would, opening a session that the
    // caller is about to open itself.
    suppressSelection_ = true;
    api_.SelectedIndex(index);
    suppressSelection_ = false;
}

void Ribbon::setStatus(hstring const& text) {
    if (status_) status_.Text(text);
}

void Ribbon::clearReadout() {
    setDot(proxDot_, false);
    if (proxText_) proxText_.Text(L"Out");
    if (cursor_) cursor_.Text(L"--");

    setDot(dotTip_, false);
    setDot(dotEraser_, false, true);
    setDot(dotB1_, false);
    setDot(dotB2_, false);
    setDot(dotB3_, false);
    if (buttonsHex_) buttonsHex_.Text(L"0x00000000");

    for (auto* t : { &posRaw_, &posScreen_, &posApp_, &posCanvas_ })
        if (*t) t->Text(L"--,--");
    for (auto* t : { &prsRaw_, &prsNorm_, &oriAz_, &oriAl_, &oriTw_ })
        if (*t) t->Text(L"--");
}

void Ribbon::setReadout(PenReadout const& r) {
    if (!r.hasData) { clearReadout(); return; }

    setDot(proxDot_, r.inProximity);
    proxText_.Text(r.inProximity ? L"Proximity" : L"Out");
    cursor_.Text(hstring{ std::format(L"{}", r.cursor) });

    setDot(dotTip_, r.tip);
    setDot(dotEraser_, r.eraser, true);
    setDot(dotB1_, r.barrel1);
    setDot(dotB2_, r.barrel2);
    setDot(dotB3_, r.barrel3);
    buttonsHex_.Text(hstring{ std::format(L"0x{:08X}", r.rawButtons) });

    // Raw carries its unit, because raw_x is a different quantity on each backend. Printing
    // the pair alone invites reading it as a position in the same space as Screen, which on a
    // Wintab digitizer it is not.
    switch (r.rawUnits) {
        case PEN_RAW_NONE:
            posRaw_.Text(L"n/a");
            break;
        case PEN_RAW_TABLET_NATIVE:
            posRaw_.Text(hstring{ std::format(L"{},{} tablet", r.rawX, r.rawY) });
            break;
        case PEN_RAW_SCREEN_PIXELS:
            posRaw_.Text(hstring{ std::format(L"{},{} px", r.rawX, r.rawY) });
            break;
        case PEN_RAW_HIMETRIC:
            posRaw_.Text(hstring{ std::format(L"{},{} 0.01mm", r.rawX, r.rawY) });
            break;
    }

    posScreen_.Text(hstring{ std::format(L"{:.1f},{:.1f}", r.screenX, r.screenY) });
    posApp_.Text(hstring{ std::format(L"{:.1f},{:.1f}", r.appX, r.appY) });
    posCanvas_.Text(hstring{ std::format(L"{:.1f},{:.1f}", r.canvasX, r.canvasY) });

    prsRaw_.Text(hstring{ std::format(L"{} / {}", r.rawPressure, r.maxPressure) });
    prsNorm_.Text(hstring{ std::format(L"{:.3f}",
        r.maxPressure > 0 ? static_cast<double>(r.rawPressure) / r.maxPressure : 0.0) });

    oriAz_.Text(hstring{ std::format(L"{:.1f} deg", r.azimuth) });
    oriAl_.Text(hstring{ std::format(L"{:.1f} deg", r.altitude) });
    oriTw_.Text(hstring{ std::format(L"{:.1f} deg", r.twist) });
}

} // namespace scribble
