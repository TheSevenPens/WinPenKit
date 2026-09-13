# Scribble Apps

Eight demo apps. Seven prove the WinPenKit SDK end-to-end, with bitmap-backed rendering, a ribbon toolbar with API dropdown, brush size slider, clear button, pressure-sensitive drawing, and four-coordinate position display (Raw → Screen → App → Canvas).

`Scribble.Qt` is the exception and is here on purpose: it uses Qt's own stylus handling and no WinPenKit at all. Seven samples sharing one library can agree with each other and still be wrong together, so an independent implementation is what makes a measurement a statement about Windows rather than about this repository. It is also the closest thing here to what Krita sees, since Krita consumes `QTabletEvent` the same way.

> Every app here accepts `--selftest` and `--replay`, which verify the environment, the drawing
> surface and the coordinate conversion with no tablet and no person. Every one also accepts
> `--record <path>`, which captures a live pen stream in the format `--replay` reads. See
> [SELF-TEST.md](SELF-TEST.md).
>
> `Scribble.Qt` answers the same checks with the same ids, which is the only reason its numbers
> can be put beside the others'.

## Summary

| App | Framework | Renderer | Language | Backends |
|---|---|---|---|---|
| Scribble.Win32 | Win32/GDI | GDI BitBlt | C++ | System, Digitizer, WM_Pointer |
| Scribble.Rust | egui | tiny-skia | Rust | System, Digitizer, WM_Pointer |
| Scribble.WinUI | WinUI 3 | SkiaSharp | C# | System, Digitizer, WinUI Pointer |
| Scribble.Wpf | WPF | SkiaSharp | C# | System, Digitizer, WPF Stylus |
| Scribble.WinForms | WinForms | SkiaSharp | C# | System, Digitizer, WinForms Pointer |
| Scribble.Avalonia | Avalonia | SkiaSharp | C# | System, Digitizer, Avalonia Pointer |
| WinPenKit.TestConsole | Console | (headless) | C# | System, Digitizer |
| Scribble.Qt | Qt 6 Widgets | QPainter / QImage | C++ | **no WinPenKit** — Qt WM_Pointer or Qt WinTab |

## How these samples lay down ink, and why it bounds a Krita comparison

Every app here draws a straight line between consecutive pen events, with width scaled by that
event's pressure. One event pair, one line segment. That is what they were built to do and it is
not a defect, but it is worth knowing before reading them as a rendering reference.

Krita does not work that way. Its paint operation carries a `KisDistanceInformation` **across**
events. `KisPaintOpUtils::paintLine` loops while `getNextPointPosition` returns a non-negative
value, painting one dab per iteration, so **one event segment produces zero, one or many dabs**,
and the next event inherits whatever distance was left over.

The isotropic spacing rule is `max(0.5, s) - a`, where `s` is the current spacing, `a` is the
distance accumulated since the last dab, and the 0.5 is `MIN_DISTANCE_SPACING`. If that remainder
fits inside the segment, a dab is painted there and the accumulator resets. If the segment is
shorter, its length is added to the accumulator and the call returns −1, painting nothing. The
accumulator is a member of the distance object and is reset only when a dab is actually painted.

Spacing is brush-dependent rather than a fixed pixel count — for the Pixel Brush, pressure
changes dab size and spacing together. There is also an anisotropic path solving against an
ellipse, and a timed path for stationary airbrush work; `getNextPointPosition` computes a
distance factor and a time factor and takes the minimum of whichever are valid.

**The property is segmentation invariance.** One 10-unit segment and ten 1-unit segments produce
dabs in the same places — 2.5, 5, 7.5, 10. Per-event painting does not have that property, and a
deliberately broken control that resets the accumulator on every event produces no dabs at all.

**No Krita setting draws the way these samples draw.** `NO_SMOOTHING` still goes through the
spacing loop: it calls `paintLine` directly on consecutive points, skipping the Bezier
interpolation and tangent work the other modes do. Smoothing selects *which* points reach the
painter, not whether spacing applies.

### What this bounds

Comparing coordinates in against coordinates out is unaffected — that is what issue #92 does. But
the moment anyone compares how the **ink looks**, against Krita or anything else, they are
measuring this renderer's naivety rather than the pen stack. That is a confound worth knowing
before the comparison rather than after.

It also explains something that would otherwise puzzle. At high pen speed these samples produce
visibly sparser, more angular ink than Krita from the same input, because one fast event pair
yields a single long thin segment where Krita yields a run of evenly spaced dabs.

A dab-spacing renderer is a drawing application's job rather than a pen input library's, so
WinPenKit is unlikely to grow one.

> Verified against `KDE/krita` at commit `1e6586cb`, the generic Pixel Brush path — the Pixel
> Brush has a sharpness-enabled 1×1 branch that draws a DDA line instead, and other paint engines
> can override painting, so this is the ordinary path rather than every preset. Research origin:
> `TheSevenPens/devnotes#23`, issue #103.

## Scribble.Win32

Minimal C++ Win32/GDI scribble app. Zero framework dependencies — just the Windows API and `WinPenKit.Native.dll`.

- DPI-aware (`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`)
- Double-buffered painting (`WM_ERASEBKGND` suppressed, offscreen bitmap)
- Ribbon built with native Win32 controls (ComboBox, TrackBar, Button) positioned via `layout_controls()`
- Child controls get DPI-scaled fonts via `WM_SETFONT`
- `WS_CLIPCHILDREN` prevents control flicker during 60fps repaints

## Scribble.Rust

Rust drawing app using the `pen_session` C API via FFI.

- **egui** immediate-mode UI — ribbon with API dropdown, brush slider, clear button, pen telemetry
- **tiny-skia** bitmap-backed rendering — `Pixmap` + `stroke_path` with round caps, uploaded as egui texture
- Safe `WinPenKit` wrapper with RAII `Drop` cleanup
- DPI-aware coordinate conversion (physical desktop pixels → egui logical points via `pixels_per_point`)
- HWND obtained via `GetActiveWindow()` on first frame for WM_Pointer support
- Ribbon uses `exact_height(130.0)` to prevent layout jumping when telemetry values change width

Depends on `WinPenKit.Native.dll` at runtime. Pure Rust otherwise — no C/C++ compilation needed. Total dependency footprint ~500 KB.

## Scribble.WinUI

WinUI 3 drawing app with the most detailed ribbon UI.

- Consolidated `ScribbleRibbon` toolbar with labeled sections:
  - **APP** — input API selector, Clear button, log link
  - **BRUSH** — size slider (1–500 px)
  - **PEN** — proximity indicator, cursor type
  - **BUTTONS** — tip, eraser, barrel 1/2/3 status indicators with raw hex value
  - **POSITION** — Raw, Screen, App, Canvas coordinates
  - **PRESSURE** — raw value and normalized percentage
  - **ORIENTATION** — azimuth, altitude, twist, tiltX, tiltY (all in degrees)
- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, copied to WinUI `WriteableBitmap` via `IBuffer.AsStream()`
- Correct DPI handling on high-DPI multi-monitor setups (225%+ scaling)
- Digitizer hi-res mode preserving full tablet-native precision (~5280 LPI)
- Unpackaged app — requires DPI manifest in `app.manifest`

Uses `WinPenKit` + `WinPenKit.WinUI` via `PenSessionWinUI3` wrapper for desktop → canvas DIP conversion.

## Scribble.Wpf

WPF drawing app with SkiaSharp rendering.

- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied to WPF `WriteableBitmap` via `Buffer.MemoryCopy`
- `PointFromScreen` for automatic DPI-correct coordinate conversion
- WPF Stylus backend uses `StylusMove`/`StylusDown` events

Uses `WinPenKit` + `WinPenKit.Wpf`.

## Scribble.WinForms

WinForms drawing app with SkiaSharp rendering.

- **FlowLayoutPanel** ribbon — DPI-scales automatically, no hardcoded pixel positions
- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied via `Bitmap.LockBits`
- **DoubleBufferedPanel** subclass for flicker-free painting
- WinForms Pointer backend uses `IMessageFilter` (not `NativeWindow.AssignHandle` which crashes on Form HWNDs)

Uses `WinPenKit` + `WinPenKit.WinForms`.

## Scribble.Avalonia

Avalonia drawing app with SkiaSharp rendering.

- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied to Avalonia `WriteableBitmap`
- Coordinate conversion by hand, **not** via `TopLevel.PointToClient()` — that takes a `PixelPoint`, whose members are integers, and so forces the pen position onto the whole-pixel grid. Only the window origin goes through it, since that is genuinely on a pixel boundary

Uses `WinPenKit` + `WinPenKit.Avalonia`.

## WinPenKit.TestConsole

Headless console app for verifying Wintab backends without a GUI. Useful for debugging session creation, packet delivery, and telemetry values.

- Discovers available APIs via `PenSessionFactory.GetAvailableApis()`
- Interactive API selection
- Prints live pen data at 10 Hz (position, pressure, buttons, cursor)
- WM_Pointer not available (no window handle) — correctly reports the error

## Scribble.Qt

Qt 6 Widgets, `QTabletEvent`, and no WinPenKit. Full notes in
[Scribble.Qt/README.md](../Scribble.Qt/README.md); the parts that change how you read its output:

- **The pen API is fixed at startup, so the dropdown saves rather than switches.** Qt decides
  between WM_POINTER and WinTab while the Windows platform plugin initialises. Choosing one
  stores it and shows *Restart to use Wintab, tablet-native. Still on WM_Pointer.* until the
  next launch, which then comes up on the stored choice. `--wintab` and `--pointer` override it
  for a single run without changing what is stored. Krita's UI works the same way.
- **`L0.pen-api` is in the report**, because the application cannot ask Qt afterwards which
  path it got, so a run that did not record it has no way to say.
- **Qt's WinTab context is always tablet-native.** `--wintab` is comparable to `WintabDigitizer`
  and never to `WintabSystem`. There is no low-resolution option in Qt, and therefore none in
  Krita.
- **It shares `Scribble.Win32/src/selftest.h`** and nothing else. Same check ids, same report
  lines, same recording format, no WinPenKit dependency.
- Built with CMake against an external Qt 6; it is not in either solution.
