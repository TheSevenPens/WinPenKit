# Stylus Input Approach

How WinPenKit receives pen/stylus data on Windows, and how it handles switching between Wintab and Windows Pointer APIs.

## The Two Input Paths

On Windows, pen input reaches your application through one of two fundamentally different paths:

### Wintab

The Wintab API has been the standard for tablet input since 1991. It is provided by the tablet driver (e.g., Wacom's `Wintab32.dll`).

**How it works:**
1. WinPenKit creates a hidden Win32 window on a background thread
2. Opens a Wintab context via `WTOpenA`, requesting all packet data fields
3. The driver delivers `WT_PACKET` messages to that window at the device's report rate (180 Hz, measured on a Wacom DTH246)
4. WinPenKit's message pump calls `WTPacket` to retrieve each packet
5. Packet data is converted to a `PenPoint` and enqueued to a thread-safe buffer
6. The app polls `DrainPoints()` on its render timer

**Key characteristics:**
- Runs on a dedicated background thread — unaffected by UI thread load
- Provides all pen data: position, pressure, azimuth, altitude, twist, Z height, buttons, cursor type
- Two modes: System (screen pixels, mapped by the driver) and Digitizer (tablet units, which WinPenKit maps to desktop pixels with a fractional part)
- Works in every UI framework because it creates its own window
- **Desktop-global by nature** — delivers packets anywhere on screen, even when the pen is over another window. WinPenKit scopes this to the window the application passes to `Start`; with no window it stays desktop-wide (see [Spatial Scope](#spatial-scope-capture-region))

### Windows Pointer (WM_POINTER)

The modern Windows pen input path, introduced in Windows 8. Built into the OS — no third-party driver needed (though drivers enhance it).

**How it works (varies by framework):**

| Framework | How WinPenKit receives WM_POINTER data |
|---|---|
| **Raw Win32** | Subclasses the app's HWND via `SetWindowSubclass`. Intercepts `WM_POINTERUPDATE`/`DOWN`/`UP`. Calls `GetPointerPenInfo` for pen data. |
| **WinUI 3** | Attaches to XAML `PointerMoved`/`PointerPressed` events on a UIElement. WinUI routes input through its composition layer — WM_POINTER never reaches the HWND. |
| **WPF** | Attaches to `StylusMove`/`StylusDown` events. WPF routes pen input through its Wisp/RealTimeStylus stack. |
| **WinForms** | Uses `IMessageFilter` to intercept WM_POINTER at the application message pump level. `NativeWindow.AssignHandle` crashes on Form HWNDs. |
| **Avalonia** | Attaches to Avalonia's `PointerMoved`/`PointerPressed` events. |

**Key characteristics:**
- Runs on the UI thread — can be delayed by rendering load
- Provides pressure, tilt (X/Y), rotation, eraser flag, barrel button
- No Z height, no barrel pressure, no azimuth/altitude (only planar tilt)
- Events may be coalesced — use `GetPointerPenInfoHistory` to recover (see gotchas)
- Fixed pressure range (0–1024) vs Wintab's device-specific range
- **Window-scoped by nature** — only delivers points over the app window. The WPF, WinUI and Avalonia sessions are narrower, scoped to the attached control. The WinForms session's message filter sees every window in the process, so it filters to its window by default (see [Spatial Scope](#spatial-scope-capture-region))

## How Switching Works

WinPenKit treats each input path as an independent session object. Switching is a runtime operation:

```csharp
// Stop the current session.
session.Stop();
session.Dispose();

// Create a new one for a different API.
session = PenSessionFactory.Create(InputApi.WintabDigitizer);
session.Start(hwnd);
```

This works because:
- **Wintab sessions** create and destroy their own hidden window + background thread. No shared state.
- **WM_POINTER sessions** install and remove their hook (subclass, message filter, or event handler). Clean attach/detach.
- **PenPoint is the same struct** regardless of which backend produced it. The app's rendering code doesn't change.

### Why Other Apps Require a Restart

Qt-based apps like Krita require a restart because Qt's platform plugin makes the Wintab/WM_POINTER decision at process startup (the `-platform windows:nowmpointer` flag). Once the event plumbing is wired, it can't be changed.

WinPenKit avoids this because it owns the input layer directly rather than going through a framework's platform abstraction.

## The Normalization Contract

Every backend fills the same `PenPoint`: desktop position in physical pixels as `double`,
pressure from 0 to the session's `MaxPressure`, both tilt representations in degrees, twist,
Z height, buttons, cursor type and a microsecond timestamp. The fields that mean different
things on different backends (raw position units, button encoding, cursor numbering, clock) are
named by `IPenSession.Conventions`.

Where each value comes from differs by backend. Wintab reads `pkX/pkY` and either uses them as
screen pixels (system context) or maps tablet units onto the driver's screen rectangle
(digitizer). WM_POINTER reads `ptHimetricLocationRaw` and maps it through
`GetPointerDeviceRects`, falling back to `ptPixelLocationRaw` when the device rectangles are
unavailable. The framework sessions convert the framework's DIPs to pixels. Wintab reports
azimuth and altitude and computes planar tilt; the pointer backends report planar tilt and
compute azimuth and altitude. These conversions are lossy at extreme angles but accurate enough
for brush engines. Calligraphy brushes may prefer Azimuth, physics-based brushes may prefer
TiltX/TiltY.

The per-backend source fields, mapping formulas, tilt formulas and conventions are in
[ARCHITECTURE.md → Position](ARCHITECTURE.md#position) and
[ARCHITECTURE.md → Values](ARCHITECTURE.md#values).

## Spatial Scope (Capture Region)

Beyond data *fields*, the two input paths disagree on something more basic: **where on screen the pen must be** for the app to receive anything at all. Left unnormalized, this makes the same app behave differently depending on the active API — the pen "works" over the whole desktop on Wintab but only over the window (or a single control) on the pointer backends.

| Backend | Native spatial scope |
|---|---|
| Wintab System / Digitizer | **Entire desktop** — packets arrive even when the pen is over another window |
| WM_POINTER | The **app window** only |
| WinForms Pointer | **Every window in the process**: the message filter is application-wide |
| Framework pointer sessions (WinUI / WPF / Avalonia) | The **attached control** only |

WinPenKit can give every backend the same scope with `IPenSession.CaptureRegion`, a
screen-pixel filter applied before a point is queued. What it does when left `null` differs:
Wintab filters to the window passed to `Start` and is desktop-wide when no window was passed;
WM_POINTER and WinForms filter to their window; WPF, WinUI and Avalonia do not filter and receive
only their element's events. `PenCaptureRegion.Unbounded` gives desktop-wide capture on Wintab
only. The filter is a rectangle test with no occlusion check, and for Wintab it runs on the
background pump thread.

The exact defaults, the threading rules and the built-in regions are in
[ARCHITECTURE.md → Capture region](ARCHITECTURE.md#capture-region). How to set one is in
[HOW_TO_USE.md → Capture Region](HOW_TO_USE.md#capture-region-spatial-scope).

## When to Use Which

| Scenario | Recommended |
|---|---|
| Drawing app needing maximum precision | Wintab Digitizer (hi-res) |
| Drawing app, simple setup | Wintab System |
| App without Wintab driver | WM_POINTER / framework pointer events |
| User wants to switch at runtime | Offer all available APIs in a dropdown |
| Testing / debugging | Use WinPenKit.TestConsole for Wintab, Scribble apps for all |

## Driver Interaction

The tablet driver controls which APIs are available:

| Driver setting | Available APIs |
|---|---|
| Windows Ink **enabled** (default) | Wintab + WM_POINTER |
| Windows Ink **disabled** | Wintab only |

WinPenKit's factory (`GetAvailableApis`) probes for actual driver support rather than the OS version. The managed library reports Wintab when `WTInfoA(0, 0, NULL)` returns non-zero and WM_POINTER when `GetPointerType` exists. The native DLL reports Wintab when `Wintab32.dll` loads and WM_POINTER when `GetPointerPenInfo` exists.

See the [devnotes article on Wintab/Windows Ink coexistence](https://github.com/TheSevenPens/devnotes) for full details on driver configuration.
