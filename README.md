# WinPenKit

This is a unified pen input SDK for modern Windows writen for someone devleoping a drawing application or something similar. It is SIMPLE and EASY.


# Benefits
- You don't need to know anything about the complications of Windows Pen Input
- You don't need to know anything about the complications of WinTab drivers
- Both managed and unmanaged libraries are provided so you can use the languages you want
- Can switch APIs in your apps dynamically without even restarting the app
- One capture region can give every API the same spatial scope: Wintab, WM_POINTER and framework pointer events then deliver pen data over the same area
- Supports WinTab high-resolution 

## Packages

| Package | Purpose | Works in |
|---|---|---|
| **WinPenKit** | Core library (Wintab + WM_POINTER) | Any .NET app |
| **WinPenKit.Native** | C++ DLL with C ABI | Any native app (C++, Rust, Zig) |
| **WinPenKit.WinUI** | WinUI 3 pointer events | WinUI 3 apps |
| **WinPenKit.Wpf** | WPF stylus events | WPF apps |
| **WinPenKit.WinForms** | WinForms IMessageFilter | WinForms apps |
| **WinPenKit.Avalonia** | Avalonia pointer events | Avalonia apps |

## Scribble Apps

Demo apps that exercise the SDK end-to-end, all with bitmap-backed rendering and ribbon UI. See [Docs/SCRIBBLE-APPS.md](Docs/SCRIBBLE-APPS.md).

| App | Framework | Renderer | Language |
|---|---|---|---|
| Scribble.Win32 | Win32/GDI | GDI BitBlt | C++ |
| Scribble.Rust | egui | tiny-skia | Rust |
| Scribble.WinUI | WinUI 3 | SkiaSharp | C# |
| Scribble.WinUINative | WinUI 3 (C++/WinRT) | Skia C API | C++ |
| Scribble.Wpf | WPF | SkiaSharp | C# |
| Scribble.WinForms | WinForms | SkiaSharp | C# |
| Scribble.Avalonia | Avalonia | SkiaSharp | C# |
| Scribble.Qt | Qt 6 Widgets | QPainter | C++ (Qt's own tablet support, no WinPenKit, for comparison) |

## Tools

| Project | Purpose |
|---|---|
| WinPenKit.TestConsole | Console host for the Wintab sessions, the clock self-test, and the Wintab epoch and mapping probes |
| WinPenKit.MappingWizard | Checks whether each pen API puts the pen under the cursor across display and tablet configurations ([Docs/MAPPING-WIZARD.md](Docs/MAPPING-WIZARD.md)) |
| WinPenKit.SweepProbe | Measures what a tablet's coordinates mean: the whole range the pen reaches against the cursor, millimetres a pixel, and any cropping ([Docs/PEN-SWEEP.md](Docs/PEN-SWEEP.md)) |
| Samples/ContextCount | Plain C programs that read and leak Wintab contexts ([Docs/WINTAB-CONTEXT-LEAK.md](Docs/WINTAB-CONTEXT-LEAK.md)) |

### Verifying a build

Every Scribble app self-checks with no tablet, no pen input and no person looking at the screen:

```bash
Scribble.Wpf.exe --selftest     # environment and drawing surface
Scribble.Wpf.exe --replay       # the above, plus the coordinate conversion
```

Both print a line-oriented report and **exit 0 only if every check passes**, so CI or an agent can branch on the exit code.

```
[PASS] L1.surface-physical     bitmap 2672x1230, expected 2672x1230 (= ceil(1188x547 logical x 2.25))
[PASS] L3.conversion-lossless  mean turn angle in 0.74 deg, out 0.74 deg (delta 0.00)
RESULT 9/9 passed
```

These catch the two bug classes that make strokes look wrong while every obvious check still
passes: a canvas quietly rendering at a fraction of the display's resolution, and a coordinate
conversion quantizing the pen position to whole pixels. Both are invisible on screen until you
know to look.

They are not a substitute for drawing with a real pen — Wintab needs a tablet, and whether a
stroke *looks* right is not machine-checkable. See **[Docs/SELF-TEST.md](Docs/SELF-TEST.md)**
for what each check catches and what is deliberately left uncovered.

## Quick Start (C#)

```csharp
using WinPenKit;

// Discover available APIs. (A WPF, WinForms, Avalonia or WinUI app calls its
// framework package's GetAvailable() instead.)
var apis = PenSessionFactory.GetAvailableApis();

// Create a session and start it with your window handle. Wintab uses the window as its
// default capture region; with no window it reports the pen anywhere on the desktop.
var session = PenSessionFactory.Create(apis[0]);
var error = session.Start(hwnd);
if (error != null) { /* show error */ }

// Tell the session when your window is activated, or Wintab loses the first stroke
// after the user returns from another application.
Activated += (_, _) => session.OnActivated();

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
const char* error = pen_session_start(session, app_hwnd);   // NULL on success
if (error) { /* show error */ }

// In WM_ACTIVATE, when LOWORD(wParam) != WA_INACTIVE:
pen_session_on_activated(session);

PenPoint points[64];
int n = pen_session_drain_points(session, points, 64);

pen_session_destroy(session);
```

## Documentation

See the [Docs/](Docs/) folder for:
- [WINTAB-METADATA.md](Docs/WINTAB-METADATA.md) — device names, raw PnP IDs and Wintab versions without opening a pen session
- [GETTING-STARTED.md](Docs/GETTING-STARTED.md): Project overview and setup
- [HOW_TO_USE.md](Docs/HOW_TO_USE.md): Usage guide with gotchas and best practices
- [ARCHITECTURE.md](Docs/ARCHITECTURE.md): How WinPenKit works, backend by backend: delivery, timing, position mapping, values, and managed versus native
- [STYLUS.md](Docs/STYLUS.md): The Wintab and WM_POINTER input paths, and runtime switching between them
- [PEN-SWEEP.md](Docs/PEN-SWEEP.md): Measuring what a tablet's coordinates mean, and what was learned doing it
- [TIMESTAMPS.md](Docs/TIMESTAMPS.md): How each backend's timestamp was measured, and how wrapping counters are handled
- [SCRIBBLE-APPS.md](Docs/SCRIBBLE-APPS.md): Details on each scribble demo app
- [SELF-TEST.md](Docs/SELF-TEST.md): `--selftest` and `--replay`: what they check, and what they do not
- [MAPPING-WIZARD.md](Docs/MAPPING-WIZARD.md): Checking where each pen API puts the pen across display and tablet configurations
- [WINTAB-CONTEXT-LEAK.md](Docs/WINTAB-CONTEXT-LEAK.md): Wintab contexts left behind by processes that do not close them, and what that costs a developer
- [WINTAB-INVESTIGATION-2026-09.md](Docs/WINTAB-INVESTIGATION-2026-09.md): The experiment record behind the context-leak findings
- [WINTAB-MAPPING-PRIOR-ART.md](Docs/WINTAB-MAPPING-PRIOR-ART.md): How other open-source applications map Wintab positions
- [FUTURES.md](Docs/FUTURES.md): Known issues and ideas
- [WinTabUtils](https://github.com/TheSevenPens/WinTabUtils): A separate repository with a window showing how many Wintab contexts a driver has open, and a button to restart the Wacom driver.
- [BUILD.md](Docs/BUILD.md): Build instructions
- [CI.md](Docs/CI.md): CI/Release workflow, versioning, and releasing
- [Planning/](Docs/Planning/): NuGet publishing plan

For general pen input knowledge (API comparisons, DPI handling, Wintab gotchas), see the [devnotes](https://github.com/TheSevenPens/devnotes) repo.

## History

This project was extracted from [Wacom_WinTabDN](https://github.com/TheSevenPens/Wacom_WinTabDN), which contains the low-level WintabDN .NET library. The design evolved through 6 phases from a Wintab-specific session into a unified multi-API, multi-framework, multi-language pen input SDK.

## License

The text and information contained in this repository may be freely used, copied, or distributed without compensation or licensing restrictions.
