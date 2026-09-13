# How to Use WinPenKit

A guide for developers building pen-enabled applications with the WinPenKit library.

## Quick Start (C#)

```csharp
using WinPenKit;

// 1. Discover available APIs. In a framework application, ask that framework's package
//    instead -- see "Filling an API dropdown" below.
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
const char* name = pen_session_get_api_label(apis[0]);   // "Wintab", for a dropdown
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
spells an API the same way a C# one does.

## PenPoint Fields

Every `PenPoint` contains:

| Field | Type | Description |
|---|---|---|
| `DesktopX/Y` | `double` | Physical screen pixels. Sub-pixel precision in digitizer mode. |
| `RawX/Y` | `int` | Device-native position, in units given by `session.Conventions.RawUnits`. Zero when that is `None`. See below. |
| `Pressure` | `uint` | Raw tip pressure. 0 = hovering. Normalize: `(float)pt.Pressure / session.MaxPressure`. That maximum is a **range, not a level count** — see below. |
| `Azimuth` | `double` | Spherical: compass direction in degrees (0.0–360.0). |
| `Altitude` | `double` | Spherical: angle from surface in degrees (0.0–90.0). 90 = perpendicular. |
| `TiltX` | `double` | Planar: tilt right/left in degrees (-90.0 to +90.0). |
| `TiltY` | `double` | Planar: tilt toward/away in degrees (-90.0 to +90.0). |
| `Twist` | `double` | Barrel rotation in degrees (0.0–360.0). |
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

**How far the backends agree.** Three things hold everywhere, and one does not:

| | consistent? | |
| --- | --- | --- |
| unit | **yes** | microseconds on every backend, always |
| contract | **yes** | subtract two, get elapsed microseconds; never decreasing within a session |
| wrapping | **yes** | handled in the session, not left to the caller — see below |
| **resolution** | **no** | 1 µs on WM_POINTER and WinUI; 1 ms on Wintab, Avalonia and WPF; 15.6 ms on Qt |
| **one timestamp per point** | **no** | yes on four backends; on WPF a batch shares one, on Qt a coarse clock repeats one |

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
| may be negative? | **yes, on WPF only**, if the session starts after ~24.9 days of uptime |
| overflow | never: `long.MaxValue` µs is about 292,000 years |
| origin | **unspecified.** Differences are the contract; absolute values are not |
| ordering | never decreasing within one session. A difference of zero is a normal reading |

### Per-backend: what it is made of

| backend | clock | source field | source type | conversion |
| --- | --- | --- | --- | --- |
| WM_POINTER, WinForms | `PerformanceCounter` | `POINTER_INFO.PerformanceCount` | `ulong` QPC ticks | `ticks × 10⁶ / QPF`, split to avoid overflow |
| WinUI 3 | `SystemTicks`¹ | `PointerPoint.Timestamp` | `ulong` µs | cast only |
| Avalonia | `SystemTicks` | `PointerEventArgs.Timestamp` | `ulong` ms | `× 1000` |
| WPF | `SystemTicks` | `StylusEventArgs.Timestamp` | **`int`** ms | wrap-extend, then `× 1000` |
| Qt (Scribble.Qt) | `SystemTicks` | `QInputEvent::timestamp` | `quint64` ms | `× 1000` |
| Wintab | `DeviceTicks` | `PACKET.pkTime` | **`uint`** ms | wrap-extend, then `× 1000` |

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
  the finest clock available as coarse. Read `Conventions.Timestamp` and the table below.
- **The QPC division truncates below a microsecond.** Integer division toward zero, so the error
  is under 1 µs and slightly downward. At a 200 Hz report rate that is 0.02% of one interval.
- **The WinUI cast is lossless, and the source is finer than it first appeared.** Under
  synthetic injection every reading ended in the same sub-millisecond remainder, which reads as
  a millisecond clock with a fixed offset. On real hardware the readings are microsecond-
  resolved and the constant tail is gone; it belonged to the injector.
- **No backend loses anything to the wrap extension.** It only adds a multiple of 2³² ms.

### Measured resolution, and why injection could not measure it

**Every backend in this table has now been drawn on by hand**, on a Wacom DTH246, 13 Sep 2026.
No injected figures remain.

| backend | points | distinct timestamps | step | one stamp per point? |
| --- | --- | --- | --- | --- |
| **WM_POINTER (WinForms)** | 2070 | 2070 | **1 µs** | yes |
| **WinUI 3** | 1878 | 1878 | **1 µs** | yes |
| **Avalonia** | 2167 | 2167 | 1 ms | yes |
| **Wintab (high-res)** | 1683 | 1683 | 1 ms | yes |
| **WPF Stylus** | 2442 | 885 | 1 ms clock, 15.6 ms batches | **no** — ~3 points share one |
| **Qt (`Scribble.Qt`, not WinPenKit)** | 2280 | 810 | **15.6 ms** | **no** — coarse clock repeats |

Five of these six had an injected figure to compare against; Wintab never did, because it
ignores injected input entirely. **Four of those five were wrong.** Only Qt's survived. That is
the headline finding of this whole exercise, and it is a fact about the instrument rather than
about any backend: measuring a clock through `InjectSyntheticPointerInput` mostly produces the
injector's properties.

Avalonia was the last measured and corrected its figure in a different direction from the rest.
Injection gave 172 points carrying 113 distinct timestamps, which reads as a clock too coarse to
separate consecutive points. On hardware there are **no repeats at all** — 2167 points, 2167
timestamps, 2166 gaps and not one of them zero. Its 1 ms resolution is real, but that comes from
the source type rather than from the recording: `PointerEventArgs.Timestamp` is a `ulong` count
of milliseconds. Recorded in `testdata/avalonia-hardware-stroke.csv`.

The Wintab row is the only one measured on real hardware — a Wacom DTH246 over the hi-res
digitizer context, 13 Sep 2026 — and it is the best of the set by a wide margin. **Every one of
1683 points carried its own timestamp**, with no repeats and no backward steps, where WPF gave
6 distinct values for 196 points. Gaps were 5 ms or 6 ms and nothing else, their greatest
common divisor exactly 1000 µs, averaging 5555 µs: a **180 Hz** device reported on a
millisecond clock, which is why it alternates rather than landing on 5.556 every time.

It is also the one row synthetic injection did not shape, because Wintab ignores injected input
entirely. Recorded in `testdata/winuinative-wintab-hires-stroke.csv`.

Qt is in the table because `Scribble.Qt` exists to be compared against, not because WinPenKit
produces it. It is the one backend whose injected figure survived contact with hardware: across
809 gaps the **smallest is 15 ms**, with 504 of 16 ms and 303 of 15 ms. Nothing finer occurs at
all. `QInputEvent::timestamp` is the coarsest clock in the table, which is worth knowing before
treating Qt as the reference implementation.

**A greatest common divisor is evidence of resolution only when the smallest gap is near it.**
The Qt recording has a gcd of 1000 µs and no gap under 15 ms, because `gcd(15000, 16000)` is
1000: alternating between the two ticks of a 15.625 ms timer produces that number
arithmetically, out of nothing. On the WM_POINTER and WinUI recordings the same statistic meant
something, because gaps that small genuinely occurred. Quote the minimum alongside the gcd, or
the statistic will manufacture a resolution the clock does not have.

**Injection was setting the floor it appeared to measure, and this is the proof.**

Under injection, WM_POINTER's `PerformanceCount` arrived as exact millisecond multiples and
matched `dwTime` one for one; this page recorded 1 ms and warned the figure was an upper bound.
Drawn on by hand, the greatest common divisor of all 2069 gaps is **1 µs**, every one of 2070
points carries a distinct timestamp, and consecutive gaps read 5001, 4943, 4999, 4946. It is
the finest clock of any backend here.

WinUI told the same story. Under injection every reading ended in the same sub-millisecond
remainder — 171 µs in one run, 622 µs in another — which is exactly what a millisecond clock
with a fixed offset looks like. On hardware the gcd is **1 µs** across 1877 gaps with 1878
distinct timestamps. The constant tail was the injector's, not WinUI's.

The general lesson is worth more than either number: `InjectSyntheticPointerInput` stamps its
own events, so a backend cannot be shown to resolve finer than the thing feeding it. A
measurement taken through it can only ever bound a clock from above. Recorded in
`testdata/wmpointer-hardware-stroke.csv` and `testdata/winui-hardware-stroke.csv`.

One thing all six hardware recordings agree on, and the reason to trust them: a **180 Hz**
device. The four per-point backends read it directly, as gaps averaging 5559–5560 µs. WPF and Qt
cannot — their timestamps step by the timer tick — but dividing points by elapsed span gives
180.1 Hz and 179.9 Hz. Six unrelated code paths: the native C ABI, WinForms, WinUI, Avalonia,
WPF, and Qt's own stack.

### WPF is different in kind, not degree — and its clock was never the problem

WPF's `StylusEventArgs` carries a whole `StylusPointCollection`, and the timestamp belongs to
the **event**, not the point. Every point in a batch gets the same one. Drawn on by hand: 2442
points, **885 distinct timestamps**, two to four points per value and three most of the time.

The hardware recording separates two things this page used to run together. **The clock is a
millisecond clock.** Sixteen gaps of exactly 1000 µs appear, spread through the stroke rather
than bunched at its start, so `StylusEventArgs.Timestamp` does express a millisecond when it is
given the chance. What steps by 15.6 ms is the **delivery** — 531 gaps of 16 ms and 329 of 15 ms,
which is the Windows timer tick, not a property of the clock. The old 15.6 ms figure described
the batch cadence and was attributed to the clock.

That distinction matters because it says which effect a better clock would remove: none of it.
The batching is the whole of what reaches a caller, and WPF exposes no per-point time at all, so
this is a ceiling of the framework rather than a choice made here.

Qt reaches a similar-looking number — 2280 points, 810 timestamps — by the opposite route, and
the two should not be run together. `QTabletEvent` is a `QSinglePointEvent`, so those 2280
points are 2280 separate events, each with its own timestamp. They repeat because the *clock*
only advances on the 15.6 ms timer tick. WPF has a fine clock and coarse delivery; Qt has fine
delivery and a coarse clock. A finer clock would fix Qt and would do nothing for WPF.

Recorded in `testdata/wpf-hardware-stroke.csv` and `testdata/qt-hardware-stroke.csv`.

Avalonia sits with the per-point group rather than with these two, and the intermediate-point
recovery added in #110 did not change that on the run measured: `GetIntermediatePoints` returned
a single point every time, so nothing was coalesced and nothing shared a timestamp. That path
produces several points per timestamp when the application falls behind, not as a rule.

### Two more things worth stating

- **`dwTime` and `PerformanceCount` are not two readings of one clock.** Both are populated on
  every `POINTER_INFO`. `dwTime` is milliseconds on the `GetTickCount64` epoch, `PerformanceCount`
  is QPC, and they sat 27.08 ms apart — identically — across every sample. These backends use
  `PerformanceCount`.
- **Sampling rate is now established; latency still is not.** While every figure here came from
  injection, the gaps were the injection script's and said nothing about a device. The six
  hardware recordings do measure the device: 180 Hz, agreed on by all six. They still say nothing
  about latency, which is the delay between the pen touching glass and the point reaching your
  handler, and no recording of timestamps alone can measure it.

### Reading it as wall-clock time

You cannot, through the API. The origin is unspecified on purpose, because it differs per
backend and only one machine has been measured.

If you need wall clock anyway — lining a stroke up against a log, say — calibrate it yourself.
At the moment a point arrives, read the wall clock too:

```csharp
long offsetUs = long.MaxValue;   // keep the smallest seen

// in your point handler, per point:
long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
offsetUs = Math.Min(offsetUs, nowUs - pt.TimestampMicroseconds);

// then, for any point:
DateTimeOffset wall = DateTimeOffset.FromUnixTimeMilliseconds(
    (pt.TimestampMicroseconds + offsetUs) / 1000);
```

The **minimum** matters. An event happens at T and your handler runs at T + latency, so every
sample overestimates the offset by that run's latency and never underestimates it. The smallest
difference over many points is the closest you get to the true offset. Measured handler latency
in one run was 0.372 ms to 26.6 ms, the largest on the first point after startup — calibrating
on a single sample would have been 26 ms out.

Recalibrate per session. The offset is not valid across a backend switch. On WPF the clock
being read is a millisecond clock, so the calibration is bounded by that rather than by the
15.6 ms delivery cadence; on Qt the 15.6 ms *is* the clock, and no calibration beats it.


Wintab's `pkTime` is requested on every packet — `lcPktData` is `PK_PKTBITS_ALL` — and Wintab
documents it as milliseconds with no origin. Its **granularity is now measured** at 1 ms, with
one timestamp per point and no repeats, which makes it the most usable clock of any backend
here.

Its **origin is still unknown**, and that is why this backend detects a wrap rather than
anchoring against the system clock the way the framework backends do — anchoring needs an
origin to anchor to. The recording that settled the granularity could not settle the origin,
because `StrokeRecorder` writes times relative to the first point by design. Differences are
sound; absolute values are not.

Where `Conventions.Timestamp` is `None`, the field is zero. Zero is not a time; it means the
backend supplied none. No session substitutes its own clock, which would measure when this
library got round to reading the packet rather than when the pen moved.

### Counters that wrap

Two backends count milliseconds in fewer than 64 bits:

| backend | raw type | wraps after | what it does |
| --- | --- | --- | --- |
| WPF | `int` | ~24.9 days of uptime | passes `int.MaxValue` and **continues negative** |
| Wintab | `uint` | ~49.7 days of uptime | returns to 0 |

The other four are 64-bit and do not wrap in any relevant timeframe.

Left alone, a stroke drawn across either boundary would produce a difference wrong by the
entire range — about −49.7 days, from two points a millisecond apart. `MillisecondCounter`
extends both inside the session, so `TimestampMicroseconds` stays continuous and the caller
never sees it. Detection is a backward jump of more than half the range; pen packets arrive
milliseconds apart, so nothing legitimate moves backward, and half a range leaves 12 days of
margin.

It cannot recover a wrap that happened while the session was not running, which the
differences-only contract does not promise anyway.

Neither wrap can be reached by ordinary testing, so there is a check that does not need to
wait for one:

```bash
dotnet run --project WinPenKit.TestConsole -- --selftest-clock
```

Six cases, no tablet and no window. Two of them are the wraps; two more are ordinary input and
a repeated value, present because a wrap detector that fires on normal packets would corrupt
every stroke rather than one every few weeks. Verified in both directions: with the extension
removed, the two wrap cases fail by exactly −4,294,967,295,000 µs and the other four still
pass.

### What `RawX/Y` holds

Ask the session: `session.Conventions.RawUnits`.

| Backend | `RawUnits` | |
|---|---|---|
| Wintab digitizer, hi-res context open | `TabletNative` | the tablet's own space |
| Wintab digitizer, fallen back | `ScreenPixels` | |
| Wintab system | `ScreenPixels` | mapped by the driver |
| WM_POINTER, WinForms | `HundredthsOfMillimetre` | `ptHimetricLocationRaw` |
| WPF, WinUI, Avalonia | `None` | **no device-native value exists; the fields are zero** |

Read it as a diagnostic, not a position: sane raw values against a wrong `DesktopX` point at the mapping, and both wrong point upstream of it.

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

## Capture Region (Spatial Scope)

The input paths natively disagree on **where the pen has to be** for your app to get data: Wintab is desktop-global, WM_POINTER is window-scoped, and the framework pointer sessions are control-scoped. `IPenSession.CaptureRegion` normalizes this so one app behaves identically on every backend. (For *why* the backends differ, see [STYLUS.md → Spatial Scope](STYLUS.md#spatial-scope-capture-region).)

```csharp
// Default (CaptureRegion == null): window-scoped on every backend.
// Wintab is filtered to the app window passed to Start(), matching the
// pointer backends instead of capturing across the whole desktop.
session.Start(appHwnd);

// Scope to a fixed screen rectangle.
session.CaptureRegion = PenCaptureRegion.Rect(x, y, width, height);

// Scope to a window's live bounds (tracks moves/resizes).
session.CaptureRegion = PenCaptureRegion.Window(appHwnd);

// Opt back in to desktop-wide capture (Wintab only — see below).
session.CaptureRegion = PenCaptureRegion.Unbounded;
```

`CaptureRegion` may be set before or after `Start()`; it takes effect on the next point.

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

`PenPoint` exposes `ButtonAction`, `ButtonNumber`, `IsTipPressed`, `IsButtonPressed(int)` and `IsButtonReleased(int)`. **All five are obsolete.** They apply the Wintab encoding to any point, and the five pointer backends set only bits 0 and 1, so on those backends `ButtonAction` is always `None` and the three predicates are always `false` — false, rather than any indication that the question could not be answered.

Use `PenButtonTracker`. It branches on `pt.Source` and decodes both encodings. If you must read `pt.Buttons` yourself, check `pt.Source` first and decode accordingly.

### Eraser detection

Eraser is detected via `pt.IsEraser`, which checks `pt.Cursor == 14`. In Wintab, cursor type changes on hover before contact. The pointer backends read `PEN_FLAG_INVERTED` and write 13 or 14 to match. `PenButtonTracker.IsEraser` mirrors this from the latest point.

**The match is one-way.** Wintab writes the driver's own cursor number through unchanged, and Wintab cursor indices are assigned by the device — `PenCursorType` documents 13 and 14 as *observed* values, not standard ones. On a tablet that numbers its eraser differently, `IsEraser` is false on Wintab while true on every pointer backend. Tracked in issue 48.

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
