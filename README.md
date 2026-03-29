# WinPenSession

This is a unified pen input SDK for modern Windows writen for someone devleoping a drawing application or something similar. It is SIMPLE and EASY.


# Benefits
- You don't need to know anything about the complications of Windows Pen Input
- You don't need to know anything about the complications of WinTab drivers
- Both managed and unmanaged libraries are provided so you can use the languages you want
- Can switch APIs in your apps dynamically without even restarting the app
- Supports WinTab high-resolution 

## Packages

| Package | Purpose | Works in |
|---|---|---|
| **PenSession** | Core library (Wintab + WM_POINTER) | Any .NET app |
| **PenSession.Native** | C++ DLL with C ABI | Any native app (C++, Rust, Zig) |
| **PenSession.WinUI** | WinUI 3 pointer events | WinUI 3 apps |
| **PenSession.Wpf** | WPF stylus events | WPF apps |
| **PenSession.WinForms** | WinForms IMessageFilter | WinForms apps |
| **PenSession.Avalonia** | Avalonia pointer events | Avalonia apps |

## Scribble Apps

These demo apps proving the SDK end-to-end, all with bitmap-backed rendering and ribbon UI:

| App | Framework | Renderer | Language |
|---|---|---|---|
| Scribble.Win32 | Win32/GDI | GDI BitBlt | C++ |
| Scribble.Rust | egui | tiny-skia | Rust |
| Scribble.WinUI | WinUI 3 | SkiaSharp | C# |
| Scribble.Wpf | WPF | SkiaSharp | C# |
| Scribble.WinForms | WinForms | SkiaSharp | C# |
| Scribble.Avalonia | Avalonia | SkiaSharp | C# |
| PenSession.TestConsole | Console | (headless) | C# |

## Quick Start (C#)

```csharp
using PenSession;

// Discover available APIs.
var apis = PenSessionFactory.GetAvailableApis();

// Create and start a session.
using var session = PenSessionFactory.Create(apis[0]);
session.Start();

// Poll on a render timer (~60fps).
var points = session.DrainPoints();
foreach (var pt in points)
{
    // pt.DesktopX/Y — physical screen pixels
    // pt.Pressure — 0 to session.MaxPressure
    // pt.Azimuth, pt.TiltX — both tilt representations
}
```

## Quick Start (C++/Rust)

```cpp
#include "pen_session.h"

PenInputApi apis[8];
int count = pen_session_get_available_apis(apis, 8);

PenSessionHandle session = pen_session_create(apis[0]);
pen_session_start(session, app_hwnd);

PenPoint points[64];
int n = pen_session_drain_points(session, points, 64);

pen_session_destroy(session);
```

## Documentation

See the [Docs/](Docs/) folder for:
- [GETTING-STARTED.md](Docs/GETTING-STARTED.md) — Project overview and setup
- [HOW_TO_USE.md](Docs/HOW_TO_USE.md) — Usage guide with gotchas and best practices
- [SCRIBBLE-APPS.md](Docs/SCRIBBLE-APPS.md) — Details on each scribble demo app
- [BUILD.md](Docs/BUILD.md) — Build instructions
- [Planning/](Docs/Planning/) — Architecture decisions, phase plan, NuGet plan, rendering notes

For general pen input knowledge (API comparisons, DPI handling, Wintab gotchas), see the [devnotes](https://github.com/TheSevenPens/devnotes) repo.

## History

This project was extracted from [Wacom_WinTabDN](https://github.com/TheSevenPens/Wacom_WinTabDN), which contains the low-level WintabDN .NET library. The design evolved through 6 phases from a Wintab-specific session into a unified multi-API, multi-framework, multi-language pen input SDK.

## License

The text and information contained in this repository may be freely used, copied, or distributed without compensation or licensing restrictions.
