# WinPenSession

Unified pen input SDK for Windows. Abstracts over Wintab, WM_POINTER, and framework-native pen APIs behind a single `IPenSession` interface with runtime API switching — no restart required.

## Features

- **7 input backends**: Wintab System, Wintab Digitizer (hi-res), WM_POINTER, WinUI Pointer, WPF Stylus, WinForms Pointer, Avalonia Pointer
- **Runtime API switching** via dropdown — unlike Krita/Qt which require a restart
- **Framework-agnostic core** + framework-specific extensions
- **Both managed (C#) and native (C++) implementations**
- **Rust FFI bindings** with safe wrapper
- **Consistent PenPoint data** across all APIs — desktop coordinates, both tilt representations (Azimuth/Altitude + TiltX/TiltY), all in tenths of degree

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

Seven demo apps proving the SDK end-to-end, all with bitmap-backed rendering and ribbon UI:

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
- [FUTURES_UNIFIED_SESSION.md](Docs/FUTURES_UNIFIED_SESSION.md) — Architecture and design decisions
- [HOW_TO_USE.md](Docs/HOW_TO_USE.md) — Usage guide with gotchas and best practices
- [FUTURES_WPF_RENDERING.md](Docs/FUTURES_WPF_RENDERING.md) — Rendering approach across frameworks

For general pen input knowledge (API comparisons, DPI handling, Wintab gotchas), see the [devnotes](https://github.com/TheSevenPens/devnotes) repo.

## History

This project was extracted from [Wacom_WinTabDN](https://github.com/TheSevenPens/Wacom_WinTabDN), which contains the low-level WintabDN .NET library. The design evolved through 6 phases from a Wintab-specific session into a unified multi-API, multi-framework, multi-language pen input SDK.

## License

The text and information contained in this repository may be freely used, copied, or distributed without compensation or licensing restrictions.
