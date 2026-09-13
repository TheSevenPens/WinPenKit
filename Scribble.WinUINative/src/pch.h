#pragma once

// C++/WinRT and WinUI 3 headers are large and included everywhere; the precompiled header is
// not an optimisation here so much as the difference between a build and a wait.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <unknwn.h>
#include <restrictederrorinfo.h>
#include <hstring.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.Media.Imaging.h>
#include <winrt/Microsoft.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Text.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Input.h>
#include <winrt/Microsoft.UI.Content.h>
#include <winrt/Windows.UI.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.h>

#include <cmath>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>
