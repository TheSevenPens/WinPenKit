#pragma once

#include "pch.h"
#include "pensession.h"

#include <functional>
#include <vector>

namespace scribble {

/// The standard Scribble ribbon: the same seven sections, in the same order, with the same
/// labels as the other seven samples, so a reading here sits beside theirs without
/// translation.
///
/// Built in code rather than XAML markup, for the reasons in main.cpp. Every control is the
/// same `Microsoft.UI.Xaml` type a `.xaml` file would produce.
///
/// Three fields differ, and differ because this backend cannot answer them rather than because
/// the sample chose not to. They read as unavailable rather than being filled with a stand-in:
///
///  * **Raw position** on the WinUI pointer path. WinUI exposes no device-native coordinate,
///    and `Conventions.RawUnits` reports `None`. Wintab fills it normally.
///  * **B2 and B3** on any pointer backend. The pointer button encoding is a bitmask with no
///    per-button identity, so a second or third side switch cannot be told from the first.
///    They read false because the backend cannot say, not because they are up.
///  * **Pressure Raw** on the WinUI pointer path, which is reconstructed against 1024 -- the
///    API's fixed range, not the device's.
class Ribbon {
public:
    /// `apis` is what the dropdown offers; `onApiSelected` is raised with the chosen one. The
    /// ribbon performs no switching itself.
    Ribbon(std::vector<PenInputApi> apis,
           std::function<void(PenInputApi)> onApiSelected,
           std::function<void()> onClear,
           std::function<void(double)> onBrushSize);

    /// A FrameworkElement rather than a UIElement, because Grid::SetRow takes one and a
    /// caller should not have to cast to place the ribbon in a row.
    winrt::Microsoft::UI::Xaml::FrameworkElement root() const { return root_; }

    /// Selects an entry without raising `onApiSelected`, for restoring a saved choice.
    void selectApi(int index);

    /// What the ribbon shows when a session opened, or failed to.
    void setStatus(winrt::hstring const& text);

    void setReadout(PenReadout const& r);
    void clearReadout();

    double brushSize() const { return brushSize_; }

private:
    winrt::Microsoft::UI::Xaml::Controls::TextBlock makeValue(winrt::hstring const& initial);
    winrt::Microsoft::UI::Xaml::Controls::TextBlock makeCaption(winrt::hstring const& text);
    winrt::Microsoft::UI::Xaml::Controls::Border makeSection(
        winrt::hstring const& header, winrt::Microsoft::UI::Xaml::UIElement const& body);
    winrt::Microsoft::UI::Xaml::Shapes::Ellipse makeDot();
    static void setDot(winrt::Microsoft::UI::Xaml::Shapes::Ellipse const& dot,
                       bool on, bool eraserColour = false);

    winrt::Microsoft::UI::Xaml::Controls::StackPanel root_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::ComboBox api_{ nullptr };

    std::vector<PenInputApi> apis_;
    std::function<void(PenInputApi)> onApiSelected_;
    bool suppressSelection_ = false;

    winrt::Microsoft::UI::Xaml::Controls::TextBlock status_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::TextBlock brushLabel_{ nullptr };
    double brushSize_ = 6.0;

    winrt::Microsoft::UI::Xaml::Shapes::Ellipse proxDot_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::TextBlock proxText_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::TextBlock cursor_{ nullptr };

    winrt::Microsoft::UI::Xaml::Shapes::Ellipse dotTip_{ nullptr }, dotEraser_{ nullptr };
    winrt::Microsoft::UI::Xaml::Shapes::Ellipse dotB1_{ nullptr }, dotB2_{ nullptr }, dotB3_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::TextBlock buttonsHex_{ nullptr };

    winrt::Microsoft::UI::Xaml::Controls::TextBlock posRaw_{ nullptr }, posScreen_{ nullptr };
    winrt::Microsoft::UI::Xaml::Controls::TextBlock posApp_{ nullptr }, posCanvas_{ nullptr };

    winrt::Microsoft::UI::Xaml::Controls::TextBlock prsRaw_{ nullptr }, prsNorm_{ nullptr };

    winrt::Microsoft::UI::Xaml::Controls::TextBlock oriAz_{ nullptr }, oriAl_{ nullptr }, oriTw_{ nullptr };
};

/// The API label the dropdown shows. Taken from the library for the native backends so this
/// sample cannot drift from the others over how one is named; WinUI's own pointer path is not
/// a session the library can open, so it is named here.
winrt::hstring apiDisplayName(PenInputApi api);

} // namespace scribble
