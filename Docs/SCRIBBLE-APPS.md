# Scribble Apps

Seven demo apps proving the PenSession SDK end-to-end. All feature bitmap-backed rendering, a ribbon toolbar with API dropdown, brush size slider, clear button, pressure-sensitive drawing, and four-coordinate position display (Raw → Screen → App → Canvas).

## Summary

| App | Framework | Renderer | Language | Backends |
|---|---|---|---|---|
| Scribble.Win32 | Win32/GDI | GDI BitBlt | C++ | System, Digitizer, WM_Pointer |
| Scribble.Rust | egui | tiny-skia | Rust | System, Digitizer, WM_Pointer |
| Scribble.WinUI | WinUI 3 | SkiaSharp | C# | System, Digitizer, WinUI Pointer |
| Scribble.Wpf | WPF | SkiaSharp | C# | System, Digitizer, WPF Stylus |
| Scribble.WinForms | WinForms | SkiaSharp | C# | System, Digitizer, WinForms Pointer |
| Scribble.Avalonia | Avalonia | SkiaSharp | C# | System, Digitizer, Avalonia Pointer |
| PenSession.TestConsole | Console | (headless) | C# | System, Digitizer |

## Scribble.Win32

Minimal C++ Win32/GDI scribble app. Zero framework dependencies — just the Windows API and `PenSession.Native.dll`.

- DPI-aware (`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`)
- Double-buffered painting (`WM_ERASEBKGND` suppressed, offscreen bitmap)
- Ribbon built with native Win32 controls (ComboBox, TrackBar, Button) positioned via `layout_controls()`
- Child controls get DPI-scaled fonts via `WM_SETFONT`
- `WS_CLIPCHILDREN` prevents control flicker during 60fps repaints

## Scribble.Rust

Rust drawing app using the `pen_session` C API via FFI.

- **egui** immediate-mode UI — ribbon with API dropdown, brush slider, clear button, pen telemetry
- **tiny-skia** bitmap-backed rendering — `Pixmap` + `stroke_path` with round caps, uploaded as egui texture
- Safe `PenSession` wrapper with RAII `Drop` cleanup
- DPI-aware coordinate conversion (physical desktop pixels → egui logical points via `pixels_per_point`)
- HWND obtained via `GetActiveWindow()` on first frame for WM_Pointer support
- Ribbon uses `exact_height(130.0)` to prevent layout jumping when telemetry values change width

Depends on `PenSession.Native.dll` at runtime. Pure Rust otherwise — no C/C++ compilation needed. Total dependency footprint ~500 KB.

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

Uses `PenSession` + `PenSession.WinUI` via `PenSessionWinUI3` wrapper for desktop → canvas DIP conversion.

## Scribble.Wpf

WPF drawing app with SkiaSharp rendering.

- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied to WPF `WriteableBitmap` via `Buffer.MemoryCopy`
- `PointFromScreen` for automatic DPI-correct coordinate conversion
- WPF Stylus backend uses `StylusMove`/`StylusDown` events

Uses `PenSession` + `PenSession.Wpf`.

## Scribble.WinForms

WinForms drawing app with SkiaSharp rendering.

- **FlowLayoutPanel** ribbon — DPI-scales automatically, no hardcoded pixel positions
- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied via `Bitmap.LockBits`
- **DoubleBufferedPanel** subclass for flicker-free painting
- WinForms Pointer backend uses `IMessageFilter` (not `NativeWindow.AssignHandle` which crashes on Form HWNDs)

Uses `PenSession` + `PenSession.WinForms`.

## Scribble.Avalonia

Avalonia drawing app with SkiaSharp rendering.

- **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` to `SKBitmap`, pixel-copied to Avalonia `WriteableBitmap`
- Coordinate conversion via `TopLevel.PointToClient()` and `TranslatePoint()`

Uses `PenSession` + `PenSession.Avalonia`.

## PenSession.TestConsole

Headless console app for verifying Wintab backends without a GUI. Useful for debugging session creation, packet delivery, and telemetry values.

- Discovers available APIs via `PenSessionFactory.GetAvailableApis()`
- Interactive API selection
- Prints live pen data at 10 Hz (position, pressure, buttons, cursor)
- WM_Pointer not available (no window handle) — correctly reports the error
