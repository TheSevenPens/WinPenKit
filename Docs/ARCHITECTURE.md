# Architecture

## Overview

WinPenSession is a layered pen input SDK. The core abstraction (`IPenSession`) sits between platform-specific input APIs and consumer applications. Each layer has a single responsibility.

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

### Core Library — `PenSession`

**Role:** Framework-agnostic pen input abstraction for .NET apps.

**Contains:**
- `IPenSession` — the interface all backends implement
- `PenPoint` — the universal pen data record (desktop coords, pressure, tilt, buttons)
- `PenSessionFactory` — discovers available APIs, creates sessions
- `InputApi` enum — identifies each backend
- `PenCapabilities` flags — advertises what a backend supports

**Backends (internal):**
- `WintabSystemSession` — Wintab system context (screen pixels)
- `WintabDigitizerSession` — Wintab digitizer (tablet-native hi-res + ScaleAxis)
- `WmPointerSession` — WM_POINTER via `SetWindowSubclass`

**Dependencies:** None. Has its own Wintab P/Invoke layer (`Wintab/WintabNative.cs`), message pump (`WintabMessagePump.cs`), and WM_POINTER P/Invoke (`Pointer/PointerNative.cs`).

### Native Library — `PenSession.Native`

**Role:** C ABI DLL for native consumers (C++, Rust, Zig).

**Contains:**
- `pen_session.h` — the public C API header (PenPoint struct, factory, lifecycle, polling)
- `wintab_session_impl.cpp/.h` — Wintab backend (C++)
- `wm_pointer_session_impl.cpp/.h` — WM_POINTER backend (C++)
- `pen_session_exports.cpp` — C ABI bridge dispatching to backends
- `wintab_loader.h` — RAII DLL loader for Wintab32.dll
- `scale_axis.h` — coordinate conversion with Y-axis sign-flip
- `log.h` — thread-safe file logger

**Output:** `PenSession.Native.dll` + `PenSession.Native.lib`

**Dependencies:** None (dynamically loads `Wintab32.dll` and `user32.dll`).

### Framework Extensions

Each extends `IPenSession` for a specific UI framework's native pointer events.

| Package | Backend class | Input mechanism | Dependency |
|---|---|---|---|
| `PenSession.WinUI` | `WinUiPointerSession` | XAML `PointerMoved` events | Windows App SDK |
| `PenSession.Wpf` | `WpfStylusSession` | WPF `StylusMove` events | WPF |
| `PenSession.WinForms` | `WinFormsPointerSession` | `IMessageFilter` | WinForms |
| `PenSession.Avalonia` | `AvaloniaPointerSession` | Avalonia `PointerMoved` events | Avalonia |

Each references `PenSession` (for `IPenSession` and `PenPoint`) plus its framework.

### Scribble Apps

Demo applications proving the SDK end-to-end. See [SCRIBBLE-APPS.md](SCRIBBLE-APPS.md) for details.

Each app:
1. Discovers APIs via factory (+ adds its framework-specific backend)
2. Creates a session for the selected API
3. Polls `DrainPoints()` on a 60fps timer
4. Converts desktop pixels to canvas coordinates (framework-specific)
5. Draws strokes to a bitmap (SkiaSharp, tiny-skia, or GDI)
6. Displays telemetry in a ribbon toolbar

### Supporting Projects

| Project | Role |
|---|---|
| `PenSession.TestConsole` | Headless Wintab testing |
| `ExtensionTestApp` | Tablet extension controls (ExpressKeys, Touch Rings) |
| `WintabDN` | Low-level Wintab .NET wrapper — used only by ExtensionTestApp |

## Dependency Graph

```
PenSession.WinUI ──┐
PenSession.Wpf ────┤
PenSession.WinForms┼──► PenSession (core)
PenSession.Avalonia┘         │
                              │ (no dependency)
PenSession.Native             │ (independent C++ implementation)
                              │
Scribble.WinUI ───► PenSession + PenSession.WinUI
Scribble.Wpf ─────► PenSession + PenSession.Wpf
Scribble.WinForms ─► PenSession + PenSession.WinForms
Scribble.Avalonia ─► PenSession + PenSession.Avalonia
Scribble.Win32 ────► PenSession.Native (via C ABI)
Scribble.Rust ─────► PenSession.Native (via FFI)
PenSession.TestConsole ► PenSession
ExtensionTestApp ──► WintabDN
```

`PenSession` and `PenSession.Native` are **peers** — two independent implementations of the same concept. Neither depends on the other.

## Key Design Decisions

1. **Polling, not events.** All backends buffer internally. Apps poll with `DrainPoints()`. This gives one code path regardless of whether the backend uses a background thread (Wintab) or UI thread events (WM_POINTER, XAML).

2. **Desktop pixels as the universal coordinate space.** Every backend normalizes to physical screen pixels. Framework adapters convert to canvas-local coordinates.

3. **Both tilt representations.** PenPoint carries Azimuth/Altitude (spherical) and TiltX/TiltY (planar), all in tenths of degree. Each backend computes whichever it doesn't have natively.

4. **Factory for framework-agnostic, constructors for framework-specific.** `PenSessionFactory.Create()` handles Wintab and WM_POINTER. Framework-specific sessions need UI elements and are created directly by the app.

5. **No shared code between managed and native.** `PenSession` (C#) and `PenSession.Native` (C++) reimplement the same logic independently. The knowledge is shared via documentation, not code.
