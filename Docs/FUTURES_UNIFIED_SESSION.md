# Unified Pen Session

**Status: Implemented (Phase 5, updated Phase 6).** This document served as the design guide for the PenSession library. The design has been fully implemented and tested across 7 scribble apps, 6 UI frameworks, and 2 languages. It remains as architectural documentation and a reference for the decisions made.

---

A unified pen input abstraction that hides the differences between Windows pen APIs behind a single session interface. A paint app creates a session, starts it, and gets pen points — without knowing or caring whether Wintab, WM_POINTER, or a framework-specific pointer API is providing them.

## The Problem

Today's `WintabSession` does a great job of hiding Wintab complexity (context creation, packet handling, coordinate mapping, threading). But it's Wintab-specific. A paint app that wants to support multiple input APIs would need to know about each one:

```
App code today:
  "Create a WintabSession, call Start(WintabResolution.Screen), poll for PenPoints"

What we want:
  "Create a PenSession, call Start(), poll for PenPoints"
```

The app shouldn't need to know which API is running. The session abstraction handles:
- API discovery (what's available on this machine?)
- Context/device setup
- Coordinate normalization to desktop pixels
- Threading differences (background thread vs UI thread)
- Pressure range normalization
- Tilt/orientation format differences
- Lifecycle (start, stop, switch, cleanup)

## Available APIs

See the [Windows Pen API Landscape](https://github.com/TheSevenPens/devnotes) in the devnotes repo for the full comparison. Summary:

| API | Pressure Range | Tilt Format | Coords | Threading | Twist | Hi-Res |
|---|---|---|---|---|---|---|
| Wintab System | 0–device max | Azimuth/Altitude (tenths of degree) | Physical pixels | Background 200+ Hz | Yes | No |
| Wintab Digitizer | 0–device max | Azimuth/Altitude (tenths of degree) | Tablet native | Background 200+ Hz | Yes | Yes |
| WM_POINTER | 0–1024 | TiltX/TiltY (degrees, -90 to +90) | Physical pixels | UI thread | Yes | No |
| WinUI PointerPoint | 0.0–1.0 | TiltX/TiltY (degrees) | DIPs | UI thread | No | No |
| RealTimeStylus | 0–device max | TiltX/TiltY | HIMETRIC | Configurable | Yes | Partial |

Key differences the abstraction must hide:
1. **Pressure range** — raw int vs fixed 0–1024 vs pre-normalized float
2. **Tilt representation** — Azimuth/Altitude (spherical) vs TiltX/TiltY (planar)
3. **Coordinate space** — physical pixels vs DIPs vs HIMETRIC vs tablet-native
4. **Threading model** — background thread polling vs UI thread events
5. **Feature availability** — not all APIs report twist, Z, or barrel buttons

## Proposed Architecture

```
┌──────────────────────────────────────────────────────────┐
│  Paint App                                                │
│                                                           │
│  ┌──────────┐  ┌──────────┐  ┌─────────────────────────┐ │
│  │ Tool UI  │  │  Brush   │  │  Canvas / Renderer      │ │
│  │          │  │  Engine   │  │                         │ │
│  └────┬─────┘  └────▲─────┘  └─────────────────────────┘ │
│       │              │                                    │
│  ┌────▼──────────────┴──────────────────────────────────┐ │
│  │  CanvasAdapter (per framework)                        │ │
│  │   - Desktop → canvas coordinate conversion            │ │
│  │   - Owns render timer or event wiring                 │ │
│  │   - Feeds brush engine with canvas-local PenPoints    │ │
│  └───────────────────▲──────────────────────────────────┘ │
└───────────────────────┼──────────────────────────────────┘
                        │  PenPoint stream (desktop coords)
┌───────────────────────┴──────────────────────────────────┐
│  IPenSession                                              │
│                                                           │
│   Start() / Stop() / Dispose()                            │
│   DrainPoints(buffer, max) → int                          │
│   MaxPressure → int                                       │
│   IsRunning → bool                                        │
│   Capabilities → PenCapabilities                          │
│                                                           │
│  Implementations:                                         │
│   ├── WintabSystemSession      ✅ Done (C# + C++)         │
│   ├── WintabDigitizerSession   ✅ Done (C# + C++)         │
│   ├── WmPointerSession         ✅ Done (C# + C++)         │
│   ├── WinUiPointerSession      ✅ Done (C# in PenSession. │
│   │                                WinUI package)          │
│   ├── WinFormsPointerSession   ✅ Done (C# in PenSession. │
│   │                                WinForms package)       │
│   └── RealTimeStylusSession    (future, low priority)     │
└───────────────────────────────────────────────────────────┘
```

## The PenPoint Contract

All sessions produce the same `PenPoint`. This is the universal currency.

```csharp
record struct PenPoint
{
    // Position — always physical desktop pixels (double for sub-pixel precision)
    double DesktopX;
    double DesktopY;

    // Raw values from the underlying API (for diagnostics/logging)
    int RawX, RawY;

    // Pressure — always 0 to MaxPressure (query session for MaxPressure)
    uint Pressure;

    // Orientation — all fields in tenths of a degree for consistency.
    // Both spherical and planar representations are provided.
    //
    // Spherical (native for Wintab, computed for WM_POINTER):
    // Azimuth: 0–3600 (clockwise from north)
    // Altitude: 0–900 (0=horizontal, 900=vertical)
    //
    // Planar (native for WM_POINTER, computed for Wintab):
    // TiltX: -900 to +900 (positive = tilt right)
    // TiltY: -900 to +900 (positive = tilt toward user)
    //
    // Twist: 0–3600 (barrel rotation)
    int Azimuth, Altitude, Twist;
    int TiltX, TiltY;

    // Height above tablet surface
    int Z;

    // Button and cursor state
    uint Buttons;
    uint Cursor;
    uint Status;

    // Which API produced this point
    InputApi Source;
}
```

### Normalization Decisions

| Field | Normalization rule | Rationale |
|---|---|---|
| DesktopX/Y | Physical screen pixels | Common ground — every API can produce these. Framework adapters convert to canvas coords. |
| Pressure | Raw integer, 0 to session.MaxPressure | Avoids precision loss from pre-normalizing. Brush engine normalizes when it needs to. |
| Azimuth/Altitude | Tenths of degree (Wintab convention) | Spherical representation. WM_POINTER's TiltX/TiltY converted to this. |
| TiltX/TiltY | Tenths of degree | Planar representation. Wintab's Azimuth/Altitude converted to this. Both representations always present. |
| Twist | Tenths of degree | Same unit as all orientation fields. APIs that don't support twist report 0. |
| Buttons | Raw from API | Button encoding varies (Wintab relative mode vs WM_POINTER flags). Needs more thought — see open questions. |

### Tilt Conversion (Bidirectional)

PenPoint carries **both** tilt representations simultaneously. Each backend computes whichever it doesn't have natively. All values are in tenths of degree.

**WM_POINTER → Spherical** (TiltX/TiltY native, compute Azimuth/Altitude):
```
tiltMag  = sqrt(tiltX^2 + tiltY^2)         // tenths of degree from vertical
Altitude = 900 - tiltMag
Azimuth  = atan2(-tiltX, tiltY) * 1800/PI  (mod 3600)
```

**Wintab → Planar** (Azimuth/Altitude native, compute TiltX/TiltY):
```
tiltMag = 900 - Altitude                    // tenths of degree from vertical
TiltX   = -tiltMag * sin(Azimuth * PI/1800)
TiltY   =  tiltMag * cos(Azimuth * PI/1800)
```

These conversions are lossy at extreme angles but accurate enough for brush engines. Consumers use whichever representation suits their algorithm — calligraphy brushes may prefer Azimuth, physics-based brushes may prefer TiltX/TiltY.

## IPenSession Interface (Implemented)

```csharp
interface IPenSession : IDisposable
{
    // Lifecycle — appWindowHandle needed for WM_POINTER (subclassing);
    // pass IntPtr.Zero for Wintab (creates own hidden window)
    string? Start(IntPtr appWindowHandle = default);
    void Stop();
    bool IsRunning { get; }

    // Output — polling model
    PenPoint[] DrainPoints();
    int DrainPoints(Span<PenPoint> buffer);  // zero-alloc overload
    bool HasNewData { get; }

    // Properties
    int MaxPressure { get; }
    PenCapabilities Capabilities { get; }
    InputApi Api { get; }
    string DebugInfo { get; }

    // Mapping
    void RefreshMapping();
}

enum InputApi
{
    WintabSystem,      // screen-pixel Wintab
    WintabDigitizer,   // hi-res tablet-native Wintab
    WmPointer,         // Win32 window subclassing (raw Win32 apps only)
    WinUiPointer,      // XAML PointerPoint events (WinUI 3 apps only)
    WpfStylus,         // WPF StylusMove/StylusDown events (WPF apps only)
    AvaloniaPointer,   // Avalonia PointerMoved/PointerPressed events (Avalonia apps only)
    WinFormsPointer    // IMessageFilter WM_POINTER interception (WinForms apps only)
}
```

## Polling vs Events

Current WintabSession uses a polling model: background thread enqueues, UI timer drains. This works well and keeps the UI in control of when to process input.

WM_POINTER delivers on the UI thread via window messages — no background thread. Two approaches:

**Option A: Polling everywhere (recommended)**
The WM_POINTER session still enqueues to an internal buffer. The app polls with `DrainPoints()` on its render timer, same as Wintab. Adds one frame of latency (~16ms) for pointer input, but the app code is identical regardless of API.

**Option B: Dual model**
Add an event-based path (`event Action<PenPoint> OnPoint`) alongside polling. WM_POINTER sessions fire events directly. More complex, and the app/adapter must handle both patterns.

Option A is simpler and the latency is negligible for a paint app.

### WM_POINTER Event Coalescing

An important caveat with Option A: when the app's UI thread is busy (GPU rendering, layout, texture upload), Windows coalesces multiple `WM_POINTERUPDATE` messages into one. The most recent position is delivered, but intermediate positions are lost — causing visible "polygon" strokes instead of smooth curves.

**PenSession's `WmPointerSession` handles this automatically** by calling `GetPointerPenInfoHistory` to recover all coalesced events from each message. The history is processed in chronological order and all points are enqueued to the buffer. The app's `DrainPoints()` call then gets the full set of points, producing smooth strokes.

**Framework-specific sessions (WinUI, WPF, Avalonia) are not affected** because those frameworks decoalesce pointer events internally before delivering them to the app's event handlers. Only raw Win32 `WM_POINTER` subclassing requires explicit history recovery.

## Session Factory

```csharp
static class PenSessionFactory
{
    // Discover what's available
    static IReadOnlyList<InputApi> GetAvailableApis();

    // Create a session for a specific API
    static IPenSession Create(InputApi api);

    // Create the best available session (Wintab preferred if available)
    static IPenSession CreateDefault();
}
```

Discovery logic:
1. Try to load `Wintab32.dll` — if found, Wintab APIs are available
2. Check Windows version — WM_POINTER available on Windows 8+
3. Check for RealTimeStylus COM registration
4. Return available options so the UI can populate a dropdown

## What Changes from Today

| Today | Unified |
|---|---|
| `WintabSession` class with `Start(WintabResolution)` | `IPenSession` with resolution baked into the implementation |
| App knows it's using Wintab | App knows it has a PenSession |
| `WintabResolution.Screen` / `.Digitizer` | `InputApi.WintabSystem` / `.WintabDigitizer` |
| PenPoint.Source is an afterthought | PenPoint.Source tells the brush engine where data came from |
| Button encoding is Wintab-specific | Needs a unified button model (see open questions) |

## What Stays the Same

- **PenPoint** as the data format — already designed to be API-neutral
- **Polling model** — DrainPoints() with a render timer
- **Desktop coordinates** — all APIs normalize to physical screen pixels
- **Separation of concerns** — session produces raw data, brush engine interprets it
- **Thread safety** — mutex/lock on the point buffer, same pattern

## Open Questions

### 1. Button Encoding (Partially Resolved)
Current approach: PenPoint stores raw `Buttons` field. Wintab uses relative encoding `(action << 16) | buttonNumber`. WM_POINTER/WinUI use flag bitmasks. Helper properties (`ButtonAction`, `ButtonNumber`, `IsEraser`) provide a common interface. A fully unified button model (normalized bitmask) is still deferred.

### 2. Eraser Detection (Resolved)
All sessions map eraser state to `Cursor == 14` (the Wintab eraser cursor ID). Wintab detects via cursor type change. WM_POINTER detects via `PEN_FLAG_INVERTED`. WinUI detects via `Properties.IsEraser`. All converge on the same PenPoint representation.

### 3. Managed vs Native (Resolved)
Both exist:
- **C#:** PenSession.dll (Wintab + WmPointer) + PenSession.WinUI.dll (WinUI Pointer)
- **C++:** pen_session.h API in PenSession.Native.dll (Wintab + WmPointer)

### 4. WinUI PointerPoint (Resolved)
Implemented as `WinUiPointerSession` in `PenSession.WinUI.dll`. WinUI 3 apps offer all four APIs in a dropdown: System, Digitizer, WM_Pointer (won't work — see framework-specificity section above), and WinUI Pointer.

### 5. API Coexistence
Wintab and WM_POINTER may conflict at the driver level (see [Wintab and Windows Ink Coexistence](https://github.com/TheSevenPens/devnotes) in the devnotes repo). In practice, switching between them via the dropdown works because `Stop()` closes the previous session before `Start()` opens the new one. However, some driver configurations may require that Wintab32.dll not be loaded at all for WM_POINTER to work correctly. This is driver-dependent and not fully tested across all configurations.

## Implementation Status

| Session | C# | C++ | Tested in |
|---|---|---|---|
| WintabSystemSession | PenSession.dll | PenSession.Native.dll | Scribble.Win32, Scribble.WinUI, Scribble.Wpf, TestConsole |
| WintabDigitizerSession | PenSession.dll | PenSession.Native.dll | Scribble.Win32, Scribble.WinUI, Scribble.Wpf, TestConsole |
| WmPointerSession | PenSession.dll | PenSession.Native.dll | Scribble.Win32 |
| WinUiPointerSession | PenSession.WinUI.dll | N/A | Scribble.WinUI |
| WpfStylusSession | PenSession.Wpf.dll | N/A | Scribble.Wpf |
| AvaloniaPointerSession | PenSession.Avalonia.dll | N/A | Scribble.Avalonia |
| WinFormsPointerSession | PenSession.WinForms.dll | N/A | Scribble.WinForms |
| RealTimeStylusSession | Not implemented | Not implemented | — |

## Framework-Specific vs Framework-Agnostic Sessions

Not all session types work in all apps. The key distinction is **how the session receives input**:

| Session | Input mechanism | Works in |
|---|---|---|
| **WintabSystem** | Creates own hidden window, receives WT_PACKET on background thread | Any app — Win32, WinUI 3, WPF, WinForms |
| **WintabDigitizer** | Same as above | Any app |
| **WmPointer** | Subclasses the app's HWND via `SetWindowSubclass` | Raw Win32 apps only (e.g., Scribble.Win32) |
| **WinUiPointer** | Attaches to XAML `UIElement.PointerMoved` events | WinUI 3 apps only |
| **WpfStylus** | Attaches to WPF `StylusMove`/`StylusDown` events | WPF apps only |
| **AvaloniaPointer** | Attaches to Avalonia `PointerMoved`/`PointerPressed` events | Avalonia apps only |
| **WinFormsPointer** | `IMessageFilter` intercepts `WM_POINTER` messages application-wide | WinForms apps only |

**Why Wintab works everywhere:** Wintab sessions create their own hidden Win32 window on a background thread. The Wacom driver delivers `WT_PACKET` messages to that window regardless of what UI framework the app uses. The session is completely decoupled from the app's windowing model.

**Why non-Wintab sessions are framework-specific:** Each UI framework intercepts pen input through its own path:
- **Raw Win32:** `WM_POINTER` messages reach the HWND directly → subclassing works
- **WinUI 3:** Routes input through its composition InputSite → `WM_POINTER` never reaches the HWND → must use XAML `PointerMoved` events
- **WPF:** Routes pen input through its Wisp/RealTimeStylus stack → `WM_POINTER` subclassing gets no data → must use WPF `StylusMove` events
- **Avalonia:** Routes pointer input through its own event system → must use Avalonia `PointerMoved` events
- **WinForms:** `NativeWindow.AssignHandle` on a Form HWND crashes (exit code -1) because it conflicts with WinForms' internal `NativeWindow` that already owns the handle. `SetWindowSubclass` also doesn't work reliably. The solution is `IMessageFilter`, which intercepts `WM_POINTER` messages application-wide at the message pump level without touching HWND ownership

**Package structure reflecting this:**

```
PenSession.dll              ← framework-agnostic (no UI framework dependency)
  ├── IPenSession            ← the interface all sessions implement
  ├── PenSessionFactory      ← creates Wintab + WmPointer sessions
  ├── WintabSystemSession    ← works in any app
  ├── WintabDigitizerSession ← works in any app
  └── WmPointerSession       ← works in raw Win32 apps

PenSession.WinUI.dll        ← requires Windows App SDK
  └── WinUiPointerSession    ← works in WinUI 3 apps only

PenSession.Wpf.dll          ← requires WPF
  └── WpfStylusSession       ← works in WPF apps only

PenSession.Avalonia.dll     ← requires Avalonia
  └── AvaloniaPointerSession ← works in Avalonia apps only

PenSession.WinForms.dll     ← requires WinForms
  └── WinFormsPointerSession ← works in WinForms apps only (IMessageFilter)
```

Each app type references PenSession.dll plus its framework extension:
- **Raw Win32 / Console:** PenSession.dll only
- **WinUI 3:** PenSession.dll + PenSession.WinUI.dll
- **WPF:** PenSession.dll + PenSession.Wpf.dll
- **Avalonia:** PenSession.dll + PenSession.Avalonia.dll
- **WinForms:** PenSession.dll + PenSession.WinForms.dll

## Using PenSession in WinUI 3

WinUI 3 apps have access to all four input APIs. Setup requires:

### 1. Project references

```xml
<ProjectReference Include="..\PenSession\PenSession.csproj" />
<ProjectReference Include="..\PenSession.WinUI\PenSession.WinUI.csproj" />
```

### 2. Discover APIs and add WinUiPointer

`PenSessionFactory.GetAvailableApis()` returns the framework-agnostic APIs (Wintab, WmPointer). The app manually adds `WinUiPointer` since the factory doesn't know about WinUI types:

```csharp
var apis = new List<InputApi>(PenSessionFactory.GetAvailableApis());
apis.Add(InputApi.WinUiPointer);  // always available in WinUI 3
```

### 3. Create sessions — factory for most, constructor for WinUI Pointer

```csharp
IPenSession session = api == InputApi.WinUiPointer
    ? new WinUiPointerSession(canvasElement, hwnd)  // needs UIElement + HWND
    : PenSessionFactory.Create(api);                 // factory handles the rest
```

The `WinUiPointerSession` needs:
- A `UIElement` to attach pointer events to (typically the drawing canvas)
- The app window `HWND` for DIP → screen pixel coordinate conversion

### 4. Start with HWND

```csharp
var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
session.Start(hwnd);  // Wintab ignores this; WmPointer uses it for subclassing
```

### 5. DPI manifest for unpackaged apps

Unpackaged WinUI 3 apps (`WindowsPackageType=None`) must declare DPI awareness in `app.manifest` to avoid blurry rendering and coordinate mismatches:

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  </windowsSettings>
</application>
```

### 6. Why WM_Pointer doesn't work in WinUI 3

WinUI 3 (Windows App SDK) routes pen input through its own composition InputSite, not through traditional `WM_POINTER` messages on the top-level HWND. If a WinUI 3 app selects the WM_Pointer session:
- `SetWindowSubclass` succeeds on the HWND
- But no `WM_POINTERUPDATE` messages are ever delivered to it
- The session reports no data — drawing appears broken

This is not a bug — it's how WinUI 3's input architecture works. The solution is `WinUiPointerSession`, which hooks into the XAML event path that WinUI 3 actually uses for pen input.

### Complete WinUI 3 example

```csharp
// In MainWindow constructor:
var apis = new List<InputApi>(PenSessionFactory.GetAvailableApis());
apis.Add(InputApi.WinUiPointer);
toolbar.SetAvailableApis(apis);

// When starting:
var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

IPenSession session = selectedApi == InputApi.WinUiPointer
    ? new WinUiPointerSession(myCanvas, hwnd)
    : PenSessionFactory.Create(selectedApi);

var error = session.Start(hwnd);
if (error != null) { ShowError(error); return; }

// Polling — identical regardless of which API:
void OnRenderTimer()
{
    var points = session.DrainPoints();
    foreach (var pt in points)
    {
        var canvasPoint = DesktopToCanvasDips(pt.DesktopX, pt.DesktopY);
        brushEngine.ProcessPoint(canvasPoint, pt);
    }
}

// Runtime switching — no restart:
void OnApiChanged(InputApi newApi)
{
    session.Stop();
    session.Dispose();
    session = newApi == InputApi.WinUiPointer
        ? new WinUiPointerSession(myCanvas, hwnd)
        : PenSessionFactory.Create(newApi);
    session.Start(hwnd);
}
```

## Related Implementations

Existing frameworks that solve the same problem — abstracting over platform-specific tablet APIs to present a unified pen input interface. These are worth studying for design patterns, normalization choices, and lessons learned.

### Qt QTabletEvent

**Links:** [QTabletEvent docs](https://doc.qt.io/qt-6/qtabletevent.html) | [Tablet example](https://doc.qt.io/qt-6/qtwidgets-widgets-tablet-example.html)

Qt abstracts tablet input across Wintab (Windows), XInput (Linux), and Apple Pencil (macOS/iOS) behind a single `QTabletEvent` class. Key design choices:

**Full normalization — no raw values exposed:**

| Field | Range | Notes |
|---|---|---|
| `pressure()` | 0.0–1.0 | Hardware max scaled away entirely |
| `tangentialPressure()` | -1.0–1.0 | Airbrush finger wheel only |
| `xTilt()` / `yTilt()` | -60 to +60 degrees | Planar tilt, not Azimuth/Altitude |
| `rotation()` | 0–360 degrees | Art pen barrel rotation; 0 if unsupported |
| `z()` | device-dependent | 0 if unsupported |
| `pos()` / `globalPos()` | QPointF (float pixels) | Widget-local and screen coordinates |

This is a different normalization philosophy from our current approach. We keep raw pressure (0 to MaxPressure) and let the brush engine normalize. Qt normalizes at the input layer so consumers never see hardware-specific ranges.

**Two-axis device classification:**

Qt separates *what the hardware is* from *which end is active*:

- `DeviceType`: Stylus, Airbrush, Puck, TouchScreen, etc.
- `PointerType`: Pen, Eraser, Cursor, Finger

This avoids a combinatorial explosion. A stylus flipped to the eraser end is `DeviceType=Stylus, PointerType=Eraser`. An airbrush is `DeviceType=Airbrush, PointerType=Pen` — distinguished from a regular stylus by DeviceType, which tells the app that `tangentialPressure()` is available.

This is relevant to our open question about eraser detection — Qt makes it a PointerType, not a button state or cursor ID.

**Event-driven, not polling:**

Qt delivers `TabletPress`, `TabletMove`, `TabletRelease` events on the UI thread via the event loop. There is no polling API. Proximity events (`TabletEnterProximity` / `TabletLeaveProximity`) are delivered at the application level, not the widget level, because they happen before any widget has focus.

Qt acknowledges the high-frequency problem with `Qt::AA_CompressHighFrequencyEvents` — an opt-in application attribute that coalesces rapid tablet move events when the tablet reports faster than the app can render. This is essentially the framework doing the batching that our polling model handles naturally.

**Opaque platform abstraction:**

Qt does not expose which underlying API is being used. There is no runtime query for "am I getting Wintab or Windows Ink data?" This is deliberate — the consumer code is identical regardless of backend. On Windows, Qt uses Wintab when `wintab32.dll` is present.

**Lessons for our design:**

| Qt choice | Our current approach | Consideration |
|---|---|---|
| Normalize pressure to 0.0–1.0 | Keep raw pressure + MaxPressure | Qt's approach is simpler for consumers but loses precision. Our approach preserves full resolution for brush engines that want it. |
| Planar tilt only (xTilt/yTilt) | Both representations (Azimuth/Altitude + TiltX/TiltY) | Qt chose one convention; we provide both so consumers pick what suits their algorithm. All in tenths of degree for consistency. |
| DeviceType + PointerType enums | Cursor ID (13=pen, 14=eraser) | Qt's two-axis model is cleaner. Worth adopting for IPenSession. |
| Event-driven | Polling | Qt needs `CompressHighFrequencyEvents` to handle what polling gives us for free. Validates our polling choice. |
| Hide the underlying API | Expose via `InputApi` enum and `Source` field | Qt prioritizes portability. We want the app to offer an API dropdown, so we expose it. |
| Runtime capabilities query | Not yet implemented | Qt's `pointingDevice()->capabilities()` flags are a good pattern — maps to our proposed `PenCapabilities`. |

### Krita's API Switching via Qt

**Links:** [Krita Tablet Settings](https://docs.krita.org/en/reference_manual/preferences/tablet_settings.html) | [Krita's Qt patch](https://invent.kde.org/johnnynator/krita/-/blob/v4.4.3/3rdparty/ext_qt/0023-Implement-a-switch-for-tablet-API-on-Windows.patch) | [QGuiApplication platform options](https://doc.qt.io/qt-6/qguiapplication.html)

Krita is the most prominent real-world example of an app that lets users choose between Wintab and Windows Pointer input. Studying how they do it reveals important architectural constraints.

**How it works:**

Krita does **not** switch APIs at runtime. The switch requires an app restart. The flow is:

1. User selects "WinTab" or "Windows 8+ Pointer Input" in Krita's tablet settings
2. Preference is persisted to settings
3. User restarts Krita
4. On startup, *before constructing `QApplication`*, Krita injects Qt platform arguments:
   - Default: Qt uses WM_POINTER (modern Windows Ink path)
   - WinTab: Qt is launched with `-platform windows:nowmpointer`, which disables WM_POINTER handling and falls back to the legacy Wintab path
5. From that point, all tablet input flows through `QTabletEvent` regardless of which backend is active

```cpp
// Krita's startup pattern (simplified):
TabletApi api = loadPreference();  // from settings file

if (api == TabletApi::WinTab) {
    // Inject before QApplication construction
    args.push_back("-platform");
    args.push_back("windows:nowmpointer");
}

QApplication app(argc, argv);  // Backend decision is now locked in
```

**Why restart is required:**

Qt's Windows platform plugin makes its input-path decision during initialization. Once the native event plumbing is installed, the choice between Wintab and WM_POINTER cannot be changed. The backend is wired into the event loop, window message routing, and device enumeration at construction time.

This is a fundamental constraint of Qt's architecture, not a Krita limitation. Any Qt app faces the same restriction.

**Krita carried a custom Qt patch:**

For older Krita versions (4.x), the WinTab/Pointer switch wasn't just a command-line flag — Krita maintained a custom Qt patch titled "Implement a switch for tablet API on Windows." This patch added explicit support for choosing the tablet backend at startup. Later Qt versions (5.12+, 6.x) incorporated similar functionality natively via the `nowmpointer` platform option.

This tells us: even in a mature framework like Qt, getting API switching right was hard enough that a major app had to patch the framework itself.

**Driver dependency:**

The switch only works if the corresponding driver stack is present:
- **WinTab mode** requires a WinTab-capable driver (e.g., Wacom's `wintab32.dll`)
- **Windows Pointer mode** requires the tablet driver to support Windows Ink

Krita's docs explicitly note this. A session factory / API discovery mechanism must account for this — don't offer an API in the dropdown if the driver support isn't there.

**Lessons for our design:**

| Krita/Qt constraint | Impact on our design |
|---|---|
| API switch requires restart | Our `IPenSession` design avoids this — sessions are independent objects. Stopping one and starting another is a runtime operation, not a process-level decision. This is a significant advantage of our approach. |
| Backend choice is process-global in Qt | Our sessions are per-instance. Multiple session types could theoretically coexist (though Wintab/WM_POINTER coexistence has its own issues — see Open Question #5). |
| Driver must be present for the API to work | `PenSessionFactory.GetAvailableApis()` must probe for actual driver presence, not just OS version. Check for `wintab32.dll` on disk, test WM_POINTER capability, etc. |
| Qt hides the backend; Krita exposes it as a user setting | We're taking Krita's approach — expose the API choice to the user. But unlike Krita, we don't need a restart because we own the input layer directly. |
| Even Qt needed a patch to get this right | Validates that API switching is non-trivial. Our clean-room implementation has the advantage of designing for it from the start, rather than retrofitting it into an existing framework. |
| `QTabletEvent` is the same regardless of backend | Confirms the pattern: the consumer-facing event/data type (`QTabletEvent` / `PenPoint`) must be identical across all backends. The app code doesn't change when the API changes. |

**The key architectural difference:**

Qt's approach: the *framework* chooses the backend at process startup. The app influences the choice but can't change it later.

Our approach: the *app* creates session objects at runtime. Switching is `session.Stop(); session = factory.Create(newApi); session.Start();`. No restart. This is possible because we own the input layer directly rather than going through a framework's platform abstraction.

This runtime-switching capability is one of the strongest arguments for building our own unified session rather than depending on a framework's built-in tablet support.

## Relationship to Other Docs

- [Windows Pen API Landscape](https://github.com/TheSevenPens/devnotes) — Detailed comparison of all APIs (moved to devnotes repo)
- [FUTURES_NEXTGEN.md](FUTURES_NEXTGEN.md) — Phase plan and implementation history
