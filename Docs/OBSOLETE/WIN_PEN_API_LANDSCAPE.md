# Windows Pen Input API Landscape

A comparison of the available APIs for receiving pen/stylus input on Windows, relevant to drawing and handwriting applications.

## API Overview

| API | Introduced | Coordinates | Precision | Pen Data | Multi-Monitor | Cursor Follows Pen | Threading |
|---|---|---|---|---|---|---|---|
| **Wintab (System)** | 1991 (Wacom) | Physical screen pixels | Screen resolution (~200 DPI) | Full: pressure, tilt (azimuth/altitude), twist, buttons, Z (height) | Works (driver-dependent; often requires custom mapping) | Yes | Background thread (200+ Hz) |
| **Wintab (Digitizer Hi-Res)** | 1991 (Wacom) | Tablet native units | Tablet resolution (up to 5080+ LPI) | Full: pressure, tilt, twist, buttons, Z | Works (driver-dependent; often requires custom mapping) | Yes | Background thread (200+ Hz) |
| **WM_POINTER / WM_POINTERUPDATE** | Windows 8 | Physical screen pixels | Screen resolution¹ | Pressure, tilt (X/Y), twist, buttons via POINTER_PEN_INFO | Works | Yes | UI thread (message-driven) |
| **Windows Ink (WinUI PointerPoint)** | Windows 10 / WinUI | DIPs (device-independent pixels) | DIP resolution | Pressure, tilt (X/Y) via PointerPointProperties | Works | Yes | UI thread |
| **Windows Ink (WPF StylusPoint)** | .NET 3.0 / WPF | WPF device-independent units | WPF layout resolution | Pressure, tilt (X/Y) via StylusPointDescription | Works | Yes | UI thread |
| **RealTimeStylus (COM)** | Windows XP Tablet PC Edition | HIMETRIC (0.01mm) | Up to ~2540 DPI² | Pressure, tilt (X/Y), twist, buttons | Works | No — raw RTS does not move cursor; frameworks may layer cursor sync on top | Configurable (sync or async plugin) |

## Detailed Comparison

### Coordinate Precision

The X ranges below are examples based on these assumptions:
- **Screen pixels (3,840):** a single 4K UHD monitor (3840×2160 physical pixels)
- **DIPs (1,707):** the same 4K monitor at 225% display scaling (3840 / 2.25 = 1,707 DIPs)
- **Tablet native (52,600):** a Wacom Intuos Pro Large PTK-870 (349mm active width at 5080 LPI = ~52,600 native units). Other tablets will differ — smaller tablets or lower-resolution models produce smaller ranges
- **HIMETRIC (264,000):** HIMETRIC units are 0.01mm, so a 264mm-wide tablet = ~264,000 units

| API | Typical X range | Tablet resolution preserved? |
|---|---|---|
| Wintab System | 0–3,840 (screen pixels on a 4K monitor) | No — downscaled to screen pixels |
| Wintab Digitizer Hi-Res | 0–52,600 (tablet native, varies by device) | **Yes** — full tablet LPI |
| WM_POINTER | 0–3,840 (screen pixels on a 4K monitor) | No — screen pixels¹ |
| WinUI PointerPoint | 0–1,707 (DIPs on a 4K monitor at 225% scaling) | No — DIP resolution |
| WPF StylusPoint | Framework-dependent | No — layout resolution |
| RealTimeStylus | 0–~264,000 (HIMETRIC, varies by tablet width) | Partial — HIMETRIC unit is 0.01mm (~2540 units/inch), but actual resolution depends on hardware and driver² |

### Pressure Data

Note: "Normal" in Wintab's `pkNormalPressure` does **not** mean "normalized." It means **normal force** — the pressure applied perpendicular (normal) to the tablet surface, as opposed to `pkTangentPressure` which measures tangential (sideways) force from an airbrush finger wheel. The value is a raw integer that must be divided by `GetMaxPressure()` to normalize.

| API | Pressure type | Range | Normalization |
|---|---|---|---|
| Wintab | `pkNormalPressure` (uint) | 0 to device-specific max (query via `GetMaxPressure()`) | App divides by max |
| WM_POINTER | `POINTER_PEN_INFO.pressure` (uint32) | 0–1024 | Fixed range |
| WinUI PointerPoint | `Properties.Pressure` (float) | 0.0–1.0 | Pre-normalized |
| WPF StylusPoint | `PressureFactor` (float) | 0.0–1.0 | Pre-normalized |
| RealTimeStylus | `PACKET_PROPERTY.pkNormalPressure` | Device-specific | App normalizes |

### Tilt / Orientation Data

**Twist** (also called **barrel rotation**) is the rotation of the pen around its own long axis — like turning a screwdriver. Twist support is rare — it requires both a specific tablet model and a specific pen that contains a rotation sensor (e.g., Wacom's Art Pen or Airbrush stylus). Standard pen styluses do not report twist, and most consumer tablets do not support it even with the right pen. The APIs list twist support, but in practice very few hardware setups actually produce twist data.

| API | Tilt representation | Twist | Notes |
|---|---|---|---|
| Wintab | Azimuth (compass direction, 0–3600 tenths of degree) + Altitude (angle from surface, 0–900) | orTwist (barrel rotation, 0–3600) | Full spherical coordinates; most detailed |
| WM_POINTER | TiltX / TiltY (degrees, -90 to +90) | Rotation (degrees, 0–359) | Cartesian tilt; simpler but less expressive |
| WinUI PointerPoint | `Properties.XTilt` / `YTilt` (float, degrees) | `Properties.Twist` (float, degrees) | Same as WM_POINTER |
| WPF StylusPoint | `XTiltOrientation` / `YTiltOrientation` | Not directly available | Limited |
| RealTimeStylus | Azimuth + Altitude + Twist | Full | Similar to Wintab |

**PenPoint normalization:** The unified `PenPoint` struct carries **both** representations (Azimuth/Altitude and TiltX/TiltY), all normalized to **tenths of a degree**. Each backend computes whichever it doesn't have natively. WM_POINTER's integer degrees are multiplied by 10; Wintab's spherical values are converted via trigonometry. Consumers can use either representation without knowing which API produced the data.

### Additional Pen Data

| API | Z (height) | Barrel pressure | Eraser detection | Multiple pen buttons |
|---|---|---|---|---|
| Wintab | `pkZ` | `pkTangentPressure` | Via cursor type (`pkCursor`) | `pkButtons` bitmask |
| WM_POINTER | Not available | Not available | `IS_POINTER_INRANGE_WPARAM` + flags | Button flags in `POINTER_INFO` |
| WinUI PointerPoint | Not directly | Not available | `Properties.IsEraser` | `Properties.IsBarrelButtonPressed` etc. |
| WPF StylusPoint | Not directly | Not available | `StylusDevice.Inverted` | Via StylusButton collection |
| RealTimeStylus | Via PACKET_PROPERTY | Via PACKET_PROPERTY | Via cursor type | Via PACKET_PROPERTY |

## Framework Compatibility

| API | WinForms | WPF | WinUI 3 | Avalonia | Raw Win32 |
|---|---|---|---|---|---|
| Wintab | Yes (via P/Invoke or .NET wrapper) | Yes (via P/Invoke or .NET wrapper) | Yes (via P/Invoke or .NET wrapper) | Yes (via P/Invoke or .NET wrapper) | Yes (native) |
| WM_POINTER | Via IMessageFilter only (NativeWindow.AssignHandle crashes on Form HWNDs; subclassing conflicts with WinForms' internal NativeWindow) | Yes (via WndProc / HwndSource) | Not directly (WinUI wraps it) | Possible (via platform interop) | Yes (native) |
| WinUI PointerPoint | No | No | Yes (native) | No | No |
| WPF StylusPoint | No | Yes (native) | No | No | No |
| RealTimeStylus | Yes (COM interop) | Yes (COM interop) | Possible (COM) | Possible (COM) | Yes (COM) |

## When to Use What

| Scenario | Recommended API | Why |
|---|---|---|
| Maximum tablet precision for drawing | Wintab Digitizer Hi-Res | Only API that preserves full tablet LPI |
| Simple pen input, modern app | WinUI PointerPoint or WPF StylusPoint | Built into the framework, no dependencies |
| Cross-framework pen input library | Wintab (via .NET wrapper) | Works in all .NET UI frameworks via P/Invoke |
| Touch + pen discrimination | WM_POINTER | Designed for multi-input-type scenarios |
| Airbrush / barrel pressure | Wintab | Only API exposing tangential pressure |
| Pen height above tablet | Wintab | Only API exposing Z axis |
| Full tilt as azimuth/altitude | Wintab or RealTimeStylus | Other APIs only provide X/Y tilt |

## Known Quirks and Gotchas

### Wintab
- **CXO_SYSTEM required** for Wacom driver packet delivery — without it, context opens but no packets arrive
- **Multi-monitor:** custom `OutExt` is clipped by the driver's tablet-to-monitor mapping; use system context output range or tablet-native with ScaleAxis conversion
- **Y-axis:** tablet origin is bottom-left; must negate `OutExtY` or `SysExtY` for screen convention
- **DPI in WinUI 3:** `ClientToScreen` must be called from a Per-Monitor V2 thread context; default DPI-unaware context returns virtualized values that drift
- **Two contexts:** `WTI_DEFCONTEXT` (digitizer) may not deliver packets; `WTI_DEFSYSCTX` (system) is more reliable as a base

### WM_POINTER
- **DPI awareness:** coordinates are in the process's DPI awareness context — may need conversion for Per-Monitor V2
- **Pointer ID tracking:** each contact point has a unique ID; pen and touch can coexist
- **WinUI 3 wraps this:** `PointerPoint` events in WinUI 3 are built on WM_POINTER; using both simultaneously can cause conflicts

### Windows Ink (WinUI / WPF)
- **Pre-normalized pressure:** convenient but loses the raw range (some tablets report 8192 levels, others 2048 — both become 0.0–1.0)
- **Tilt as X/Y:** less precise than azimuth/altitude for certain brush algorithms (calligraphy, airbrush)
- **InkCanvas:** built-in ink rendering is easy but limited for custom brush engines

### RealTimeStylus
- **COM-based:** requires interop boilerplate in .NET
- **Deprecated in spirit:** Microsoft hasn't invested in it since Windows 7; WM_POINTER is the successor
- **Plugin model:** sync plugins run on the pen thread (low latency), async plugins run on the UI thread

## Per-Monitor V2 DPI Awareness

The term "Per-Monitor V2" is central to understanding coordinate handling in pen input applications on Windows. Here's what it means and why it matters.

### The DPI awareness spectrum

To learn what "Per-Monitor V2" means and how it fits into the full DPI awareness model, read Microsoft's [High DPI Desktop Application Development on Windows](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows). The summary below covers what's relevant to pen input.

Windows applications declare how they handle high-DPI displays. The level affects what coordinates Win32 APIs return:

| DPI Awareness Level | Introduced | What happens |
|---|---|---|
| **DPI Unaware** | (default before Vista) | Windows lies to the app — all APIs return values as if the display is 96 DPI. Windows bitmap-scales the app's output to the actual DPI, causing blurriness. |
| **System DPI Aware** | Windows Vista | The app knows the primary monitor's DPI at startup. Renders crisply on the primary monitor but is bitmap-scaled on other monitors with different DPIs. |
| **Per-Monitor V1** | Windows 8.1 | The app receives `WM_DPICHANGED` when moved between monitors and can re-render. But many Win32 APIs (dialog sizing, non-client area) still use the wrong DPI. |
| **Per-Monitor V2** | Windows 10 1703 | The full solution. All Win32 APIs respect the current monitor's DPI. Non-client area (title bar, scrollbars) scales automatically. Child window DPI works correctly. |

### What Per-Monitor V2 means for pen input

When your process is Per-Monitor V2 aware:

- **`ClientToScreen` / `ScreenToClient`** return **physical screen pixels** — the real hardware pixel positions
- **`GetDpiForMonitor`** returns the **real DPI** (e.g., 216 for 225% scaling)
- **Wintab `pkX/pkY`** are also in **physical screen pixels** — Wintab always bypasses DPI virtualization

This means Wintab coordinates and Win32 coordinates are in the same space — you can subtract them directly. If the app were DPI Unaware, `ClientToScreen` would return virtualized (scaled-down) values while Wintab still returns physical pixels, causing a coordinate mismatch that grows with distance from the screen origin.

### How each framework sets DPI awareness

| Framework | Default DPI Awareness | How it's set |
|---|---|---|
| **.NET 10 WinForms** | Per-Monitor V2 | Automatic via `<HighDpiMode>PerMonitorV2</HighDpiMode>` in the project |
| **WinUI 3** | Per-Monitor V2 | Set by the Windows App SDK at startup |
| **WPF (.NET 10)** | Per-Monitor V2 | Automatic in .NET 10; earlier versions need app manifest |
| **Raw Win32** | DPI Unaware | Must declare in app manifest or call `SetProcessDpiAwarenessContext` |

### `SetThreadDpiAwarenessContext` — controlling the coordinate space

Even in a Per-Monitor V2 process, individual threads can explicitly set their DPI awareness context. This controls which coordinate space Win32 APIs operate in. WinUI 3 apps can use this to ensure `ClientToScreen` and `GetDpiForMonitor` return physical pixel values:

```csharp
var old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
try
{
    ClientToScreen(hwnd, ref clientOrigin);  // guaranteed physical pixels
    GetDpiForMonitor(hMon, 0, out dpiX, out dpiY);  // guaranteed real DPI
}
finally
{
    SetThreadDpiAwarenessContext(old);  // restore previous context
}
```

The context handle values:

| Handle | Value | Meaning |
|---|---|---|
| `DPI_AWARENESS_CONTEXT_UNAWARE` | -1 | DPI unaware — APIs return virtualized values |
| `DPI_AWARENESS_CONTEXT_SYSTEM_AWARE` | -2 | System DPI aware |
| `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE` | -3 | Per-Monitor V1 |
| `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` | -4 | Per-Monitor V2 — recommended for pen input apps |
| `DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED` | -5 | DPI unaware with improved GDI scaling |

### Why WinForms doesn't need this

In WinForms (.NET 10), the process is already Per-Monitor V2 from startup. `Control.PointToClient()` internally calls `ScreenToClient` which returns physical-pixel-based logical coordinates automatically. No thread context switching is needed — `PointToClient` handles everything in one call.

In WinUI 3, the situation is more complex because the XAML layout engine uses DIPs (not physical pixels), and the thread's DPI context may vary. The explicit `SetThreadDpiAwarenessContext` call ensures consistency between `ClientToScreen` (physical pixels) and Wintab `pkX/pkY` (also physical pixels).

## Footnotes

**¹ WM_POINTER precision:** The Windows pointer input stack internally tracks higher-resolution input from the tablet hardware, but coordinates exposed via Win32 APIs (`ptPixelLocation` in `POINTER_INFO`) are quantized to physical screen pixels. Higher-frequency sampling is available via `GetPointerInfoHistory`, but the coordinate space remains screen pixels — you do not get tablet-native resolution as with Wintab Digitizer Hi-Res.

**² RealTimeStylus resolution:** The "2540 DPI" figure comes from the HIMETRIC unit definition (1 HIMETRIC = 0.01mm, and 25.4mm/inch = 2540 units/inch). This is a unit conversion, not a hardware limit. Actual coordinate resolution depends on the tablet hardware, driver, and packet scaling. High-end tablets with 5080+ LPI may deliver more precision than the HIMETRIC grid can represent; lower-end devices may deliver less. RTS gives higher resolution than screen pixels but is not guaranteed to match modern tablet LPI.

## Wintab vs Windows Ink Driver Conflict

Many tablet drivers (including Wacom's) treat Wintab and Windows Ink as mutually exclusive input paths. When one is active, the other may be suppressed or produce degraded data.

**In practice:**
- When Wintab is active (a context is open), the driver may stop delivering WM_POINTER pen events
- When Windows Ink is the active path, the driver may not load `wintab32.dll` or may deliver incomplete Wintab data
- Some driver versions have a "Use Windows Ink" checkbox in the driver settings that controls which path is primary

**This is the reason apps like Krita require a restart when switching input APIs** — the driver-level plumbing is set up at process startup, and switching cleanly at runtime is not reliably supported by all drivers.

**Implications for a unified session design:**
- The session factory must probe for actual driver support, not just OS capability
- Offering both Wintab and WM_POINTER in a dropdown is only meaningful if the driver supports both
- Starting a Wintab session may disable WM_POINTER for the rest of the process lifetime (driver-dependent)
- See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the full abstraction design

## Hover / Proximity Behavior

APIs differ in how they report the pen being near the tablet but not touching the surface. This matters for cursor preview, brush ghosting, and eraser detection.

| API | Proximity detection | Hover data | Notes |
|---|---|---|---|
| Wintab | `WT_PROXIMITY` message; `pkStatus` proximity bit | Full pen data during hover (position, tilt, buttons, cursor type) | Eraser detection happens on hover (cursor type changes before contact). Proximity bit in `pkStatus` is set when pen *leaves*, not when it enters. Packets stop when pen leaves range. |
| WM_POINTER | `IS_POINTER_INRANGE_WPARAM` flag | `WM_POINTERUPDATE` with `INRANGE` but not `INCONTACT` | Pen data available during hover. `WM_POINTERLEAVE` when pen exits range. |
| WinUI PointerPoint | `PointerEntered` / `PointerExited` events | `IsInContact` property distinguishes hover from press | Natural XAML event model. Hover data includes pressure=0, tilt, position. |
| WPF StylusPoint | `StylusInAirMove` event | Position and tilt during in-air movement | `StylusDevice.InAir` property. Less data than contact events. |
| RealTimeStylus | `IStylusPlugin::InAirPackets` | Position and tilt during hover | Separate callback from `Packets` (contact). |

**Key differences:**
- Wintab provides full pen data (including cursor type for eraser detection) during hover, but proximity tracking is quirky — the `pkStatus` proximity bit signals *leaving*, and packets simply stop when the pen is out of range. Time-based timeout is more reliable than flag-based detection.
- WM_POINTER has cleaner hover semantics (`INRANGE` + `INCONTACT` flags) but less pen data (no Z height, no barrel pressure).
- Eraser detection timing differs: Wintab reports cursor type change on hover (before contact), while WM_POINTER reports eraser state in pen flags per-event.

## Latency Implications

The threading model affects pen-to-pixel latency:

| API | Delivery thread | Typical frequency | Latency characteristics |
|---|---|---|---|
| Wintab | Background thread | 200+ Hz | Lowest latency to capture. App polls on render timer (adds up to one frame). Total: ~4-20ms. |
| WM_POINTER | UI thread (message pump) | ~120-240 Hz | Tied to message pump processing. If UI thread is busy (rendering, layout), messages queue. Total: ~4-30ms. |
| WinUI PointerPoint | UI thread (XAML events) | Same as WM_POINTER | Additional XAML event dispatch overhead on top of WM_POINTER. |
| RealTimeStylus | Configurable | Up to 240+ Hz | Sync plugins run on pen thread (lowest possible latency). Async plugins run on UI thread. |

**For paint apps:** The latency difference between Wintab's background thread and WM_POINTER's UI thread is rarely perceptible (both are well under one frame at 60fps). The bigger practical concern is that WM_POINTER events can be delayed if the UI thread is doing heavy work (complex layout, large canvas redraw), while Wintab's background thread captures independently. This is why production paint apps (Photoshop, Krita, Clip Studio) historically prefer Wintab — not for raw latency but for capture reliability under load.

## WM_POINTER Event Coalescing

When the UI thread is busy (rendering, layout, GPU texture upload), Windows coalesces multiple `WM_POINTERUPDATE` messages into a single message. The most recent position is delivered normally, but intermediate positions are lost unless the app explicitly requests them via `GetPointerPenInfoHistory`.

**Symptoms:** Strokes appear as straight-line segments between widely spaced points — the classic "polygon" appearance instead of smooth curves. This is especially visible in apps with heavier render loops (e.g., egui/wgpu in Scribble.Rust) and less visible in apps with lightweight message pumps (e.g., GDI in Scribble.Win32).

**The fix:** Use `GetPointerPenInfoHistory` to recover coalesced events, but **only when `count > 1`** (actual coalescing occurred). For single events, use the normal `GetPointerPenInfo` path. The history API returns subtly different data for `count == 1` on some driver/OS combinations, causing silent data loss.

```cpp
// Only use history for WM_POINTERUPDATE with actual coalescing.
if (msg == WM_POINTERUPDATE) {
    POINTER_PEN_INFO history[64];
    UINT32 count = 64;
    if (GetPointerPenInfoHistory(pointerId, &count, history) && count > 1) {
        for (int i = count - 1; i >= 0; i--)  // oldest first
            process_point(history[i]);
        return;
    }
}

// Single point (most common case, DOWN/UP, or non-coalesced UPDATE).
POINTER_PEN_INFO pen_info = {};
GetPointerPenInfo(pointerId, &pen_info);
process_point(pen_info);
```

**Critical: `count > 1`, not `count > 0`.** This was discovered through debugging — using `count > 0` caused Scribble.Win32 to silently lose all WM_POINTER data. The `count == 1` case from history differs from `GetPointerPenInfo` in ways that break the data path. Always fall through to `GetPointerPenInfo` for single events.

**PenSession's WmPointerSession handles this automatically.** The C++ implementation uses `GetPointerPenInfoHistory` for coalesced events with a fallback to single-point `GetPointerPenInfo`.

**Why managed framework sessions aren't affected:** WinUI 3, WPF, and Avalonia decoalesce pointer events internally before delivering them to app event handlers. Their native input stacks (XAML composition layer, Wisp/RTS, Avalonia's pointer system) handle history recovery transparently. Only raw Win32 `WM_POINTER` subclassing requires explicit history retrieval.

## Unifying These APIs (Implemented)

The `PenSession` library provides a unified `IPenSession` interface across four backends, with runtime switching via a dropdown — no restart required. See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the full design.

### Framework specificity

Not all APIs work in all app types. The key factor is how input reaches the session:

| API | Input mechanism | Framework compatibility |
|---|---|---|
| **Wintab (System & Digitizer)** | Own hidden window on background thread | **Any app** — Win32, WinUI 3, WPF, WinForms |
| **WM_POINTER** | Subclasses the app's HWND | **Raw Win32 only** — WinUI 3, WPF, and WinForms intercept pen input before it reaches the HWND or conflict with HWND ownership |
| **WinUI PointerPoint** | XAML `PointerMoved` events | **WinUI 3 only** — requires XAML UIElement |
| **WPF Stylus** | WPF `StylusMove`/`StylusDown` events | **WPF only** — uses WPF's Wisp/RTS input stack |
| **Avalonia Pointer** | Avalonia `PointerMoved`/`PointerPressed` events | **Avalonia only** — uses Avalonia's pointer system |
| **WinForms Pointer** | `IMessageFilter` intercepts `WM_POINTER` messages | **WinForms only** — `NativeWindow.AssignHandle` on Form HWNDs crashes; `IMessageFilter` intercepts at the message pump level instead |

This is why the library is split into packages:
- `PenSession.dll` — framework-agnostic (Wintab + WmPointer)
- `PenSession.WinUI.dll` — WinUI 3 extension (WinUI PointerPoint)
- `PenSession.Wpf.dll` — WPF extension (WPF Stylus)
- `PenSession.Avalonia.dll` — Avalonia extension (Avalonia Pointer)
- `PenSession.WinForms.dll` — WinForms extension (WinForms Pointer via IMessageFilter)

### What's covered in the design doc

- The `IPenSession` interface and `PenPoint` normalization contract
- Polling vs event-driven delivery models (polling everywhere — validated)
- Bidirectional tilt conversion (Azimuth/Altitude ↔ TiltX/TiltY, all in tenths of degree)
- Session factory with API discovery
- Framework-specific vs framework-agnostic session types
- Complete WinUI 3 integration guide
- Comparison with Qt's `QTabletEvent` and Krita's API switching

## References

### Wintab
- [Wacom Wintab Basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/) — context types, coordinate mapping, packet fields
- [Wacom Wintab Reference](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-reference/) — full API reference (WTOpen, WTInfo, WTGet, context fields, packet structures)
- [Wintab Specification 1.4](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-spec/) — the original specification document defining context mapping, axis scaling, and coordinate spaces

### WM_POINTER
- [Pointer Input Messages and Notifications](https://learn.microsoft.com/en-us/windows/win32/inputmsg/messages-and-notifications-portal) — WM_POINTER message family
- [POINTER_PEN_INFO structure](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_pen_info) — pen-specific fields (pressure, tilt, rotation)
- [Pointer Input API overview](https://learn.microsoft.com/en-us/windows/win32/inputmsg/about-the-pointer-input-stack) — architecture, pointer types, DPI behavior

### Windows Ink / WinUI 3
- [PointerPoint class](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpoint) — WinUI 3 pointer input
- [PointerPointProperties](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.input.pointerpointproperties) — pressure, tilt, eraser, buttons
- [InkCanvas (WinUI 3)](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.inkcanvas) — built-in ink rendering control

### Windows Ink / WPF
- [StylusPoint class](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.styluspoint) — WPF stylus data
- [InkCanvas (WPF)](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.inkcanvas) — WPF ink rendering control
- [Stylus Input overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/the-ink-object-model-windows-forms-and-com-versus-wpf) — WPF vs WinForms ink models

### RealTimeStylus
- [RealTimeStylus overview](https://learn.microsoft.com/en-us/windows/win32/tablet/realtimestylus-reference) — COM API for low-latency pen input
- [StylusInput API](https://learn.microsoft.com/en-us/windows/win32/tablet/stylusinput-api-reference) — plugin model, sync vs async

### Windows DPI and Multi-Monitor
- [GetSystemMetrics (virtual screen)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics) — SM_XVIRTUALSCREEN, SM_CXVIRTUALSCREEN for multi-monitor coordinate spaces
- [SetThreadDpiAwarenessContext](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext) — per-thread DPI awareness switching
- [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor) — real monitor DPI query
- [High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) — comprehensive DPI awareness guide
