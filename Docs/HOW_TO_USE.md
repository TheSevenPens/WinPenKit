# How to Use WinPenKit

A guide for developers building pen-enabled applications with the WinPenKit library.

## Quick Start (C#)

```csharp
using WinPenKit;

// 1. Discover available APIs.
var apis = PenSessionFactory.GetAvailableApis();

// 2. Create and start a session.
using var session = PenSessionFactory.Create(apis[0]);
var error = session.Start();
if (error != null)
{
    Console.WriteLine($"Start failed: {error}");
    return;
}

// 3. Poll on a render timer (~60 fps).
var points = session.DrainPoints();
foreach (var pt in points)
{
    // pt.DesktopX/Y  — physical screen pixels (double)
    // pt.Pressure    — 0 to session.MaxPressure
    // pt.Azimuth/Altitude — spherical tilt (degrees)
    // pt.TiltX/TiltY — planar tilt (degrees)
}

// 4. Switch APIs at runtime — no restart needed.
session.Stop();
session.Dispose();
var newSession = PenSessionFactory.Create(InputApi.WintabDigitizer);
newSession.Start();
```

## Quick Start (C++ / Rust)

```cpp
#include "pen_session.h"

// Discover and create.
PenInputApi apis[8];
int count = pen_session_get_available_apis(apis, 8);
PenSessionHandle session = pen_session_create(apis[0]);

// Start (pass HWND for WM_POINTER, NULL for Wintab).
pen_session_start(session, app_hwnd);

// Poll.
PenPoint points[64];
int n = pen_session_drain_points(session, points, 64);

// Cleanup.
pen_session_destroy(session);
```

## Framework-Specific Sessions

The factory creates framework-agnostic sessions (Wintab, WM_POINTER). Framework-specific sessions require UI elements and must be created directly:

```csharp
// WinUI 3:
IPenSession session = new WinUiPointerSession(canvasElement, hwnd);

// WPF:
IPenSession session = new WpfStylusSession(canvasElement);

// WinForms:
IPenSession session = new WinFormsPointerSession(form);

// Avalonia:
IPenSession session = new AvaloniaPointerSession(control);
```

All implement `IPenSession` — the polling code is identical regardless of backend.

## PenPoint Fields

Every `PenPoint` contains:

| Field | Type | Description |
|---|---|---|
| `DesktopX/Y` | `double` | Physical screen pixels. Sub-pixel precision in digitizer mode. |
| `RawX/Y` | `int` | Raw values from the API. Tablet-native in digitizer mode, screen pixels otherwise. |
| `Pressure` | `uint` | Raw tip pressure. 0 = hovering. Normalize: `(float)pt.Pressure / session.MaxPressure` |
| `Azimuth` | `double` | Spherical: compass direction in degrees (0.0–360.0). |
| `Altitude` | `double` | Spherical: angle from surface in degrees (0.0–90.0). 90 = perpendicular. |
| `TiltX` | `double` | Planar: tilt right/left in degrees (-90.0 to +90.0). |
| `TiltY` | `double` | Planar: tilt toward/away in degrees (-90.0 to +90.0). |
| `Twist` | `double` | Barrel rotation in degrees (0.0–360.0). |
| `Z` | `int` | Height above tablet surface. 0 if unsupported. |
| `Buttons` | `uint` | Button state. Wintab: `(action << 16) \| buttonNumber`. |
| `Cursor` | `uint` | Cursor type. 13 = pen tip, 14 = eraser (Wacom). |
| `Source` | `InputApi` | Which backend produced this point. |

Both tilt representations are always present — Wintab backends compute TiltX/TiltY from Azimuth/Altitude, and WM_POINTER backends compute Azimuth/Altitude from TiltX/TiltY.

## Coordinate Conversion

PenPoint provides desktop screen pixels. Your app converts to canvas-local coordinates:

| Framework | Conversion |
|---|---|
| **WinForms** | `panel.PointToClient(new Point((int)pt.DesktopX, (int)pt.DesktopY))` |
| **WPF** | `element.PointFromScreen(new Point(pt.DesktopX, pt.DesktopY))` |
| **WinUI 3** | `(desktopX - clientOrigin) × (96/DPI) - canvasPosition` (see DPI notes below) |
| **Avalonia** | `topLevel.PointToClient(new PixelPoint((int)pt.DesktopX, (int)pt.DesktopY))` |
| **Win32** | `ScreenToClient(hwnd, &pt)` |
| **egui (Rust)** | `desktop / pixels_per_point - window_pos` |

## DPI Handling

Wintab always reports physical screen pixels. Your app must be **Per-Monitor V2 DPI aware** for coordinates to match:

- **WinForms (.NET 10)**: Automatic — `PointToClient()` handles DPI.
- **WPF (.NET 10)**: Automatic — `PointFromScreen()` handles DPI.
- **WinUI 3**: Must call `ClientToScreen` inside a `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` block.
- **Win32 C++**: Call `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` before creating windows.
- **WinUI 3 unpackaged**: Add `<dpiAwareness>PerMonitorV2</dpiAwareness>` to `app.manifest` or the UI is blurry.

See the [devnotes DPI article](https://github.com/TheSevenPens/devnotes) for the full deep-dive.

## Buttons and Eraser

### Use `PenButtonTracker` (recommended)

`PenPoint.Buttons` carries different encodings depending on the backend:

- **Wintab**: one event per packet, `(action << 16) | buttonNumber`. Action: 0=none, 1=released, 2=pressed. Button: 0=tip, 1-3=barrel.
- **Pointer-style backends** (WM_POINTER, WinUI Pointer, WPF Stylus, WinForms Pointer, Avalonia Pointer): absolute flag bitmask. `0x0001` = barrel button, `0x0002` = eraser.

`PenButtonTracker` hides this difference. Create one per session, feed every point through it, then read state:

```csharp
var buttons = new PenButtonTracker();
foreach (var pt in session.DrainPoints())
{
    buttons.Update(pt);
}

if (buttons.IsTipDown) { ... }
if (buttons.IsBarrelDown(1)) { ... }
if (buttons.IsEraser) { ... }
```

Call `buttons.Reset()` when restarting a session.

**Hard ceiling**: pointer-style backends only expose a single barrel flag — `IsBarrelDown(2)` and `IsBarrelDown(3)` always return `false` on those backends. Per-button identity for B2/B3 requires Wintab.

### Raw access (advanced)

If you need the raw event encoding, `PenPoint` exposes `pt.ButtonAction`, `pt.ButtonNumber`, `pt.IsTipPressed`, `pt.IsButtonPressed(int)`, `pt.IsButtonReleased(int)`. These match the Wintab encoding only — for non-Wintab backends you must read `pt.Buttons` as a bitmask.

### Eraser detection

Eraser is detected via `pt.IsEraser` (which checks `pt.Cursor == 14`). In Wintab, cursor type changes on hover before contact. WM_POINTER uses `PEN_FLAG_INVERTED`, mapped to cursor 14 for consistency. `PenButtonTracker.IsEraser` mirrors this from the latest point.

## Error Handling

`Start()` returns `null` on success, or an error string on failure. Always check:

```csharp
var error = session.Start();
if (error != null)
{
    // "Wintab not found. Is the tablet driver installed?"
    // "WM_POINTER requires an application window handle."
    // "Failed to open system context."
    ShowError(error);
    return;
}
```

Always call `Dispose()` when done — this stops the session and closes the diagnostic log file.

## Diagnostics

WinPenKit logs to `%TEMP%\WinPenKit.log`:
- Context configuration before/after open
- Hi-res fallback events
- Button/cursor transitions
- Packet processing errors

## Native C++ / Rust Gotchas

These apply when using `WinPenKit.Native.dll` (the C ABI) or implementing your own Wintab integration:

### 1. Hidden window must not be HWND_MESSAGE

The Wacom driver doesn't deliver `WT_PACKET` to message-only windows. Use a regular hidden top-level window.

### 2. Set lcPktData to match your PACKET struct

The default context may not include all fields. Set `lcPktData = PK_PKTBITS_ALL` (0x1FFF) explicitly.

### 3. DPI awareness is required

Without Per-Monitor V2, `ScreenToClient` returns virtualized coordinates and pen position drifts.

### 4. NOMINMAX required

`<windows.h>` defines `min`/`max` macros that break `std::min`/`std::max`.

### 5. Double-buffer WM_PAINT

60fps toolbar repaints cause flicker without offscreen buffering and `WM_ERASEBKGND` suppression.

### 6. Child controls need DPI-scaled fonts

Win32 controls inherit the tiny system bitmap font. Apply `WM_SETFONT` with a DPI-scaled font.

### 7. Don't reposition controls in WM_PAINT

Causes feedback loops. Use a `layout_controls()` function called from `WM_CREATE`/`WM_SIZE`/`WM_DPICHANGED`.

### 8. RAII for log files

Static `FILE*` in DLLs isn't reliably destroyed on unload. Use an RAII struct with a destructor.

### 9. Always check Start() return value

Ignoring errors leaves the app in a broken state with no user feedback.

### 10. HCTX is pointer-sized (8 bytes on x64)

Using `uint` (4 bytes) for `pkContext` in the PACKET struct silently shifts all subsequent fields. Use `IntPtr` in C#.

### 11. WM_POINTER coalescing: use history only when count > 1

`GetPointerPenInfoHistory` with `count == 1` returns different data than `GetPointerPenInfo`, causing silent data loss. Always fall through to single-point `GetPointerPenInfo` unless `count > 1`.

### 12. WinForms: NativeWindow.AssignHandle crashes on Form HWNDs

Use `IMessageFilter` instead — it intercepts messages at the app message pump level without touching HWND ownership. This is how `WinPenKit.WinForms` works.
