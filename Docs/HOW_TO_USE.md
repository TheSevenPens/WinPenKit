# How to Use WinPenKit

A guide for developers building pen-enabled applications with the WinPenKit library. How the
library works internally is in [ARCHITECTURE.md](ARCHITECTURE.md).

## Quick Start (C#)

```csharp
using WinPenKit;

IPenSession? _session;

void StartPen(IntPtr hwnd)   // your main window's handle
{
    // 1. Discover available APIs. In a framework application, ask that framework's package
    //    instead -- see "Filling an API dropdown" below.
    var apis = PenSessionFactory.GetAvailableApis();

    // 2. Create a session and start it with your window. WM_POINTER needs the window to
    //    subclass. For Wintab it sets the default capture region: Start() with no window
    //    reports the pen anywhere on the desktop, including over other applications.
    _session = PenSessionFactory.Create(apis[0]);
    var error = _session.Start(hwnd);
    if (error != null)
    {
        Console.WriteLine($"Start failed: {error}");
        _session.Dispose();
        _session = null;
        return;
    }
}

// 3. Tell the session when your window is activated. Without this, Wintab loses the first
//    stroke after the user returns from another application. (WPF shown; see
//    "Window activation" below for other frameworks.)
Activated += (_, _) => _session?.OnActivated();

// 4. Poll on a render timer (~60 fps).
foreach (var pt in _session.DrainPoints())
{
    // pt.DesktopX/Y  — physical screen pixels (double)
    // pt.Pressure    — 0 to session.MaxPressure
    // pt.Azimuth/Altitude — spherical tilt (degrees)
    // pt.TiltX/TiltY — planar tilt (degrees)
}

// 5. Switch APIs at runtime — no restart needed.
_session.Stop();
_session.Dispose();
_session = PenSessionFactory.Create(InputApi.WintabDigitizer);
var switchError = _session.Start(hwnd);
```

## Quick Start (C++ / Rust)

```cpp
#include "pen_session.h"

// Discover and create.
PenInputApi apis[8];
int count = pen_session_get_available_apis(apis, 8);
const char* name = pen_session_get_api_label(apis[0]);   // "Wintab", for a dropdown
PenSessionHandle session = pen_session_create(apis[0]);

// Start with the application window. WM_POINTER requires it. For Wintab it sets the default
// capture region, and NULL reports the pen anywhere on the desktop.
const char* error = pen_session_start(session, app_hwnd);
if (error) {
    // show error
    pen_session_destroy(session);
    session = NULL;
}

// In the window procedure: tell the session when the window is activated.
case WM_ACTIVATE:
    if (LOWORD(wParam) != WA_INACTIVE && session)
        pen_session_on_activated(session);
    break;

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

All implement `IPenSession` — the polling code is identical regardless of backend. Start them
with the window handle like any other session: WinForms uses it for its default capture region,
and the WPF, WinUI and Avalonia sessions ignore it.

## Filling an API dropdown

`PenSessionFactory.GetAvailableApis()` answers for any application, which is why it cannot
answer for yours. It does not know your UI framework, and the framework settles two things it
has no view of: whether the framework's own API can be offered, and whether `WmPointer` can
reach you at all.

So each framework package answers for itself:

| Your application | Call | From package |
|---|---|---|
| WPF | `WpfPenApis.GetAvailable()` | `WinPenKit.Wpf` |
| WinForms | `WinFormsPenApis.GetAvailable()` | `WinPenKit.WinForms` |
| Avalonia | `AvaloniaPenApis.GetAvailable()` | `WinPenKit.Avalonia` |
| WinUI 3 | `WinUiPenApis.GetAvailable()` | `WinPenKit.WinUI` |

```csharp
foreach (var api in WpfPenApis.GetAvailable())
    ApiCombo.Items.Add(api.Label());
```

`WmPointer` is absent from all four lists. It subclasses the window procedure, and WPF,
WinForms, WinUI and Avalonia each consume pointer messages before a subclass sees them. The
API is present on the system and a raw Win32 process could use it; it simply cannot reach a
framework application. Offering it would put a dead entry in the dropdown.

`InputApi.Label()` gives the name to show — `"Wintab (high-res)"`, not `"WintabDigitizer"`.
The C ABI has the same thing in `pen_session_get_api_label`, so a native or Rust application
spells an API the same way a C# one does. `InputApi.IsFrameworkAgnostic()` is true for the
three APIs `PenSessionFactory.Create` accepts.

## Window activation

Call `session.OnActivated()` from your window's activation event, on every session. Wintab
delivers packets to the context at the top of the driver's overlap order, and when another
application takes focus your context moves down it and stays there. The first stroke after
returning to your window then produces no points; later strokes draw normally. `OnActivated`
moves the context back to the top (`WTEnable` and `WTOverlap`). The pointer and framework
sessions do nothing with it, so it is safe to call unconditionally.

| Framework | Call it from |
|---|---|
| WPF, WinForms, Avalonia | `Activated += (_, _) => _session?.OnActivated();` |
| WinUI 3 | `Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) _session?.OnActivated(); };` |
| Win32 / C ABI | `WM_ACTIVATE` when `LOWORD(wParam) != WA_INACTIVE`: `pen_session_on_activated(session)` |

If your code wraps an `IPenSession` in its own class, forward the call. `OnActivated` is a
default interface method, so a wrapper that does not forward it compiles and does nothing.

## Polling: DrainPoints and HasNewData

- `DrainPoints()` returns every queued point as a new array.
- `DrainPoints(Span<PenPoint> buffer)` copies up to `buffer.Length` points and returns the
  count. It allocates nothing. Points that do not fit stay queued, and `HasNewData` stays true
  so the next poll collects them.
- `HasNewData` is true when points have been queued since the last drain.

Both drains are thread-safe. On the Wintab sessions they also check, once a second, that the
driver still knows the context (next section). The C ABI's `pen_session_drain_points` and
`pen_session_has_new_data` behave like the span overload.

## Wintab context recovery

Restarting the tablet service invalidates every open Wintab context without telling the
applications that hold them. The managed Wintab sessions detect this inside `DrainPoints`, at
most once a second, and open a new context on their own. A failed reopen is retried every five
seconds. The log records each step.

**The check runs only while you drain.** An application that stops calling `DrainPoints` (for
example, one that stops its render timer while idle) does not recover until it drains again.
The native DLL does not recover at all: restart the session or the application. Details in
[ARCHITECTURE.md](ARCHITECTURE.md#context-keep-alive-and-reopen).

## PenPoint Fields

Every `PenPoint` contains:

| Field | Type | Description |
|---|---|---|
| `DesktopX/Y` | `double` | Physical screen pixels, with a fraction where the source has one. Whole pixels on Wintab system, and on Wintab (high-res) and WM_POINTER when they have fallen back (`HiRes` cleared). |
| `RawX/Y` | `int` | Device-native position, in units given by `session.Conventions.RawUnits`. Zero when that is `None`. See below. |
| `Pressure` | `uint` | Raw tip pressure. 0 = hovering. Normalize: `(float)pt.Pressure / session.MaxPressure`. That maximum is a **range, not a level count** — see below. |
| `Azimuth` | `double` | Spherical: compass direction in degrees (0.0–360.0). |
| `Altitude` | `double` | Spherical: angle from surface in degrees (0.0–90.0). 90 = perpendicular. |
| `TiltX` | `double` | Planar: tilt right/left in degrees (-90.0 to +90.0). |
| `TiltY` | `double` | Planar: tilt toward/away in degrees (-90.0 to +90.0). |
| `Twist` | `double` | Barrel rotation in degrees (0.0–360.0). 0 when the API reports none. Only the Wintab sessions set `PenCapabilities.Twist`; the others fill this field when the API provides it without setting the flag. |
| `Z` | `int` | Height above tablet surface. 0 unless the session advertises `ZHeight`. |
| `Status` | `uint` | Packet flags, carrying the proximity bit. 0 unless the session advertises `Proximity`, which only the Wintab backends do. |
| `Buttons` | `uint` | Button state, in one of two encodings named by `session.Conventions.Buttons`. Wintab: `(action << 16) \| buttonNumber`. Pointer backends: a flag bitmask, bit 0 barrel, bit 1 eraser. Read it through `PenButtonTracker`. |
| `Cursor` | `uint` | Cursor type, numbered as `session.Conventions.Cursor` says. Pointer backends normalise to 13 tip / 14 eraser; Wintab passes the driver's own number through. |
| `Source` | `InputApi` | Which backend produced this point. |
| `TimestampMicroseconds` | `long` | When the point was produced. **Subtract two of these; do not read one.** The clock is named by `session.Conventions.Timestamp`. See below. |

### What `MaxPressure` is, and is not

It is the largest value the device will report, and dividing by it gives correct relative
pressure. That is all it claims and all it does.

It is **not** a count of distinguishable levels. A Wacom DTH246 over Wintab reports 32767 and
resolves **8192**, in steps of 4. Measured twice, on different code paths: 99.8% of the gaps
between consecutive distinct pressures are multiples of 4 in
`testdata/wintab-digitizer-stroke-1.75x.csv`, taken through the managed Avalonia sample, and
99.7% in `testdata/winuinative-wintab-hires-stroke.csv`, taken through the native C ABI on a
1683-point stroke, where the smallest step between distinct pressures is exactly 4. Anyone
reasoning "32767 levels to work with" is wrong by a factor of 4 on that device, having read a
true sentence.

Nothing here reports granularity, because no driver declares it. Wintab's `AXIS` carries
`axUnits` and `axResolution`; for `DVC_NPRESSURE` the driver returns `TU_NONE` and `0`, while
populating both meaningfully for X and Y. Granularity can only be observed from a captured
stream.

Where the number comes from varies, and the number alone does not say which:

| backend | `MaxPressure` | what it is |
| --- | --- | --- |
| Wintab system, Wintab digitizer | queried | `WTInfoA(WTI_DEVICES, DVC_NPRESSURE).axMax` |
| WM_POINTER, WPF, WinUI, Avalonia, WinForms | 1024 | the API's fixed range, not the device's |

See issue 94.

### What `TimestampMicroseconds` is for

Differences. Two of them subtracted give elapsed microseconds, which is what sampling rate,
velocity and any time-based smoothing need. One on its own gives nothing: the origin is
unstated, every backend counts from somewhere different, and no two of them are comparable.

**How far the backends agree.** Two things hold everywhere, and three do not:

| | consistent? | |
| --- | --- | --- |
| unit | **yes** | microseconds on every backend, always |
| contract | **yes** | subtract two, get elapsed microseconds; never decreasing within a session |
| wrapping | **no** | 32-bit counters are extended in the session on Wintab, WPF and Avalonia; WM_POINTER does not need it; **WinUI is not extended** (see [TIMESTAMPS.md](TIMESTAMPS.md#counters-that-wrap)) |
| **resolution** | **no** | 1 µs on WM_POINTER and WinUI; 1 ms on Wintab, Avalonia and WPF; 15.6 ms on Qt |
| **one timestamp per point** | **no** | yes on WM_POINTER, WinForms, WinUI and Wintab; on WPF a batch shares one; on Avalonia the points recovered from one event share one; on Qt a coarse clock repeats one |

The last two rows reach your code. A velocity or smoothing routine tuned against WM_POINTER
will meet **zero deltas** on WPF and Qt, and not occasionally: drawn on by hand, 2442 WPF points
carried 885 distinct timestamps and 2280 Qt points carried 810 — about three points to a value,
the whole stroke through. Guard the division. A zero difference means two points the backend
could not separate in time, which is not a claim that no time passed.

### The exact shape of the value

| | |
| --- | --- |
| type | `long` (C# `Int64`, C `int64_t`, Rust `i64`) |
| unit | microseconds, on every backend, always |
| magnitude | microseconds since the machine booted — about 2.6 × 10¹² after 30 days up |
| may be negative? | **no.** The 32-bit counters are anchored to the 64-bit system tick count, which keeps the value positive. WPF's raw `int` turns negative after ~24.9 days of uptime; the session's value does not |
| overflow | never: `long.MaxValue` µs is about 292,000 years |
| origin | **unspecified.** Differences are the contract; absolute values are not |
| ordering | never decreasing within one session. A difference of zero is a normal reading |

### Per-backend: what it is made of

| backend | clock | source field | source type | conversion |
| --- | --- | --- | --- | --- |
| WM_POINTER, WinForms | `PerformanceCounter` | `POINTER_INFO.PerformanceCount` | `ulong` QPC ticks | `PenTimestamp.FromPerformanceCount`: `ticks × 10⁶ / QPF`, split to avoid overflow |
| WinUI 3 | `SystemTicks`¹ | `PointerPoint.Timestamp` | `ulong` µs | cast only, not anchored |
| Avalonia | `SystemTicks` | `PointerEventArgs.Timestamp` | `ulong` ms, filled from 32-bit `GetMessageTime` | `PenTimestamp.FromSystemTicks`: anchor, then `× 1000` |
| WPF | `SystemTicks` | `StylusEventArgs.Timestamp` | **`int`** ms | `PenTimestamp.FromSystemTicks`: anchor, then `× 1000` |
| Qt (Scribble.Qt) | `SystemTicks` | `QInputEvent::timestamp` | `quint64` ms | the same anchoring, in the sample, then `× 1000` |
| Wintab | `DeviceTicks` | `PACKET.pkTime` | **`uint`** ms | `PenTimestamp.FromSystemTicks`: anchor, then `× 1000` |

¹ `SystemTicks` names the epoch, which `PointerPoint.Timestamp` was measured to track, and not
the granularity — this clock resolves to the microsecond, well past the millisecond that
`GetTickCount64` itself advances in. It is the one backend where the enum's name is coarser
than the thing it names.

**What each conversion costs.**

- **`× 1000` (Avalonia, WPF, Qt, Wintab) is exact and adds nothing.** The last three digits are
  always `000`. Four of the six backends produce a number that looks microsecond-precise and
  carries milliseconds.
- **Do not try to infer resolution from the value.** An earlier draft of this page said a
  non-zero remainder mod 1000 meant WM_POINTER or WinUI. That is wrong twice over: WinUI's
  remainder is a per-run constant that cancels out of every difference, so the test flags a
  millisecond clock as fine; and WM_POINTER's measured values all ended in `000`, so it flags
  the finest clock available as coarse. Read `Conventions.Timestamp` and the table above.
- **The QPC division truncates below a microsecond.** Integer division toward zero, so the error
  is under 1 µs and slightly downward. At a 200 Hz report rate that is 0.02% of one interval.
- **The WinUI cast is lossless, and the source is finer than it first appeared.** Under
  synthetic injection every reading ended in the same sub-millisecond remainder, which reads as
  a millisecond clock with a fixed offset. On real hardware the readings are microsecond-
  resolved and the constant tail is gone; it belonged to the injector.
- **No backend loses anything to the wrap extension.** It only adds a multiple of 2³² ms.

### Rules

- Subtract two timestamps from the same session. Do not compare across sessions or backends.
- Expect a difference of zero and guard any division by one.
- Read `Conventions.Timestamp` for the clock. Where it is `None`, the field is zero; zero is not
  a time, and no session substitutes its own clock.
- To line points up with wall-clock time, calibrate yourself, take the **smallest**
  `wallClock - TimestampMicroseconds` seen over many points, and recalibrate after every
  backend switch. The code is in [TIMESTAMPS.md](TIMESTAMPS.md#reading-it-as-wall-clock-time).
- On WinUI, a session running across 49.7 days of uptime may see one backward step. This is not
  established either way.

`PenTimestamp` exposes the conversions for code that reads a clock itself:
`FromPerformanceCount(ulong)` for a QPC reading, `FromSystemTicks(long rawMs)` for a
millisecond value on the `GetTickCount64` epoch (anchored against `Environment.TickCount64`),
`FromSystemTicks(long rawMs, long nowMs)` against a reference you supply, and
`FromMilliseconds(long)` for a source already known to be 64 bits. `PenTimestamp.Wrap32` is
2³² ms.

How each figure here was measured, why synthetic injection could not measure it, and the
history of the wrap handling are in [TIMESTAMPS.md](TIMESTAMPS.md).

### What `RawX/Y` holds

Ask the session: `session.Conventions.RawUnits`.

| Backend | `RawUnits` | |
|---|---|---|
| Wintab digitizer, hi-res context open | `TabletNative` | the tablet's own space |
| Wintab digitizer, fallen back | `ScreenPixels` | |
| Wintab system | `ScreenPixels` | mapped by the driver |
| WM_POINTER, WinForms | `HundredthsOfMillimetre` | `ptHimetricLocationRaw` |
| WPF, WinUI, Avalonia | `None` | **no device-native value exists; the fields are zero** |

Read it as a diagnostic, not a position: sane raw values against a wrong `DesktopX` point at the mapping, and both wrong point upstream of it. `RawUnits.Label()` gives a short unit name for a readout ("tablet", "px", "0.01mm", or empty for `None`).

Those last three frameworks used to report `DesktopX` truncated to `int`. That is not a second measurement, it is the first one with its fraction removed, and it defeated the only reason to look at this field. They now report nothing and say so.

## Conventions

`PenPoint` has fields whose meaning depends on which backend produced them. `session.Conventions` says which convention is in force:

```csharp
var c = session.Conventions;
c.RawUnits   // what RawX/Y are measured in, or None
c.Buttons    // WintabEvent or PointerFlags
c.Cursor     // Normalised or DeviceAssigned
c.Timestamp  // which clock TimestampMicroseconds counts on, or None
```

`PenCapabilities` answers a different question -- *supported or not*, like `Proximity` and `ZHeight`. `Conventions` answers *which convention*. Asking one flag to answer both is how a hi-res capability once survived a fallback that had turned hi-res off.

Both tilt representations are always present — Wintab backends compute TiltX/TiltY from Azimuth/Altitude, and WM_POINTER backends compute Azimuth/Altitude from TiltX/TiltY.

## Coordinate Conversion

`PenPoint` provides desktop pixels as `double`. To convert to your canvas without losing the
fraction: get the canvas origin in desktop pixels from the framework, subtract it from
`DesktopX/Y` as `double`, and divide by the DPI scale if your canvas works in DIPs. The origin
can safely pass through an integer API, because a window's client origin is on a whole pixel.
The pen position must not.

| Framework | Canvas origin | Canvas point | Sample |
|---|---|---|---|
| **WinForms** (pixels) | `var o = canvas.PointToScreen(Point.Empty);` | `(pt.DesktopX - o.X, pt.DesktopY - o.Y)` | `Scribble.WinForms/MainForm.cs` |
| **WPF** (DIPs) | `var xf = WpfCoordinates.GetTransform(canvas);` (`OriginX/Y` in pixels, `ScaleX/Y` the DPI scale; null until the canvas is in a window) | `((pt.DesktopX - xf.OriginX) / xf.ScaleX, (pt.DesktopY - xf.OriginY) / xf.ScaleY)` | `Scribble.Wpf/MainWindow.xaml.cs` |
| **WinUI 3** (DIPs) | `ClientToScreen(hwnd, ref o)` with `o = (0, 0)`, `scale = monitorDpi / 96`, `pos = canvas.GetPositionInWindow()` | `((pt.DesktopX - o.X) / scale - pos.X, (pt.DesktopY - o.Y) / scale - pos.Y)` | `Scribble.WinUI/WintabSessionWinUI3.cs` |
| **Avalonia** (DIPs) | `var w = topLevel.PointToScreen(new Point(0, 0));`, `scale = topLevel.RenderScaling`, `var c = canvas.TranslatePoint(new Point(0, 0), topLevel)` | `((pt.DesktopX - w.X) / scale - c.X, (pt.DesktopY - w.Y) / scale - c.Y)`; multiply by `scale` for bitmap pixels | `Scribble.Avalonia/MainWindow.axaml.cs` |
| **Win32** (pixels) | `POINT o = {0, canvasTop}; ClientToScreen(hwnd, &o);` | `(pt.desktop_x - o.x, pt.desktop_y - o.y)` as `double` | `Scribble.Win32/src/main.cpp` |
| **egui (Rust)** (pixels) | `canvas_rect.min` in points | `pt.desktop_x - canvas_min.x * pixels_per_point` | `Scribble.Rust/src/main.rs` |

Do not pass the pen position through WinForms `PointToClient`, WPF `PointFromScreen` or
`PointToScreen`, Avalonia `PointToClient(PixelPoint)`, or Win32 `ScreenToClient`. Each takes or
returns an integer pixel position (WPF's do so internally, through Win32 `ClientToScreen` and `ScreenToClient`), so
every point lands on a whole pixel and the stroke is drawn as short straight steps between a few
directions. Measured through WPF's `PointFromScreen` on a 1.75x display, the mean turn between
segments went from 4.11 to 17.51 degrees, with every result on a whole pixel. `--replay` checks
for this: `L3.conversion-snap` fails when converted points land on the pixel grid
([SELF-TEST.md](SELF-TEST.md)).

The WinUI origin must be read under a per-monitor-v2 thread DPI context (see below). The
`Scribble.WinUI` sample's `PenSessionWinUI3` wrapper does this.

## DPI Handling

`PenPoint.DesktopX`/`DesktopY` are physical screen pixels on every backend. Wintab positions are used exactly as the driver reports them, taking **Wacom as the reference**: measured on a mixed-scaling desktop (monitors at different Windows scaling), Wacom's driver reports physical pixels in every configuration tried, with the tablet mapped to one display or to all of them. Other drivers may not. Huion's V20 driver was measured scaling positions by the ratio of the monitors' scalings, and WinPenKit deliberately does not correct for any one vendor's driver (issue #132). Check a driver and layout with the mapping wizard ([MAPPING-WIZARD.md](MAPPING-WIZARD.md)) or the mapping probe under [Diagnostics](#diagnostics).

Your app must be **Per-Monitor V2 DPI aware** for coordinates to match:

- **WinForms (.NET 10)**: Set `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` in the project, as `Scribble.WinForms` does. Client coordinates are then physical pixels, so subtract the canvas origin from `PointToScreen(Point.Empty)` and do not divide.
- **WPF (.NET 10)**: Declare `<dpiAwareness>PerMonitorV2</dpiAwareness>` in `app.manifest`, as `Scribble.Wpf` does. Without it the process is System DPI aware and, on a monitor whose DPI differs from the system DPI, strokes land away from the pen. Divide by the scale `WpfCoordinates.GetTransform` returns, which is the element's current `DpiScale`.
- **WinUI 3**: Call `ClientToScreen` inside a `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` block, and divide by the window's monitor DPI / 96.
- **Avalonia**: Per-monitor aware through the framework. Divide by `TopLevel.RenderScaling`, read at conversion time so a move to another monitor is followed.
- **Win32 C++**: Call `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` before creating windows. Client coordinates are then physical pixels.
- **WinUI 3 unpackaged**: Add `<dpiAwareness>PerMonitorV2</dpiAwareness>` to `app.manifest` or the UI is blurry.

See the [devnotes DPI article](https://github.com/TheSevenPens/devnotes) for the full deep-dive.

## Wintab high-res mapping

The Wintab (high-res) session, `WintabDigitizer`, receives positions in tablet units and maps
them onto the screen rectangle the driver reports for its default system context (`lcSys`). It
reads that rectangle once, when the session starts. If the display configuration changes while
the session runs (a monitor added or removed, a resolution or scaling change, the tablet
remapped in the driver's settings), call:

```csharp
session.RefreshMapping();
```

It re-reads the driver's rectangle without reopening the context. On every other backend it does
nothing. Listening for the change (`WM_DISPLAYCHANGE`, `WM_DPICHANGED`, or the framework's
equivalent) is the application's job.

The mapping is only as correct as the driver's rectangle. On a desktop whose monitors use
different Windows scaling, it is correct when the driver reports physical pixels: Wacom's does,
Huion's V20 does not, and WinPenKit applies no correction. If the hi-res context cannot be
opened, the session falls back to screen pixels and clears `PenCapabilities.HiRes`. Details in
[ARCHITECTURE.md](ARCHITECTURE.md#wintab-digitizer-high-res).

## Capture Region (Spatial Scope)

The input paths natively disagree on **where the pen has to be** for your app to get data: Wintab is desktop-global, WM_POINTER is window-scoped, and the framework pointer sessions are control-scoped. `IPenSession.CaptureRegion` can make one app behave the same on every backend. (For *why* the backends differ, see [STYLUS.md](STYLUS.md#spatial-scope-capture-region).)

What the default (`CaptureRegion == null`) does depends on the backend:

| Backend | Default scope |
|---|---|
| Wintab | The window passed to `Start(hwnd)`. **`Start()` with no window is unbounded**: the session reports the pen anywhere on the desktop, including over other applications. |
| WM_POINTER | The window passed to `Start(hwnd)`, which it requires. |
| WinForms | The window passed to `Start(hwnd)`, else the control passed to the constructor. |
| WPF, WinUI, Avalonia | No filter. The session receives only the events its element receives. |

```csharp
// Default: pass your window to Start and Wintab is scoped to it.
session.Start(appHwnd);

// Scope to a fixed screen rectangle.
session.CaptureRegion = PenCaptureRegion.Rect(x, y, width, height);

// Scope to a window's live bounds (tracks moves/resizes).
session.CaptureRegion = PenCaptureRegion.Window(appHwnd);

// Opt back in to desktop-wide capture (Wintab only — see below).
session.CaptureRegion = PenCaptureRegion.Unbounded;
```

`CaptureRegion` may be set before or after `Start()`; it takes effect on the next point. `Window` uses the window's outer bounds, including its frame, and accepts every point when the handle is zero.

The native C ABI has a capture region on Wintab sessions only: `pen_session_set_capture_window`, `pen_session_set_capture_rect` and `pen_session_set_capture_unbounded`. The default is the window passed to `pen_session_start`, or unbounded when that is NULL. The native WM_POINTER session has no capture region.

### Scope to a control (Avalonia)

`WinPenKit.Avalonia` ships `ControlCaptureRegion`, which tracks an Avalonia control's live on-screen bounds — so Wintab (desktop-global by default) is constrained to the same canvas the pointer backends already see:

```csharp
var region = new ControlCaptureRegion(canvasControl);
session.CaptureRegion = region;
// ...
session.Stop();
region.Dispose();   // unsubscribes from layout / window-move events
```

It is DPI-correct (corners projected through `PointToScreen`) and thread-safe: the screen rectangle is cached on the UI thread and `Contains()` only reads the cache, so it is safe to evaluate from Wintab's background capture thread. Construct and dispose it on the UI thread.

### Desktop-wide capture

Only backends advertising `PenCapabilities.GlobalCapture` (Wintab System and Digitizer) can deliver points outside the app window. Probe before relying on it:

```csharp
if (session.Capabilities.HasFlag(PenCapabilities.GlobalCapture))
    session.CaptureRegion = PenCaptureRegion.Unbounded;
```

On other backends `Unbounded` is harmless but inert — the OS still limits them to their window or control.

> **v1 limitation:** the region is a screen rectangle with no occlusion test — a point passes if it falls within the bounds even when another window is on top.

## Buttons and Eraser

### Use `PenButtonTracker` (recommended)

`PenPoint.Buttons` carries different encodings depending on the backend:

- **Wintab**: one event per packet, `(action << 16) | buttonNumber`. Action: 0=none, 1=released, 2=pressed. Button: 0=tip, 1-3=barrel (`PenButtonNumber.Tip`, `Barrel1` to `Barrel3`).
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

`PenPoint` exposes `ButtonAction`, `ButtonNumber`, `IsTipPressed`, `IsButtonPressed(int)` and `IsButtonReleased(int)`. **All five are obsolete.** They apply the Wintab encoding to any point, and the five pointer backends set only bits 0 and 1, so on those backends `ButtonAction` is always `None` and the three predicates are always `false` — false, rather than any indication that the question could not be answered.

Use `PenButtonTracker`. It branches on `pt.Source` and decodes both encodings. If you must read `pt.Buttons` yourself, check `pt.Source` first and decode accordingly.

### Eraser detection

Eraser is detected via `pt.IsEraser`, which checks `pt.Cursor == 14` (`PenCursorType.Eraser`; `PenCursorType.PenTip` is 13). In Wintab, cursor type changes on hover before contact. The pointer backends read `PEN_FLAG_INVERTED` and write 13 or 14 to match. `PenButtonTracker.IsEraser` mirrors this from the latest point.

**The match is one-way.** Wintab writes the driver's own cursor number through unchanged, and Wintab cursor indices are assigned by the device — `PenCursorType` documents 13 and 14 as *observed* values, not standard ones. On a tablet that numbers its eraser differently, `IsEraser` is false on Wintab while true on every pointer backend. Tracked in issue 48.

## Error Handling

`Start()` returns `null` on success, or an error string on failure. Always check:

```csharp
var error = session.Start(hwnd);
if (error != null)
{
    // "Wintab not found. Is the tablet driver installed?"
    // "WM_POINTER requires an application window handle."
    // "Failed to open system context."
    ShowError(error);
    return;
}
```

Always call `Dispose()` when done. It stops the session. On the Wintab sessions it also flushes the diagnostic log; the log file stays open until the process exits, so a later session in the same process appends to it.

## Diagnostics

### The log

The managed Wintab sessions log to `%TEMP%\WinPenKit.<pid>.log`, one file per process.
`WintabDiagnostics.LogPath` returns the path, for an application that wants to attach it to a
bug report. Logs older than a week are deleted when a new one is created. The log records:

- Context configuration before and after open
- Hi-res fallback events
- The driver's context counters before opening, after opening and after closing (a log with no
  "after closing" line is from a process that was killed; see
  [WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md#what-winpenkit-writes-to-its-log))
- Loss of the context and each reopen attempt
- `OnActivated` failures
- Button/cursor transitions
- Packet processing errors

The native DLL logs to `%TEMP%\WintabSessionCpp.log` (`pen_session_get_log_path()`). That name is
shared by every process using the DLL, the file is truncated by each process's first write, and
it carries no context counters.

### Packet counts

The managed Wintab sessions count packets where they arrive. Ask with a type test, because other
sessions do not implement it, and treat its absence as "this backend cannot say", not as zero:

```csharp
if (session is WinPenKit.Diagnostics.IPacketCounts counts)
{
    long fromDriver = counts.PacketsFromDriver;            // before any filtering
    long dropped    = counts.PacketsOutsideCaptureRegion;  // discarded by the region
    long delivered  = counts.PointsDelivered;              // queued for DrainPoints
}
```

A device that stopped reporting shows `PacketsFromDriver` not increasing. A session discarding
points shows `PacketsOutsideCaptureRegion` increasing.

### Asking the driver

`WintabDiagnostics` reads from the driver without opening a context:

- `ContextTable()`: the open and stated-maximum context counts as a `WintabContextTable`, or
  null. `AboveStatedMaximum` is true when more are open than the driver says it supports. It is
  not a capacity check: opens have succeeded far past the stated maximum.
- `DeviceName()`: the tablet's name, or null.
- `DriverScreen()`: the default system context's screen and input ranges, as text.

### Is the pen landing under the cursor?

`WintabMappingProbe` compares every Wintab position with the cursor, which the driver moves itself and Windows places correctly, on each monitor and in each Wintab mode:

```
WinPenKit.TestConsole --probe-wintab-mapping [seconds-per-mode] [samples.csv]
WinPenKit.TestConsole --report-wintab-mapping samples.csv
```

Hover the pen (don't tap) over every monitor, corners included, in each mode; the tablet must be in pen mode. The report gives, per mode and monitor, the mean error in pixels (judged: at most 3 px per axis), the driver's actual mapping from raw values to the cursor, and how much of the monitor was covered. Samples are saved to CSV so a run can be re-reported later on the same layout. `testdata/wintab-mapping-probe-mixed-dpi.csv` is the run that found the scaling problem, on Huion's V20 driver; it fails on all four mode and monitor pairs.

Run it after any change to the Wintab coordinate path, and on any new monitor layout, scaling, resolution or tablet mapping. [MAPPING-WIZARD.md](MAPPING-WIZARD.md) walks through a full plan of layouts and also checks WM_POINTER.

### Self-test, replay and recording

`WinPenKit.Diagnostics.SelfTest` and `SelfTestReplay` implement the `--selftest` and `--replay`
checks every Scribble app runs, and `StrokeRecorder` and `StrokeReplay` write and read the
`--record` format. `PresentationProbe` measures how the drawing surface reaches the screen.
`WindowPlacement.ClampToWorkArea(hwnd)` keeps a window inside its monitor's work area, since pen
input over the part of a window off screen is never delivered. See [SELF-TEST.md](SELF-TEST.md).

### TestConsole flags

`WinPenKit.TestConsole` with no flags lists the available APIs, starts the one you choose, and
prints the latest point ten times a second. Its flags:

| Flag | What it does | Needs a tablet |
|---|---|---|
| `--selftest-clock` | Fifteen checks of the timestamp conversions, including both wraps and the epoch analysis | no |
| `--verify-wintab-anchoring <file>` | Replays recorded `pkTime`/tick pairs (for example `testdata/wintab-epoch-probe.csv`) through the anchoring | no |
| `--probe-wintab-epoch [seconds] [x y]` | Reads raw `pkTime` against the system tick count to establish its epoch; `x y` places the window | yes |
| `--probe-wintab-mapping [seconds-per-mode] [samples.csv]` | The mapping probe above | yes |
| `--report-wintab-mapping <samples.csv>` | Reports on saved mapping samples | no |

```bash
dotnet run --project WinPenKit.TestConsole -- --selftest-clock
```

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

### 13. The native DLL does not reopen a lost Wintab context

After a tablet service restart, a native Wintab session stops receiving packets and reports nothing. Stop and start the session again. The managed library reopens on its own ([Wintab context recovery](#wintab-context-recovery)).
