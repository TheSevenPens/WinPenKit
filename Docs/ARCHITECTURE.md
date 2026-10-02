# Architecture

How WinPenKit works, from the driver to the `PenPoint` an application drains. This is the
reference for the mechanism. [HOW_TO_USE.md](HOW_TO_USE.md) is the usage guide,
[STYLUS.md](STYLUS.md) explains the two Windows input paths, and
[TIMESTAMPS.md](TIMESTAMPS.md) holds the clock measurements. Source files are cited by path
relative to the repository root.

## Overview

WinPenKit is a layered pen input SDK. The core abstraction (`IPenSession`) sits between
platform-specific input APIs and consumer applications. Every backend does the same six things
in the same order, and this document is organised by them:

```
Source     which API, discovered how, with which capabilities
   |
Delivery   which thread receives the data, through which window or event,
   |       filtered by which capture region
   |
Timing     how points are queued, recovered from coalescing, and timestamped
   |
Position   which source field becomes DesktopX/Y, and how it is mapped to desktop pixels
   |
Values     pressure, tilt, twist, buttons, cursor, and the conventions they follow
   |
Output     PenPoint, drained through IPenSession (or the C ABI)
```

```
┌─────────────────────────────────────────────────────────────┐
│  Applications (Scribble.WinUI, Scribble.Wpf, etc.)          │
│  - Framework-specific UI, rendering, coordinate conversion  │
│  - Polls IPenSession.DrainPoints() on a render timer        │
└──────────────────────────┬──────────────────────────────────┘
                           │  PenPoint stream (desktop pixels)
┌──────────────────────────┴──────────────────────────────────┐
│  IPenSession interface                                      │
│  - Start(hwnd) / Stop() / Dispose() / OnActivated()         │
│  - DrainPoints() / DrainPoints(Span) / HasNewData           │
│  - MaxPressure, Api, Capabilities, Conventions,             │
│    CaptureRegion, RefreshMapping()                          │
└──────────────────────────┬──────────────────────────────────┘
                           │
         ┌─────────────────┼─────────────────┐
         │                 │                 │
    ┌────┴────┐     ┌──────┴──────┐   ┌──────┴──────┐
    │ Wintab  │     │ WM_POINTER  │   │ Framework-  │
    │ Backend │     │ Backend     │   │ Specific    │
    │         │     │             │   │ Backends    │
    └─────────┘     └─────────────┘   └─────────────┘
```

## Backends at a glance

| Backend (`InputApi`) | Class | Package | Thread that produces points | Position source | Default capture region | Clock (`Conventions.Timestamp`) | `MaxPressure` |
|---|---|---|---|---|---|---|---|
| `WintabSystem` | `WintabSystemSession` | `WinPenKit` | background pump thread | `pkX/pkY`, driver-mapped pixels | the window passed to `Start`; unbounded with no window | `DeviceTicks` (`pkTime`) | `DVC_NPRESSURE.axMax` |
| `WintabDigitizer` | `WintabDigitizerSession` | `WinPenKit` | background pump thread | `pkX/pkY` in tablet units, scaled onto `lcSys` | the window passed to `Start`; unbounded with no window | `DeviceTicks` (`pkTime`) | `DVC_NPRESSURE.axMax` |
| `WmPointer` | `WmPointerSession` | `WinPenKit` | UI thread (window subclass) | `ptHimetricLocationRaw` through `GetPointerDeviceRects`, pixel fallback | the window passed to `Start` (required) | `PerformanceCounter` | 1024 |
| `WinFormsPointer` | `WinFormsPointerSession` | `WinPenKit.WinForms` | UI thread (`IMessageFilter`) | as `WmPointer` | the window passed to `Start`, else the control's handle | `PerformanceCounter` | 1024 |
| `WpfStylus` | `WpfStylusSession` | `WinPenKit.Wpf` | UI thread (stylus events) | `GetStylusPoints` DIPs, scaled by `WpfCoordinates` | none: the element's events only | `SystemTicks` (per event) | 1024 |
| `WinUiPointer` | `WinUiPointerSession` | `WinPenKit.WinUI` | UI thread (XAML events) | `PointerPoint.Position` DIPs, scaled by monitor DPI | none: the element's events only | `SystemTicks` (per point, not anchored) | 1024 |
| `AvaloniaPointer` | `AvaloniaPointerSession` | `WinPenKit.Avalonia` | UI thread (Avalonia events) | `PointerPoint.Position` DIPs, scaled by `RenderScaling` | none: the control's events only | `SystemTicks` (per event) | 1024 |

The native DLL implements the first three. See [Managed and native](#managed-and-native) for
where it differs.

## Source

### InputApi

`InputApi` (`WinPenKit/InputApi.cs`) names every backend and is also carried on each point as
`PenPoint.Source`. `InputApi.Label()` gives the display name ("Wintab", "Wintab (high-res)",
"WM_Pointer", ...), and `InputApi.IsFrameworkAgnostic()` is true for the three APIs the
factory can create. The C ABI has the same values in `PenInputApi`
(`WinPenKit.Native/include/pen_session.h`) and the same labels in `pen_session_get_api_label`.

### Discovery

`PenSessionFactory.GetAvailableApis()` (`WinPenKit/PenSessionFactory.cs`) calls into each
driver rather than reading an OS version:

- **Wintab**: `WTInfoA(0, 0, NULL)` returns non-zero (`WinPenKit/Wintab/WintabNative.cs`). Both
  `WintabSystem` and `WintabDigitizer` are then reported.
- **WM_POINTER**: the `GetPointerType` entry point exists in `user32.dll`
  (`WinPenKit/Pointer/PointerNative.cs`).

The native DLL checks that `Wintab32.dll` loads and its functions resolve
(`WinPenKit.Native/src/wintab_loader.h`), and that `user32.dll` exports `GetPointerPenInfo`
(`WinPenKit.Native/src/wm_pointer_session_impl.cpp`).

A framework application asks its framework package instead: `WpfPenApis`, `WinFormsPenApis`,
`AvaloniaPenApis` and `WinUiPenApis` each take the factory's list, remove `WmPointer`, and add
the framework's own API. `WmPointer` is removed because it subclasses the window procedure, and
each of these frameworks consumes pointer messages before a subclass sees them.

`PenSessionFactory.Create(api)` creates the three framework-agnostic sessions.
`CreateDefault()` prefers `WintabDigitizer`, then `WintabSystem`, then `WmPointer`. Framework
sessions need a UI element and are constructed directly.

### Capabilities

`PenCapabilities` (`WinPenKit/PenCapabilities.cs`) says what a session supports. It is computed
from live state, not fixed at creation:

| Backend | Pressure | Tilt | Twist | ZHeight | Buttons | Eraser | HiRes | GlobalCapture | Proximity |
|---|---|---|---|---|---|---|---|---|---|
| Wintab system | yes | yes | yes | yes | yes | yes | no | yes | yes |
| Wintab digitizer | yes | yes | yes | yes | yes | yes | while the hi-res context is open | yes | yes |
| WM_POINTER, WinForms | yes | yes | yes ¹ | no | yes | yes | while device rects are available | no | no |
| WPF, WinUI, Avalonia | yes | yes | yes ¹ | no | yes | yes | no | no | no |

¹ `Twist` means the backend reads twist from its API. A pen without a rotation sensor reports 0
on every backend, including Wintab. See [Values](#values).

The native C ABI computes `HiRes` the same way in `pen_session_get_capabilities`
(`WinPenKit.Native/src/pen_session_exports.cpp`).

## Delivery

### Threads and windows

**Wintab** (`WinPenKit/Wintab/WintabMessagePump.cs`, `WinPenKit/Wintab/WintabSessionBase.cs`).
`Start` creates a `WintabMessagePump`: a background thread named `WinPenKit.WintabMessagePump`
that registers a window class and creates a hidden top-level window (`WS_OVERLAPPED`, never
shown). It is not a message-only (`HWND_MESSAGE`) window, because the Wacom driver does not
deliver `WT_PACKET` to message-only windows. The context is opened on that window with
`WTOpenA`, `CXO_SYSTEM | CXO_MESSAGES`, `lcPktData = PK_ALL`, `lcPktMode = PK_BUTTONS`
(buttons relative, everything else absolute) and `lcMoveMask = PK_ALL`. Each `WT_PACKET` is
read with `WTPacket` on the pump thread, converted, filtered by the capture region, and queued.
The application's own window is used only to set the default capture region.

Wintab delivers `WT_PACKET` to the foreground application. A process with no visible window
of its own, such as a console host, receives no packets.

**WM_POINTER** (`WinPenKit/Pointer/WmPointerSession.cs`). `Start(hwnd)` requires a window and
calls `SetWindowSubclass` on it. The subclass procedure runs on the UI thread, handles
`WM_POINTERUPDATE`, `WM_POINTERDOWN` and `WM_POINTERUP`, ignores anything `GetPointerType`
does not report as `PT_PEN`, and always passes the message on with `DefSubclassProc`. The
subclass is removed in `Stop` and on `WM_NCDESTROY`.

**WinForms** (`WinPenKit.WinForms/WinFormsPointerSession.cs`). `Application.AddMessageFilter`
installs the session as an `IMessageFilter`, which sees every message the application's
message loop dispatches. `PreFilterMessage` handles the same three pointer messages and always
returns `false`, so WinForms processes them too. `NativeWindow.AssignHandle` is not used: it
crashes on a Form's window.

**WPF** (`WinPenKit.Wpf/WpfStylusSession.cs`). Handlers on the element's `StylusMove`,
`StylusDown`, `StylusUp` and `StylusInAirMove`. Events whose tablet device is not
`TabletDeviceType.Stylus` are ignored.

**WinUI 3** (`WinPenKit.WinUI/WinUiPointerSession.cs`). Handlers on the element's
`PointerMoved`, `PointerPressed` and `PointerReleased`. Points whose `PointerDeviceType` is not
`Pen` are ignored. The `hwnd` passed to the constructor is used for the pixel conversion.

**Avalonia** (`WinPenKit.Avalonia/AvaloniaPointerSession.cs`). Handlers for `PointerMoved`,
`PointerPressed` and `PointerReleased`, added with tunnel routing so a child control that marks
an event handled does not hide it. Points whose pointer type is not `Pen` are ignored.

### Capture region

`IPenSession.CaptureRegion` (`WinPenKit/CaptureRegion.cs`) is a screen-pixel filter.
`IPenCaptureRegion.Contains(desktopX, desktopY)` is called after the position has been mapped
to desktop pixels and before the point is queued; a point outside is dropped. It may be set
before or after `Start` and applies from the next point.

Built-in regions (`PenCaptureRegion`):

- `Unbounded`: accepts every point.
- `Window(hwnd)`: the window's `GetWindowRect` bounds, read on every point, so it follows moves
  and resizes. The rectangle includes the window frame. A zero handle accepts every point, and
  so does a failed `GetWindowRect`.
- `Rect(x, y, width, height)`: a fixed rectangle. Left and top are inclusive, right and bottom
  exclusive.

What `null` means depends on the backend, and this table is what the code does:

| Backend | `CaptureRegion == null` |
|---|---|
| Wintab (system, digitizer) | `Window(hwnd passed to Start)`. `Start()` with no window gives `Window(IntPtr.Zero)`, which accepts every point: the session then reports the pen anywhere on the desktop, including over other applications. |
| WM_POINTER | `Window(hwnd passed to Start)`. `Start` refuses a zero handle. |
| WinForms | `Window(hwnd passed to Start)`, or the constructor's control handle if no window was passed and the handle exists, or no filter. The default exists because the message filter is application-wide. |
| WPF, WinUI, Avalonia | No filter. Points are limited only by which events the attached element receives. |

`Unbounded` changes nothing on backends without `PenCapabilities.GlobalCapture`: the OS still
delivers their input only for their window or control. Only the Wintab backends advertise it.

**Threading.** For Wintab, `Contains` runs on the pump thread. An implementation must be
thread-safe and must not touch UI objects. `WinPenKit.Avalonia.ControlCaptureRegion`
(`WinPenKit.Avalonia/ControlCaptureRegion.cs`) caches a control's screen rectangle on the UI
thread on layout and window-move events, and `Contains` reads only the cache. It returns `true`
while the bounds are unknown.

The test is a rectangle test. A point inside the region passes even when another window covers
it.

### Focus: OnActivated

`IPenSession.OnActivated()` is a default interface method that does nothing. The Wintab
sessions override it (`WintabSessionBase.OnActivated`): they call `WTEnable(ctx, true)` and then
`WTOverlap(ctx, true)`, the same pair and order as Qt's `QWindowsTabletSupport::notifyActivate`,
and log if either returns false.

Wintab delivers packets to the context on top of the driver's overlap order. When another
application takes focus, this context moves down that order and the driver does not move it
back. Without the call, the first stroke after returning to the application produces no points
and later strokes draw normally. This was reproduced in the Avalonia, WinForms and WPF samples.
The pump window never receives `WM_ACTIVATE`, so the application must make the call from its
own window's activation event:

| Sample | Where it calls it |
|---|---|
| WPF, WinForms, Avalonia | `Activated += (_, _) => _session?.OnActivated();` (`Scribble.Wpf/MainWindow.xaml.cs`) |
| WinUI | `Activated`, when `WindowActivationState` is not `Deactivated` (`Scribble.WinUI/MainWindow.xaml.cs`) |
| Win32 | `WM_ACTIVATE` calls `pen_session_on_activated` (`Scribble.Win32/src/main.cpp`) |
| Rust | on the rising edge of egui's viewport focus (`Scribble.Rust/src/main.rs`) |

A wrapper that holds an `IPenSession` rather than implementing it must forward the call, since
the default implementation does nothing (`Scribble.WinUI/WintabSessionWinUI3.cs`).

### Context keep-alive and reopen

Restarting the tablet service invalidates every open context without notifying the owner. The
managed Wintab sessions check for this in `KeepContextAlive`
(`WinPenKit/Wintab/WintabSessionBase.cs`), which both `DrainPoints` overloads call first:

1. At most once a second (a `Stopwatch` sets the next check time), call `WTGetA` on the handle.
   It returns false for a handle the driver no longer knows.
2. If false: log it, clear the handle, set `IsRunning` to false, and log the context counters.
3. Open a new context on the same pump window with the session's normal `OpenContext`. On
   success set `IsRunning` to true and log it. On failure log the error and wait five seconds
   before the next attempt. A refusing driver takes about 90 ms to answer, and while the
   service restarts it returns defaults that cannot be opened.

The check runs only inside a drain. An application that stops calling `DrainPoints` does not
recover. The native DLL has no equivalent. The measurements are in
[WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md#winpenkit-now-recovers-by-itself).

### Context counts and the log

The managed Wintab sessions log the driver's context counters (`WTI_STATUS/STA_CONTEXTS` open,
`WTI_INTERFACE/IFC_NCONTEXTS` stated maximum) before opening, after opening, after closing,
after losing a context and after reopening. A log with "after opening" and no "after closing"
came from a process that ended without `WTClose`. `WintabDiagnostics.ContextTable()`
(`WinPenKit/Diagnostics/WintabContextTable.cs`) returns the same pair. The counter is not a
capacity check: opens succeeded past the stated maximum of 32 up to 334.

The log is `%TEMP%\WinPenKit.<pid>.log`, one file per process (`WintabSessionBase.LogPath`,
also `WintabDiagnostics.LogPath`). It is truncated when the process first writes to it, kept
open for the life of the process with auto-flush, and flushed but not closed by `Dispose`, so
changing pen API does not erase it. Files older than seven days, and the old shared
`%TEMP%\WinPenKit.log`, are deleted when a new log is opened.

## Timing

### Queueing, HasNewData and DrainPoints

Every managed session queues points in a `ConcurrentQueue<PenPoint>` and sets a volatile
`HasNewData` flag when it enqueues. The native sessions use a `std::vector` under a mutex.

- `DrainPoints()` clears the flag, then dequeues everything and returns an array.
- `DrainPoints(Span<PenPoint>)` clears the flag, dequeues up to the span's length, and sets the
  flag again if points remain. It returns the number written and allocates nothing.
- Clearing happens before dequeuing, so a point enqueued by the producing thread during a drain
  sets the flag itself.

Both drains are thread-safe. The native `pen_session_drain_points` and
`pen_session_has_new_data` behave like the span overload. The Wintab sessions also run the
[context check](#context-keep-alive-and-reopen) at the start of each drain.

### Coalesced points

When the UI thread is busy, Windows and the frameworks merge several samples into one event.
Every pointer backend asks for the merged samples:

| Backend | Call | Order returned | Replayed |
|---|---|---|---|
| WM_POINTER, WinForms, native WM_POINTER | `GetPointerPenInfoHistory`, `WM_POINTERUPDATE` only, buffer of 64 entries, asked again with a larger buffer when more are held | newest first | in reverse, oldest first |
| WPF | `GetStylusPoints(element)` | oldest first | in order |
| WinUI | `GetIntermediatePoints(element)` | newest first (measured; Microsoft documents the opposite) | in reverse |
| Avalonia | `GetIntermediatePoints(element)` | oldest first (measured; undocumented) | in order |

The history path is used only when it returns more than one entry. With a count of one,
`GetPointerPenInfo` is used instead, because the single-entry history result differs from it.
On return, the history count is the number of entries Windows holds for the message, which
can be more than 64. The sessions then call again with a buffer of that size, and never read
past the buffer they hold. When a framework collection is empty or
null, `GetCurrentPoint` is used.

Measured on hardware at hand speed, Avalonia's `GetIntermediatePoints` returned one point per
event (`testdata/avalonia-hardware-stroke.csv`), so recovery matters when the application falls
behind rather than on every event.

### Timestamps per backend

`PenPoint.TimestampMicroseconds` is microseconds with no stated origin. Subtract two; do not
read one. The conversions are in `WinPenKit/PenTimestamp.cs`:

| Backend | Source field | Type | Conversion | Per point or per event | Wrap handling |
|---|---|---|---|---|---|
| Wintab | `PACKET.pkTime` | `uint` ms | `FromSystemTicks(pkTime, TickCount64)` | per point | anchored |
| WM_POINTER, WinForms | `POINTER_INFO.PerformanceCount` | `ulong` QPC ticks | `FromPerformanceCount` | per point | none needed |
| WPF | `StylusEventArgs.Timestamp` | `int` ms | `FromSystemTicks` | per event: a batch shares one | anchored |
| WinUI | `PointerPoint.Timestamp` | `ulong` µs | cast | per point | **not anchored** |
| Avalonia | `PointerEventArgs.Timestamp` | `ulong` ms, filled from 32-bit `GetMessageTime` | `FromSystemTicks` | per event: a batch shares one | anchored |

**Anchoring.** `FromSystemTicks` takes the nearest multiple of 2^32 ms that brings the reading
into agreement with `Environment.TickCount64`:
`extended = raw + round((now - raw) / 2^32) * 2^32`, then multiplies by 1000. It keeps no state,
so a wrap during an idle period, a stopped session, or a run of packets the capture region
dropped is still handled. It is valid only for clocks on the `GetTickCount64` epoch. Wintab's
`pkTime` was measured on that epoch on 13 Sep 2026 (`testdata/wintab-epoch-probe.csv`). The
result is never negative. The native Wintab session performs the same arithmetic against
`GetTickCount64`.

**WinUI** is not anchored because it is not established whether its microsecond value comes
from a 32-bit millisecond clock. If it does, it wraps after about 49.7 days of uptime.

`FromPerformanceCount` divides before multiplying, `(ticks / freq) * 10^6 + (ticks % freq) *
10^6 / freq`, so it does not overflow. It truncates below a microsecond.

Measured resolution, batching and the injection caveat are in [TIMESTAMPS.md](TIMESTAMPS.md).

## Position

`DesktopX/Y` are physical desktop pixels as `double` on every backend. How each backend gets
there:

### Wintab system

The context is the driver's default system context (`WTI_DEFSYSCTX`) with `lcOutExtY` made
negative so Y increases downward. `pkX/pkY` are used unchanged as desktop pixels
(`WinPenKit/Wintab/WintabSystemSession.cs`). Positions are whole pixels. `RefreshMapping` does
nothing: the driver maps.

### Wintab digitizer (high-res)

`WinPenKit/Wintab/WintabDigitizerSession.cs`:

1. Read `WTI_DEFSYSCTX` and cache its input range (`lcInOrg`, `lcInExt`) and system range
   (`lcSysOrg`, `lcSysExt`), with `lcSysExtY` made negative because the tablet's Y axis points
   up and the screen's points down.
2. Open a context from the same defaults with the output range set to the input range
   (`lcOutOrg = lcInOrg`, `lcOutExt = lcInExt`), so packets arrive in tablet units.
3. Map each packet with `ScaleAxis`:
   `desktop = (pk - inOrg) * |sysExt| / |inExt| + sysOrg`, or measured from the opposite edge
   when the signs of the two extents differ. The result keeps its fraction.

If the hi-res context fails to open, the session opens a system context instead, uses `pkX/pkY`
unchanged, clears `HiRes`, and reports `RawUnits.ScreenPixels`. If that also fails, the error
string includes the driver's context counters when the driver reports them.

`RefreshMapping()` re-reads `WTI_DEFSYSCTX` and replaces the cached mapping. It does not reopen
the context. Call it after a display change (monitor added or removed, resolution or scaling
changed, tablet remapped in the driver).

### Wacom is the reference

The digitizer maps onto `lcSys`, and the system context uses the driver's pixels, so both are
correct exactly when the driver describes the physical desktop correctly. WinPenKit does not
correct positions for any driver and has no cursor-based correction (issue #132):

- **Wacom** (Cintiq 16, Wintab32 1.0.5-10): reported physical pixels in all 24 mapping-wizard
  steps, with the tablet mapped to one display or to all of them and every scaling mix.
- **Huion V20** (Wintab32 20.0.0.4): on a desktop whose monitors use different Windows scaling,
  positions were scaled by the ratio of the monitors' scalings, up to about 400 px from the nib.
  With the tablet mapped to all displays the scaling depended on where the pen had been.
  `lcSys` did not match the physical desktop on that driver.

On a mixed-scaling desktop, Wintab positions are therefore correct only if the driver reports
physical pixels. [MAPPING-WIZARD.md](MAPPING-WIZARD.md) and `--probe-wintab-mapping`
([HOW_TO_USE.md](HOW_TO_USE.md#is-the-pen-landing-under-the-cursor)) compare Wintab against
the cursor on a given driver and layout. The research is in
[WINTAB-MAPPING-PRIOR-ART.md](WINTAB-MAPPING-PRIOR-ART.md).

### WM_POINTER and WinForms

`POINTER_INFO` carries the position as `ptPixelLocationRaw` (whole pixels) and
`ptHimetricLocationRaw` (hundredths of a millimetre in the device's own rectangle). The sessions
use the HIMETRIC field (`ResolvePosition` in `WinPenKit/Pointer/WmPointerSession.cs` and
`WinPenKit.WinForms/WinFormsPointerSession.cs`; `resolve_position` in
`WinPenKit.Native/src/wm_pointer_session_impl.cpp`):

```
desktopX = displayRect.Left + (himetricX - deviceRect.Left) / deviceRect.Width  * displayRect.Width
desktopY = displayRect.Top  + (himetricY - deviceRect.Top)  / deviceRect.Height * displayRect.Height
```

`deviceRect` and `displayRect` come from `GetPointerDeviceRects(sourceDevice)`. They are read
when a point arrives from a `sourceDevice` different from the last one, and cached. If the call
fails or the device rectangle is empty, the session uses `ptPixelLocationRaw` and clears
`HiRes`. `RefreshMapping` does nothing on these sessions and does not clear the cache.

Measured on a Wacom over Windows Ink, the median turn between consecutive segments was 11.31
degrees from the pixel field and 2.54 from the HIMETRIC field. Rounding the HIMETRIC result
matched the pixel field on 372 of 372 samples.

### Framework sessions (DIPs to pixels)

The frameworks report positions in device-independent pixels relative to the element. Each
session converts to desktop pixels without passing the pen position through an integer
`POINT`:

| Backend | Conversion | File |
|---|---|---|
| WPF | `WpfCoordinates.GetTransform(element)`: the window's client origin from `ClientToScreen` (integer, which is exact for an origin), plus the element's offset in the window from `TransformToAncestor` times `DpiScale`. Then `desktop = origin + stylusPoint * DpiScale`, computed once per event. `PointToScreen` is not used: it truncates every point to a whole pixel. | `WinPenKit.Wpf/WpfCoordinates.cs` |
| WinUI | Element DIPs plus `TransformToVisual(null)` origin give window DIPs. Multiplied by the window's monitor DPI / 96 (`GetDpiForMonitor`) and added to the client origin from `ClientToScreen`, both under a per-monitor-v2 thread DPI context. | `WinPenKit.WinUI/WinUiPointerSession.cs` |
| Avalonia | `TranslatePoint` to the top level gives window DIPs. Multiplied by `RenderScaling` and added to `PointToScreen(0, 0)`. `PointToScreen` returns an integer `PixelPoint`, so it is used for the window origin only. | `WinPenKit.Avalonia/AvaloniaPointerSession.cs` |

These sessions report `RawUnits.None` and write zero to `RawX/RawY`.

### Desktop to canvas

The application converts `DesktopX/Y` back to its canvas. The lossless method is the same in
every sample: get the canvas origin in desktop pixels from the framework, subtract it from
`DesktopX/Y` as `double`, and divide by the DPI scale if the canvas is in DIPs. The per-framework
code is in [HOW_TO_USE.md](HOW_TO_USE.md#coordinate-conversion).

## Values

### Fields per backend

| Field | Wintab | WM_POINTER, WinForms | WPF | WinUI | Avalonia |
|---|---|---|---|---|---|
| `Pressure` | `pkNormalPressure`, 0 to `axMax` | `pressure` if `PEN_MASK_PRESSURE`, else 0 | `PressureFactor * 1024` | `Pressure * 1024` | `Pressure * 1024` |
| `Azimuth`, `Altitude` | `orAzimuth / 10`, `orAltitude / 10` | computed from tilt | computed from tilt | computed from tilt | computed from tilt |
| `TiltX`, `TiltY` | computed from azimuth and altitude | `tiltX`, `tiltY` if masked, else 0 | `X/YTiltOrientation / 100` | `XTilt`, `YTilt` | `XTilt`, `YTilt` |
| `Twist` | `orTwist / 10` | `rotation` if `PEN_MASK_ROTATION`, else 0 | `TwistOrientation / 100` | `Twist` | `Twist` |
| `Z` | `pkZ` | 0 | 0 | 0 | 0 |
| `Status` | `pkStatus` (bit 0, `TPS_PROXIMITY`, is set when the pen is out of the context) | 0 | 0 | 0 | 0 |
| `Buttons` | `pkButtons`, relative: `(action << 16) \| button` | bit 0 `PEN_FLAG_BARREL`, bit 1 `PEN_FLAG_ERASER` | bit 0 any non-tip stylus button down, bit 1 `Inverted` | bit 0 `IsBarrelButtonPressed`, bit 1 `IsEraser` | bit 0 `IsBarrelButtonPressed`, bit 1 `IsEraser` |
| `Cursor` | `pkCursor`, passed through | 14 if `PEN_FLAG_INVERTED`, else 13 | 14 if `Inverted`, else 13 | 14 if `IsEraser`, else 13 | 14 if `IsEraser`, else 13 |

Tilt formulas, in degrees. `PenTilt` (`WinPenKit/PenTilt.cs`) converts in both directions, and
the native DLL uses the same relation in `WinPenKit.Native/src/tilt.h`. With
`θ = 90 - Altitude`, the angle from vertical, the relation is exact:
`tan(TiltX) = -tan(θ) * sin(Azimuth)` and `tan(TiltY) = tan(θ) * cos(Azimuth)`.
`PenTilt.ToPlanar(azimuth, altitude)` is used by the Wintab sessions.
`PenTilt.ToSpherical(tiltX, tiltY)` is used by the pointer backends; it returns `Altitude` from 0
to 90 and `Azimuth = atan2(-tan(TiltX), tan(TiltY))` mod 360, or 0 when the pen is within
`PenTilt.UprightThreshold` (0.5 degrees) of vertical. Earlier versions used the linear form
`TiltX = -(90 - Altitude) * sin(Azimuth)`. Values on the axes are unchanged; off the axes they
moved by up to 8.3 degrees (azimuth 45, altitude 30). The sign convention is unchanged, and
whether it matches the direction a Wintab driver means by `orAzimuth` has not been measured.

**Twist.** Every session sets `PenCapabilities.Twist`, and the native WM_POINTER session sets
`PEN_CAP_TWIST`. The flag means the backend reads twist from its API. It does not say that the
pen has a rotation sensor: a pen without one reports 0 on every backend.

**Pressure range.** `MaxPressure` is the largest value the device reports, not a count of
distinguishable levels. A Wacom DTH246 over Wintab reports 32767 and resolves 8192, in steps of
4. The pointer and framework sessions declare 1024, the API's fixed range.

**Cursor numbers.** 13 and 14 are the values observed from Wacom drivers (`PenCursorType`). The
pointer sessions write them; Wintab passes the driver's number through, so on a tablet that
numbers its eraser differently `IsEraser` is false on Wintab.

**Buttons.** `PenButtonTracker` (`WinPenKit/PenButtonTracker.cs`) decodes both encodings and
holds Wintab's state between events. The pointer encoding has one barrel bit, so a second or
third barrel button is only distinguishable on Wintab. `PenButtonNumber` names Wintab's button
numbers (0 tip, 1 to 3 barrel).

### Conventions

`IPenSession.Conventions` (`WinPenKit/PenConventions.cs`) states the meaning of the four fields
that differ by backend:

| Backend | `RawUnits` | `Buttons` | `Cursor` | `Timestamp` |
|---|---|---|---|---|
| Wintab digitizer, hi-res open | `TabletNative` | `WintabEvent` | `DeviceAssigned` | `DeviceTicks` |
| Wintab digitizer, fallen back; Wintab system | `ScreenPixels` | `WintabEvent` | `DeviceAssigned` | `DeviceTicks` |
| WM_POINTER, WinForms | `HundredthsOfMillimetre` | `PointerFlags` | `Normalised` | `PerformanceCounter` |
| WPF, WinUI, Avalonia | `None` | `PointerFlags` | `Normalised` | `SystemTicks` |

The C ABI returns the same four enums, at the same numeric values, from
`pen_session_get_conventions`. `PenRawUnitsExtensions.Label()` gives a short unit name for a
readout.

## Output

### PenPoint

`PenPoint` (`WinPenKit/PenPoint.cs`) is a `readonly record struct`: `DesktopX`, `DesktopY`
(`double`), `RawX`, `RawY` (`int`), `Pressure` (`uint`), `Azimuth`, `Altitude`, `Twist`, `TiltX`,
`TiltY` (`double`, degrees), `Z` (`int`), `Status`, `Buttons`, `Cursor` (`uint`), `Source`
(`InputApi`) and `TimestampMicroseconds` (`long`). The native `PenPoint` in `pen_session.h` has
the same fields in the same order; `pen_session_get_point_size` and
`pen_session_get_conventions_size` let a binding check its struct sizes at startup.

`IsEraser` compares `Cursor` with 14. `IsInProximity` is `(Status & TPS_PROXIMITY) == 0`, where
`TPS_PROXIMITY` is bit 0, which the Wintab specification defines as set when the cursor is out of
the context. A Wintab driver sets it on the packet it sends when the pen leaves, so
`IsInProximity` is false on that point. The pointer backends leave `Status` at 0, so the property
is true on every point they deliver. They deliver points only while the pen is in range and send
no point when it leaves; only sessions that advertise `PenCapabilities.Proximity` (Wintab) report
a leaving point. The bit's meaning has not been checked against a logged `pkStatus` stream.
`ButtonAction`, `ButtonNumber`, `IsTipPressed`, `IsButtonPressed` and
`IsButtonReleased` are obsolete: they decode the Wintab encoding on every point.

### IPenSession

`WinPenKit/IPenSession.cs`:

| Member | Purpose |
|---|---|
| `Start(IntPtr hwnd = default)` | Opens the input. Returns null on success or an error string. |
| `Stop()`, `Dispose()` | Closes the input. `Dispose` calls `Stop`; the Wintab sessions also flush the log. |
| `IsRunning` | Whether the session is producing points. |
| `HasNewData`, `DrainPoints()`, `DrainPoints(Span<PenPoint>)` | Polling. See [Timing](#queueing-hasnewdata-and-drainpoints). |
| `MaxPressure`, `Api`, `Capabilities`, `Conventions`, `DebugInfo` | Description of the session. |
| `CaptureRegion` | See [Capture region](#capture-region). |
| `RefreshMapping()` | Re-reads the Wintab digitizer mapping. No effect on other backends. |
| `OnActivated()` | See [Focus](#focus-onactivated). |

The managed Wintab sessions also implement `WinPenKit.Diagnostics.IPacketCounts`:
`PacketsFromDriver` (counted on the pump thread before any filtering), `PacketsOutsideCaptureRegion`
and `PointsDelivered`. Other sessions do not implement it; ask with a type test
(`session is IPacketCounts counts`).

## Managed and native

`WinPenKit` (C#) and `WinPenKit.Native` (C++) are two implementations of the same design. They
share no code.

| | Managed `WinPenKit` | Native `WinPenKit.Native` |
|---|---|---|
| Backends | Wintab system, Wintab digitizer, WM_POINTER, plus four framework sessions in separate packages | Wintab system, Wintab digitizer, WM_POINTER |
| Wintab discovery | `WTInfoA(0, 0, NULL)` non-zero | `Wintab32.dll` loads and its functions resolve |
| WM_POINTER discovery | `GetPointerType` exists | `GetPointerPenInfo` exists |
| Position mapping | as described above | the same: `scale_axis` onto `lcSys`, HIMETRIC through `GetPointerDeviceRects` |
| Timestamps | `PenTimestamp` | the same arithmetic inline |
| Tilt conversion | `PenTilt` | the same relation in `src/tilt.h` |
| `OnActivated` | `WTEnable` + `WTOverlap` | the same, through `pen_session_on_activated` |
| Context check and reopen | in every drain, once a second | **none** |
| Packet counts | `IPacketCounts` on the Wintab sessions | **none** |
| Context counters in the log | before and after open, after close, after loss and reopen | **none** |
| Fallback error | includes the driver's context counters | "Fallback context also failed to open." |
| Capture region | `IPenCaptureRegion` on every session | Wintab only: `pen_session_set_capture_window`, `_rect`, `_unbounded`. **The WM_POINTER session has no capture region** and reports whatever reaches the subclassed window. |
| Default Wintab region | the window passed to `Start`, unbounded with none | the same |
| Log file | `%TEMP%\WinPenKit.<pid>.log`, one per process | `%TEMP%\WintabSessionCpp.log`, one name for every process, truncated by each process's first write (`pen_session_get_log_path`) |
| Diagnostics API | `WinPenKit.Diagnostics` (below) | log path only |
| Draining | `DrainPoints()` returns an array; `DrainPoints(Span)` fills a buffer | `pen_session_drain_points` fills a buffer |

The native names `WintabSessionCpp.log`, the `WINTAB_SESSION_BUILDING` export macro and the
`WintabSessionPumpWindow` class name predate the `pen_session` API and still carry the old
name.

## Diagnostics

`WinPenKit/Diagnostics/` holds tools for checking a session and an application. They are public
but are not needed to use a session:

| Type | What it does |
|---|---|
| `IPacketCounts` | Packet counts on the Wintab sessions (see [IPenSession](#ipensession)). |
| `WintabDiagnostics` | `LogPath`, `ContextTable()` (open and stated-maximum context counts, or null), `DeviceName()`, `DriverScreen()` (the default system context's ranges as text). Reads only; opens nothing. |
| `WintabContextTable` | `Open`, `Maximum`, and `AboveStatedMaximum`, which is true when `Open > Maximum`. Not a capacity check. |
| `WintabMappingProbe` | Compares Wintab positions with the cursor on each monitor and mode. Run from `--probe-wintab-mapping`. |
| `WintabEpochProbe`, `WintabEpochSampler` | Read raw `pkTime` against `TickCount64` to establish its epoch. The sampler attaches to a running Wintab session (`TryAttach`). |
| `SelfTest`, `SelfTestReplay` | The `--selftest` and `--replay` checks. See [SELF-TEST.md](SELF-TEST.md). |
| `StrokeRecorder`, `StrokeReplay` | Write and read the `--record` and `--replay` recording format. |
| `PresentationProbe` | Draws two markers and finds them in a screen capture to measure how the surface is sampled. |

`WinPenKit.WindowPlacement.ClampToWorkArea(hwnd)` moves a window inside its monitor's work area,
since pen input over the part of a window off screen is not delivered.
`WinPenKit.Wpf.WpfCoordinates` is described under [Position](#framework-sessions-dips-to-pixels).

## Components

### Core Library: `WinPenKit`

**Role:** framework-agnostic pen input for .NET applications.

**Contains:**
- `IPenSession`, `PenPoint`, `PenSessionFactory`, `InputApi` and `InputApiExtensions`
- `PenCapabilities`, `PenConventions` and its four enums, `PenRawUnitsExtensions`
- `IPenCaptureRegion` and `PenCaptureRegion`
- `PenTimestamp`, `PenTilt`, `PenButtonTracker`, `PenButtonAction`, `PenButtonNumber`, `PenCursorType`
- `WindowPlacement`
- `WinPenKit.Diagnostics` (see [Diagnostics](#diagnostics))

**Backends (internal):**
- `WintabSessionBase`, `WintabMessagePump`: shared Wintab context, pump thread, queue, log
- `WintabSystemSession`: Wintab system context (screen pixels)
- `WintabDigitizerSession`: Wintab tablet-native context mapped with `ScaleAxis`
- `WmPointerSession`: WM_POINTER through `SetWindowSubclass`

**Dependencies:** none. It has its own Wintab P/Invoke layer (`Wintab/WintabNative.cs`) and
WM_POINTER P/Invoke (`Pointer/PointerNative.cs`).

### Native Library: `WinPenKit.Native`

**Role:** C ABI DLL for native consumers (C++, Rust, Zig).

**Contains:**
- `include/pen_session.h`: the public C API (PenPoint, discovery, lifecycle, conventions,
  capture region, polling, focus, log path)
- `src/wintab_session_impl.cpp/.h`: Wintab backend
- `src/wm_pointer_session_impl.cpp/.h`: WM_POINTER backend
- `src/pen_session_exports.cpp`: C ABI dispatching to the backends
- `src/wintab_loader.h`: loads `Wintab32.dll` and resolves its functions
- `src/scale_axis.h`: `ScaleAxis` with the Y-axis sign handling
- `src/log.h`: thread-safe file logger

**Output:** `WinPenKit.Native.dll` and `WinPenKit.Native.lib`.

**Dependencies:** none (loads `Wintab32.dll` dynamically).

### Framework Extensions

Each adds one `IPenSession` for a UI framework's own pen events, and a `GetAvailable()` for the
API dropdown.

| Package | Backend class | Input mechanism | Also contains | Dependency |
|---|---|---|---|---|
| `WinPenKit.WinUI` | `WinUiPointerSession` | XAML `PointerMoved`/`Pressed`/`Released` | `WinUiPenApis` | Windows App SDK |
| `WinPenKit.Wpf` | `WpfStylusSession` | `StylusMove`/`Down`/`Up`/`InAirMove` | `WpfPenApis`, `WpfCoordinates` | WPF |
| `WinPenKit.WinForms` | `WinFormsPointerSession` | `IMessageFilter` | `WinFormsPenApis` | WinForms |
| `WinPenKit.Avalonia` | `AvaloniaPointerSession` | Avalonia `PointerMoved`/`Pressed`/`Released`, tunnel routing | `AvaloniaPenApis`, `ControlCaptureRegion` | Avalonia |

Each references `WinPenKit` plus its framework.

### Scribble Apps

Demo applications. See [SCRIBBLE-APPS.md](SCRIBBLE-APPS.md).

Each WinPenKit app:
1. Discovers APIs through its framework package's `GetAvailable()`, or the factory or C ABI
2. Creates a session for the selected API and starts it with its window handle
3. Calls `OnActivated` from its window's activation event
4. Polls `DrainPoints()` on a render timer
5. Converts desktop pixels to canvas coordinates without integer rounding
6. Draws strokes to a bitmap (SkiaSharp, Skia's C API, tiny-skia, or GDI)
7. Shows pen telemetry in a ribbon

`Scribble.Qt` uses Qt's own tablet support and no WinPenKit, as an independent comparison.

### Supporting Projects

| Project | Role |
|---|---|
| `WinPenKit.TestConsole` | Console host for Wintab sessions, the clock self-test, and the Wintab epoch and mapping probes |
| `WinPenKit.MappingWizard` | WinForms tool that checks each pen API against the cursor across display and tablet configurations. See [MAPPING-WIZARD.md](MAPPING-WIZARD.md) |
| `Samples/ContextCount` | Plain C programs and scripts that read the Wintab context counters, leak a context on purpose, and run the context investigation. See [WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md) |
| `Scripts/Test-TabletServiceAccess.ps1` | Checks whether restarting the tablet service needs elevation on this machine |

## Dependency Graph

```
WinPenKit.WinUI ───┐
WinPenKit.Wpf ─────┤
WinPenKit.WinForms ┼──► WinPenKit (core)
WinPenKit.Avalonia ┘         │
                             │ (no dependency)
WinPenKit.Native             │ (independent C++ implementation)

Scribble.WinUI ──────► WinPenKit + WinPenKit.WinUI
Scribble.Wpf ────────► WinPenKit + WinPenKit.Wpf
Scribble.WinForms ───► WinPenKit + WinPenKit.WinForms
Scribble.Avalonia ───► WinPenKit + WinPenKit.Avalonia
Scribble.Win32 ──────► WinPenKit.Native (C ABI)
Scribble.WinUINative ► WinPenKit.Native (C ABI) + Windows App SDK + Skia C API
Scribble.Rust ───────► WinPenKit.Native (FFI)
Scribble.Qt ─────────► Qt 6 only (no WinPenKit)
WinPenKit.TestConsole ► WinPenKit
WinPenKit.MappingWizard ► WinPenKit
Samples/ContextCount ► Wintab32.dll only
```

`WinPenKit` and `WinPenKit.Native` are **peers**: two independent implementations of the same
concept. Neither depends on the other.

## Key Design Decisions

1. **Polling, not events.** All backends buffer internally. Apps poll with `DrainPoints()`. This gives one code path regardless of whether the backend uses a background thread (Wintab) or UI thread events (WM_POINTER, XAML).

2. **Desktop pixels as the universal coordinate space.** Every backend maps to physical desktop pixels as `double`. Applications convert to canvas-local coordinates. For Wintab, the pixels are those the driver describes, which match the physical desktop on Wacom and not on every driver (decision 12).

3. **Both tilt representations.** PenPoint carries Azimuth/Altitude (spherical) and TiltX/TiltY (planar), all in degrees (double). Each backend computes whichever it doesn't have natively.

4. **Factory for framework-agnostic, constructors for framework-specific.** `PenSessionFactory.Create()` handles Wintab and WM_POINTER. Framework-specific sessions need UI elements and are created directly by the app.

5. **No shared code between managed and native.** `WinPenKit` (C#) and `WinPenKit.Native` (C++) reimplement the same logic independently. The knowledge is shared via documentation, not code.

6. **Coalesced points are recovered on every pointer backend.** When the UI thread is busy, Windows and the frameworks merge several samples into one event. `WmPointerSession`, `WinFormsPointerSession` and the native WM_POINTER session call `GetPointerPenInfoHistory`, but only when `count > 1`: the `count == 1` history path returns different data from `GetPointerPenInfo` and loses data. `WpfStylusSession` drains `GetStylusPoints`. `WinUiPointerSession` and `AvaloniaPointerSession` call `GetIntermediatePoints`. The order of each collection was measured, not taken from documentation, and differs: pointer history and WinUI are newest first, Avalonia oldest first. Measured on a Wacom tablet at hand speed, Avalonia's call returned one point per event, so nothing was being coalesced at that rate. See [Coalesced points](#coalesced-points), issue 43 and #110.

7. **Wintab/WM_POINTER coexistence is driver-dependent.** Once a Wintab context has been opened, some drivers may suppress WM_POINTER for the process lifetime. In practice, runtime switching works cleanly when sessions are stopped/started sequentially, but this is not fully characterized across all driver versions.

8. **Runtime API switching without restart.** Qt-based apps like Krita require a restart to switch between Wintab and WM_POINTER because Qt's platform plugin makes the input-path decision at process startup (`-platform windows:nowmpointer`). WinPenKit avoids this because it owns the input layer directly. Sessions are independent objects, and switching is `session.Stop(); session = factory.Create(newApi); session.Start(hwnd);`. This runtime-switching capability is one of the strongest arguments for building our own unified session rather than depending on a framework's built-in tablet support.

9. **Spatial scope through a capture region.** The backends receive input over different areas: Wintab over the whole desktop, WM_POINTER over the window it subclasses, the framework sessions over their element. `IPenSession.CaptureRegion` is a screen-pixel `Contains` filter applied before a point is queued. With `null`, Wintab filters to the window passed to `Start` (and to nothing if none was passed), WM_POINTER and WinForms filter to their window, and WPF, WinUI and Avalonia do not filter. A custom region scopes every backend to the same area, and `PenCaptureRegion.Unbounded` gives desktop-wide capture on backends that advertise `PenCapabilities.GlobalCapture` (Wintab only). The filter runs on the producing thread, which is the pump thread for Wintab, so a region must be thread-safe and must not touch UI objects; `WinPenKit.Avalonia.ControlCaptureRegion` caches a control's screen rectangle on the UI thread for this reason. The filter is a rectangle test with no occlusion check. See [Capture region](#capture-region).

10. **Conventions are stated, not inferred.** Four `PenPoint` fields mean different things depending on which backend filled them in: `RawX`/`RawY` are tablet units, screen pixels, hundredths of a millimetre or nothing at all; `Buttons` is a Wintab event or a pointer bitmask; `Cursor` is normalised or the driver's own number; `TimestampMicroseconds` is counted on the performance counter, the system tick count, the driver's `pkTime`, or nothing. `IPenSession.Conventions` says which is in force, so a consumer does not infer it from `Api`. That inference held only while one API value implied one encoding, and stopped holding once two implementations of Wintab disagreed about it. The member has **no default implementation on purpose**: a backend that does not state its conventions will not compile, so the next divergence is a build error rather than something a reader finds later. `PenCapabilities` answers a different question, *supported or not*, and asking one flag to answer both is how a hi-res capability once stayed set after a fallback had turned hi-res off. The native C ABI carries the same four enums at the same values through `pen_session_get_conventions`, so a number means the same thing on either surface. See [Conventions](#conventions).

11. **Discovery is answered by the framework package, and names come from one place.** `PenSessionFactory.GetAvailableApis()` reports what the machine has, which is not what an application can offer. Two things it cannot see decide that: whether the host framework's own API is available, and whether `WmPointer` can reach the process at all -- it subclasses the window procedure, and WPF, WinForms, WinUI and Avalonia each consume pointer messages first. Each framework package therefore exposes its own `GetAvailable()` (`WpfPenApis`, `WinFormsPenApis`, `AvaloniaPenApis`, `WinUiPenApis`), so the knowledge lives beside the framework it is about rather than in a comment in each application. Before this, four samples repeated the same filter-and-append recipe and each carried the reason in a comment; an application written against the library carried it nowhere and would have offered a dead dropdown entry. The display name moved with it: `InputApi.Label()` and the C ABI's `pen_session_get_api_label` replace six hand-written tables across C#, C++ and Rust that each spelled "Wintab (high-res)" independently. That is the same fault decision 10 fixed for units, one level up -- a string with no single owner drifts. See issue 6.

12. **Wintab positions are used as the driver reports them.** The system context's pixels are passed through and the digitizer's tablet units are mapped onto the driver's `lcSys`. Wacom's driver was measured reporting physical pixels in every tested configuration and is taken as the reference. Huion's V20 driver was measured scaling positions on mixed-scaling desktops, and WinPenKit does not correct for it or for any other single vendor's driver (issue #132). See [Wacom is the reference](#wacom-is-the-reference).
