# Stylus Input Approach

How WinPenKit receives pen/stylus data on Windows, and how it handles switching between Wintab and Windows Pointer APIs.

## The Two Input Paths

On Windows, pen input reaches your application through one of two fundamentally different paths:

### Wintab

The Wintab API has been the standard for tablet input since 1991. It is provided by the tablet driver (e.g., Wacom's `Wintab32.dll`).

**How it works:**
1. WinPenKit creates a hidden Win32 window on a background thread
2. Opens a Wintab context via `WTOpenA`, requesting all packet data fields
3. The Wacom driver delivers `WT_PACKET` messages to that window at 200+ Hz
4. WinPenKit's message pump calls `WTPacket` to retrieve each packet
5. Packet data is converted to a `PenPoint` and enqueued to a thread-safe buffer
6. The app polls `DrainPoints()` on its render timer

**Key characteristics:**
- Runs on a dedicated background thread — unaffected by UI thread load
- Provides all pen data: position, pressure, azimuth, altitude, twist, Z height, buttons, cursor type
- Two modes: System (screen pixels) and Digitizer (tablet-native hi-res ~5080 LPI)
- Works in every UI framework because it creates its own window
- **Desktop-global by nature** — delivers packets anywhere on screen, even when the pen is over another window. WinPenKit scopes this to the app window by default (see [Spatial Scope](#spatial-scope-capture-region))

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
- **Window-scoped by nature** — only delivers points over the app window; the framework pointer sessions (WinUI, WPF, WinForms, Avalonia) are narrower still, scoped to the attached control (see [Spatial Scope](#spatial-scope-capture-region))

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

Regardless of which backend is active, every `PenPoint` provides:

| Field | Wintab source | WM_POINTER source | Result |
|---|---|---|---|
| `DesktopX/Y` | Screen pixels or ScaleAxis-converted | `ptPixelLocationRaw` | Physical desktop pixels (double) |
| `Pressure` | 0 to device max | 0 to 1024 | Raw, normalize via `MaxPressure` |
| `Azimuth` | Native (÷10) | Computed from TiltX/TiltY | Degrees (0.0–360.0) |
| `Altitude` | Native (÷10) | Computed from TiltX/TiltY | Degrees (0.0–90.0) |
| `TiltX/TiltY` | Computed from Azimuth/Altitude | Native | Degrees (-90.0 to +90.0) |
| `Twist` | Native (÷10) | Native | Degrees (0.0–360.0) |
| `Z` | Native | Not available (0) | Height above surface |
| `Cursor` | 13=pen, 14=eraser | Mapped from PEN_FLAG_INVERTED | Consistent cursor type |

Both tilt representations are always present. Consumers use whichever suits their algorithm.

### Tilt Conversion Formulas

Each backend computes whichever tilt representation it doesn't have natively. All values are in degrees.

**WM_POINTER → Spherical** (TiltX/TiltY native, compute Azimuth/Altitude):
```
tiltMag  = sqrt(tiltX^2 + tiltY^2)         // degrees from vertical
Altitude = 90 - tiltMag
Azimuth  = atan2(-tiltX, tiltY) * 180/PI  (mod 360)
```

**Wintab → Planar** (Azimuth/Altitude native, compute TiltX/TiltY):
```
tiltMag = 90 - Altitude                    // degrees from vertical
TiltX   = -tiltMag * sin(Azimuth * PI/180)
TiltY   =  tiltMag * cos(Azimuth * PI/180)
```

These conversions are lossy at extreme angles but accurate enough for brush engines. Calligraphy brushes may prefer Azimuth, physics-based brushes may prefer TiltX/TiltY.

## Spatial Scope (Capture Region)

Beyond data *fields*, the two input paths disagree on something more basic: **where on screen the pen must be** for the app to receive anything at all. Left unnormalized, this makes the same app behave differently depending on the active API — the pen "works" over the whole desktop on Wintab but only over the window (or a single control) on the pointer backends.

| Backend | Native spatial scope |
|---|---|
| Wintab System / Digitizer | **Entire desktop** — packets arrive even when the pen is over another window |
| WM_POINTER | The **app window** only |
| Framework pointer sessions (WinUI / WPF / WinForms / Avalonia) | The **attached control** only |

WinPenKit normalizes this with `IPenSession.CaptureRegion` — a screen-pixel filter every backend applies before a point is queued:

```csharp
public interface IPenCaptureRegion
{
    bool Contains(double desktopX, double desktopY);   // physical screen pixels
}

public static class PenCaptureRegion
{
    IPenCaptureRegion Unbounded { get; }                 // accept every point
    IPenCaptureRegion Window(IntPtr hwnd);               // a window's live GetWindowRect bounds
    IPenCaptureRegion Rect(double x, y, w, h);           // a fixed screen rectangle
}
```

- **`null` (the default) → window-scoped.** Wintab now records the app-window handle passed to `Start` and filters to it, so its default matches the pointer backends instead of spilling across the desktop. Pointer sessions are already control-scoped, so `null` leaves their natural scope unchanged.
- **Set a region** to scope every backend identically — e.g. to one canvas control, so Wintab and the pointer backends deliver data over exactly the same area.
- **`PenCaptureRegion.Unbounded`** opts back in to desktop-wide capture — honored only by backends advertising `PenCapabilities.GlobalCapture` (Wintab System + Digitizer). On the others it is harmless but inert: the OS still confines them to their window/control.

**Threading.** `Contains` runs on whichever thread produces points — for Wintab that is the background message-pump thread, *not* the UI thread. Implementations must be thread-safe and must not touch UI objects directly: cache any UI-derived bounds on the UI thread and read the cached value here. When bounds aren't known yet, fail **open** (return `true`) rather than dropping all input. `WinPenKit.Avalonia.ControlCaptureRegion` is the canonical implementation — it recomputes a control's screen rectangle on the UI thread (on layout updates and window moves), projects both corners through `PointToScreen` for DPI-correctness, and `Contains` only reads the cached rectangle.

> **v1 limitation:** filtering is by screen **rectangle** only — a point inside the region's bounds passes even if another window sits on top of it (no occlusion / z-order test).

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

WinPenKit's factory (`GetAvailableApis`) probes for actual driver support — it checks if `Wintab32.dll` loads and if `GetPointerPenInfo` exists, not just OS version.

See the [devnotes article on Wintab/Windows Ink coexistence](https://github.com/TheSevenPens/devnotes) for full details on driver configuration.
