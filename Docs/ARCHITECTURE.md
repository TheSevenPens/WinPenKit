# Architecture

## Overview

WinPenKit is a layered pen input SDK. The core abstraction (`IPenSession`) sits between platform-specific input APIs and consumer applications. Each layer has a single responsibility.

```
┌─────────────────────────────────────────────────────────────┐
│  Applications (Scribble.WinUI, Scribble.Wpf, etc.)          │
│  - Framework-specific UI, rendering, coordinate conversion   │
│  - Polls IPenSession.DrainPoints() on a render timer         │
└──────────────────────────┬──────────────────────────────────┘
                           │  PenPoint stream (desktop pixels)
┌──────────────────────────┴──────────────────────────────────┐
│  IPenSession interface                                       │
│  - Start() / Stop() / Dispose()                              │
│  - DrainPoints() → PenPoint[]                                │
│  - MaxPressure, Api, Capabilities                            │
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

## Components

### Core Library — `WinPenKit`

**Role:** Framework-agnostic pen input abstraction for .NET apps.

**Contains:**
- `IPenSession` — the interface all backends implement
- `PenPoint` — the universal pen data record (desktop coords, pressure, tilt, buttons)
- `PenSessionFactory` — discovers available APIs, creates sessions
- `InputApi` enum — identifies each backend
- `PenCapabilities` flags — advertises what a backend *supports* (incl. `GlobalCapture`, `Proximity`)
- `PenConventions` — states which *convention* a backend's points follow: raw units, button encoding, cursor numbering, timestamp clock. A required `IPenSession` member with no default implementation; see design decision 10
- `IPenCaptureRegion` + `PenCaptureRegion` — screen-space spatial scope (`Unbounded` / `Window` / `Rect`); see design decision 9

**Backends (internal):**
- `WintabSystemSession` — Wintab system context (screen pixels)
- `WintabDigitizerSession` — Wintab digitizer (tablet-native hi-res + ScaleAxis)
- `WmPointerSession` — WM_POINTER via `SetWindowSubclass`

**Dependencies:** None. Has its own Wintab P/Invoke layer (`Wintab/WintabNative.cs`), message pump (`WintabMessagePump.cs`), and WM_POINTER P/Invoke (`Pointer/PointerNative.cs`).

### Native Library — `WinPenKit.Native`

**Role:** C ABI DLL for native consumers (C++, Rust, Zig).

**Contains:**
- `pen_session.h` — the public C API header (PenPoint struct, factory, lifecycle, polling)
- `wintab_session_impl.cpp/.h` — Wintab backend (C++)
- `wm_pointer_session_impl.cpp/.h` — WM_POINTER backend (C++)
- `pen_session_exports.cpp` — C ABI bridge dispatching to backends
- `wintab_loader.h` — RAII DLL loader for Wintab32.dll
- `scale_axis.h` — coordinate conversion with Y-axis sign-flip
- `log.h` — thread-safe file logger

**Output:** `WinPenKit.Native.dll` + `WinPenKit.Native.lib`

**Dependencies:** None (dynamically loads `Wintab32.dll` and `user32.dll`).

### Framework Extensions

Each extends `IPenSession` for a specific UI framework's native pointer events.

| Package | Backend class | Input mechanism | Dependency |
|---|---|---|---|
| `WinPenKit.WinUI` | `WinUiPointerSession` | XAML `PointerMoved` events | Windows App SDK |
| `WinPenKit.Wpf` | `WpfStylusSession` | WPF `StylusMove` events | WPF |
| `WinPenKit.WinForms` | `WinFormsPointerSession` | `IMessageFilter` | WinForms |
| `WinPenKit.Avalonia` | `AvaloniaPointerSession` | Avalonia `PointerMoved` events | Avalonia |

Each references `WinPenKit` (for `IPenSession` and `PenPoint`) plus its framework.

### Scribble Apps

Demo applications proving the SDK end-to-end. See [SCRIBBLE-APPS.md](SCRIBBLE-APPS.md) for details.

Each app:
1. Discovers APIs via its framework package's `GetAvailable()`
2. Creates a session for the selected API
3. Polls `DrainPoints()` on a 60fps timer
4. Converts desktop pixels to canvas coordinates (framework-specific)
5. Draws strokes to a bitmap (SkiaSharp, tiny-skia, or GDI)
6. Displays telemetry in a ribbon toolbar

### Supporting Projects

| Project | Role |
|---|---|
| `WinPenKit.TestConsole` | Headless Wintab testing |
| `ExtensionTestApp` | Tablet extension controls (ExpressKeys, Touch Rings) |
| `WintabDN` | Low-level Wintab .NET wrapper — used only by ExtensionTestApp |

## Dependency Graph

```
WinPenKit.WinUI ──┐
WinPenKit.Wpf ────┤
WinPenKit.WinForms┼──► WinPenKit (core)
WinPenKit.Avalonia┘         │
                              │ (no dependency)
WinPenKit.Native             │ (independent C++ implementation)
                              │
Scribble.WinUI ───► WinPenKit + WinPenKit.WinUI
Scribble.Wpf ─────► WinPenKit + WinPenKit.Wpf
Scribble.WinForms ─► WinPenKit + WinPenKit.WinForms
Scribble.Avalonia ─► WinPenKit + WinPenKit.Avalonia
Scribble.Win32 ────► WinPenKit.Native (via C ABI)
Scribble.Rust ─────► WinPenKit.Native (via FFI)
WinPenKit.TestConsole ► WinPenKit
ExtensionTestApp ──► WintabDN
```

`WinPenKit` and `WinPenKit.Native` are **peers** — two independent implementations of the same concept. Neither depends on the other.

## Key Design Decisions

1. **Polling, not events.** All backends buffer internally. Apps poll with `DrainPoints()`. This gives one code path regardless of whether the backend uses a background thread (Wintab) or UI thread events (WM_POINTER, XAML).

2. **Desktop pixels as the universal coordinate space.** Every backend normalizes to physical screen pixels. Framework adapters convert to canvas-local coordinates.

3. **Both tilt representations.** PenPoint carries Azimuth/Altitude (spherical) and TiltX/TiltY (planar), all in degrees (double). Each backend computes whichever it doesn't have natively.

4. **Factory for framework-agnostic, constructors for framework-specific.** `PenSessionFactory.Create()` handles Wintab and WM_POINTER. Framework-specific sessions need UI elements and are created directly by the app.

5. **No shared code between managed and native.** `WinPenKit` (C#) and `WinPenKit.Native` (C++) reimplement the same logic independently. The knowledge is shared via documentation, not code.

6. **WM_POINTER coalescing requires history recovery.** When the UI thread is busy, Windows coalesces multiple `WM_POINTERUPDATE` messages into one. `WmPointerSession` calls `GetPointerPenInfoHistory` to recover all intermediate points — but only when `count > 1`. The `count == 1` history path returns subtly different data that causes silent data loss. `WpfStylusSession` drains `GetStylusPoints`, which carries the same recovery. `WinUiPointerSession` and `AvaloniaPointerSession` take only `GetCurrentPoint` and do **not** recover intermediate points -- the claim that those frameworks decoalesce internally was never established, and `GetIntermediatePoints` exists on both precisely because the caller has to ask. Measured at hand speed on a Wacom tablet it makes no difference: 131.6 points per 1000px on Avalonia against 112.8 on WinForms, which does recover them, and no gaps where a dropped batch would show. Nothing is being coalesced at that rate. See issue 43.

7. **Wintab/WM_POINTER coexistence is driver-dependent.** Once a Wintab context has been opened, some drivers may suppress WM_POINTER for the process lifetime. In practice, runtime switching works cleanly when sessions are stopped/started sequentially, but this is not fully characterized across all driver versions.

8. **Runtime API switching without restart.** Qt-based apps like Krita require a restart to switch between Wintab and WM_POINTER because Qt's platform plugin makes the input-path decision at process startup (`-platform windows:nowmpointer`). WinPenKit avoids this because it owns the input layer directly — sessions are independent objects, and switching is `session.Stop(); session = factory.Create(newApi); session.Start();`. This runtime-switching capability is one of the strongest arguments for building our own unified session rather than depending on a framework's built-in tablet support.

9. **Consistent spatial scope via capture region.** The backends natively disagree on *where the pen must be* to produce data — Wintab is desktop-global, WM_POINTER is window-scoped, the framework pointer sessions are control-scoped. `IPenSession.CaptureRegion` (a screen-pixel `Contains` filter applied before points are queued) normalizes this: `null` defaults every backend to window scope (Wintab stores the `Start` hwnd and filters to it), a custom region scopes all backends identically, and `PenCaptureRegion.Unbounded` re-enables desktop-wide capture on backends that advertise `PenCapabilities.GlobalCapture` (Wintab only). The filter runs on the *producing* thread — the background pump thread for Wintab — so regions must be thread-safe and UI-free; `WinPenKit.Avalonia.ControlCaptureRegion` caches a control's screen rect on the UI thread for exactly this reason. v1 filters by rectangle only (no occlusion / z-order). See [STYLUS.md → Spatial Scope](STYLUS.md#spatial-scope-capture-region) and [HOW_TO_USE.md](HOW_TO_USE.md#capture-region-spatial-scope).

10. **Conventions are stated, not inferred.** Three `PenPoint` fields mean different things depending on which backend filled them in: `RawX`/`RawY` are tablet units, screen pixels, hundredths of a millimetre or nothing at all; `Buttons` is a Wintab event or a pointer bitmask; `Cursor` is normalised or the driver's own number. `IPenSession.Conventions` says which is in force, so a consumer does not infer it from `Api` — an inference that held only while one API value implied one encoding, and broke once two implementations of Wintab disagreed about it. The member has **no default implementation on purpose**: a backend that does not state its conventions will not compile, so the next divergence is a build error rather than something a reader finds later. `PenCapabilities` answers a different question — *supported or not* — and asking one flag to answer both is how a hi-res capability once survived a fallback that had turned hi-res off. The native C ABI carries the same three enums at the same values through `pen_session_get_conventions`, so a number means the same thing on either surface. See [HOW_TO_USE.md → Conventions](HOW_TO_USE.md#conventions).

11. **Discovery is answered by the framework package, and names come from one place.** `PenSessionFactory.GetAvailableApis()` reports what the machine has, which is not what an application can offer. Two things it cannot see decide that: whether the host framework's own API is available, and whether `WmPointer` can reach the process at all -- it subclasses the window procedure, and WPF, WinForms, WinUI and Avalonia each consume pointer messages first. Each framework package therefore exposes its own `GetAvailable()` (`WpfPenApis`, `WinFormsPenApis`, `AvaloniaPenApis`, `WinUiPenApis`), so the knowledge lives beside the framework it is about rather than in a comment in each application. Before this, four samples repeated the same filter-and-append recipe and each carried the reason in a comment; an application written against the library carried it nowhere and would have offered a dead dropdown entry. The display name moved with it: `InputApi.Label()` and the C ABI's `pen_session_get_api_label` replace six hand-written tables across C#, C++ and Rust that each spelled "Wintab (high-res)" independently. That is the same fault decision 10 fixed for units, one level up -- a string with no single owner drifts. See issue 6.
