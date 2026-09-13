# Scribble.Qt

The seventh Scribble sample, and the only one that does not use WinPenKit. It draws with Qt's
own stylus handling — `QTabletEvent` and nothing else.

## Why it exists

Six samples sharing one library can agree with each other and still be wrong together. A
seventh built on an independent pen stack is what turns "our six agree" into a statement about
Windows rather than about this repository.

It is also the closest thing here to what [Krita](../Docs/../README.md) sees. Krita does not
implement WinTab or Windows Ink itself; it configures Qt at startup and consumes `QTabletEvent`
from there. Anyone comparing their own stroke quality against Krita's is comparing against
whatever Qt hands Krita, and this sample is that, with a report attached.

## What it shares, and what it does not

It shares exactly one file with the rest of the repository: `Scribble.Win32/src/selftest.h`.
The check ids, the report lines and the recording format have to match or the comparison this
sample exists for is not a comparison. That header depends on nothing but `<windows.h>` and the
standard library, so sharing it costs no WinPenKit dependency — there is no `WinPenKit.dll`, no
`WinPenKit.Native.lib` and no WinPenKit header anywhere in this project.

## Three things Qt settles for you

**The backend is fixed at startup, so the dropdown saves rather than switches.** Qt chooses
between WM_POINTER and WinTab while the Windows platform plugin initialises, before
`QApplication` is running.

Picking one in the ribbon stores it and says so in place:

> Restart to use Wintab, tablet-native. Still on WM_Pointer.

The next launch comes up on the stored choice. The notice sits under the dropdown rather than in
a dialog, because a dialog is dismissed and forgotten while this stays until the restart makes
it untrue. The choice lives in `HKCU\Software\TheSevenPens\Scribble.Qt`, read before
`QApplication` is constructed — it has to be, since it decides the arguments `QApplication` is
constructed with.

`--wintab` and `--pointer` override the stored choice for one run without changing it. A flag is
for one launch; letting it rewrite the setting would mean a single `--wintab` run silently
changed what every later plain launch did.

Qt also offers no way to ask afterwards which path it took, so `L0.pen-api` records it in the
self test report. A run that does not record it has no way to say. Krita is in the same
position: it knows what it asked for and nothing more.

**Qt's WinTab context is always tablet-native.** Qt overrides `lcOutExt` to `lcInExt` on an
ordinary `WTI_DEFSYSCTX` and maps to the desktop itself in `qreal`. There is no low-resolution
option. So `--wintab` here is comparable to WinPenKit's `WintabDigitizer` and never to its
`WintabSystem`.

**Position arrives as a fractional `QPointF` in device independent pixels, and pressure
normalised to 0..1.** Both are converted at the event handler rather than carried inward, so
everything below it is in the units the other six samples use. Pressure is recorded against an
assumed maximum of 1024, which the recording's header states, because Qt does not report the
device's own range.

## Building

Qt is found, not vendored. CMake and Ninja ship with Visual Studio.

```
cmake -B build -G "Visual Studio 18 2026" -A x64 -DCMAKE_PREFIX_PATH=C:/Qt/6.8.3/msvc2022_64
cmake --build build --config Debug
C:/Qt/6.8.3/msvc2022_64/bin/windeployqt.exe --debug build/Debug/ScribbleQt.exe
```

Verified against Qt 6.8.3 `msvc2022_64`, built with the v145 toolset against Qt's v143 binaries.

## Running

```
ScribbleQt.exe                       # whatever the ribbon last stored; WM_POINTER by default
ScribbleQt.exe --wintab              # WinTab, tablet-native, this run only
ScribbleQt.exe --pointer             # WM_POINTER, this run only
ScribbleQt.exe --selftest            # environment and drawing surface
ScribbleQt.exe --replay <file.csv>   # the above, plus the coordinate conversion
ScribbleQt.exe --record <file.csv>   # capture a live stroke, written on close
```

## What it reports

```
SELFTEST Scribble.Qt
[PASS] L0.dpi-awareness          PerMonitorV2
[PASS] L0.window-placement       client 2700x1575 at 572,239; work area 3840x2052 at 0,0
[PASS] L0.scale                  2.25x
[PASS] L0.pen-api                Wintab, tablet-native, fixed at startup
[PASS] L1.surface-physical       bitmap 2700x1332, expected 2700x1332 (= ceil(1200x592 logical x 2.25))
[PASS] L1.surface-alignment      origin 572.00,482.00px
[PASS] L1.presentation-1to1      bitmap 2700x1332 presented at 2700.0x1332.0 device px
[PASS] L2.recording-subpixel     0.0% of 1225 recorded points are on whole pixels
[PASS] L3.conversion-snap        0.0% of converted points land on whole device pixels
[PASS] L3.conversion-lossless    mean turn angle in 4.86 deg, out 4.86 deg (delta 0.00)
[PASS] L1.presentation-sampling  markers 675x333 surface px apart appeared 675.0x333.0 device px apart (x 1.000, y 1.000)
[PASS] L3.origin-tracks-window   moved 37,23px; conversion followed
RESULT 12/12 passed
```

The replay figures above come from `testdata/avalonia-pointer-stroke.csv` — a stroke drawn by
hand through Avalonia's pointer path, taken through Qt's coordinate conversion, losing nothing.
That is the comparison this sample is for.

## How Qt times a stroke, and how that differs from WinPenKit

Measured 12 Sep 2026 by probing `QTabletEvent` directly through two synthetic strokes: 127
points, both strokes captured, every claim below checked against all 127 rows rather than read
from the documentation.

### Qt hands you five timing values per point; WinPenKit hands you one

| Qt | type | what it is |
| --- | --- | --- |
| `QInputEvent::timestamp()` | `quint64` ms | free-running counter, tracks `GetTickCount64` |
| `QEventPoint::timestamp()` | `ulong` ms | **identical to the event's** on all 127 rows |
| `QEventPoint::lastTimestamp()` | `ulong` ms | the previous point's value |
| `QEventPoint::pressTimestamp()` | `ulong` ms | the value at the press that began this stroke |
| `QEventPoint::timeHeld()` | `qreal` | **seconds** since that press |

`timeHeld()` is derived, not measured: `(timestamp - pressTimestamp) / 1000.0` held exactly on
all 127 rows. `pressTimestamp` reset per stroke, as it should — 0, then 116545968, then
116547468 for the two strokes.

WinPenKit's `PenPoint.TimestampMicroseconds` is the equivalent of the first two rows only. The
other three are conveniences a caller can compute, and WinPenKit does not compute them.

So the difference is not that one is absolute and the other stroke-relative. **Both are
free-running counters.** Qt additionally derives the stroke-relative values for you.

### Three traps in Qt's extras

**`timeHeld()` is meaningless before a press.** `pressTimestamp()` is 0 until one happens, and
`timeHeld()` subtracts from that anyway. The hover point at the start of the run reported
**116545.859 seconds** — 32 hours, the machine's uptime. Anything that reads `timeHeld()` on a
proximity or hover point gets that, not 0.

**`lastTimestamp()` is 0 on the first event**, so a first delta computed from it is the whole
uptime rather than a frame.

**Three of them are 32-bit.** `timestamp()`, `lastTimestamp()` and `pressTimestamp()` return
`ulong`, which is 32 bits on MSVC, so they wrap after about 49.7 days of uptime. `timeHeld()`
returns `qreal` and `velocity()` a `QVector2D`, so "all five are narrow" would be wrong.

`QInputEvent::timestamp()` is a `quint64`, and that does **not** make it safe. On Windows Qt
fills it from `GetMessageTime`, which is 32 bits, and widens the result afterwards — the value
has already wrapped by the time it is a `quint64`. A recording made across the boundary carried
a difference of about −49.7 days until this sample started anchoring the reading against
`GetTickCount64`.

Reading the declared width and concluding the clock is wide is the same error as reading
`MaxPressure` 32767 as a level count, and it is why WinPenKit's framework backends anchor
rather than trust the type.

### Qt has the coarsest clock of the seven samples

Measured by hand on a Wacom DTH246, 13 Sep 2026: one stroke, 2280 points, **810 distinct
timestamps**. Across 809 gaps the smallest is **15 ms** — 504 of 16 ms, 303 of 15 ms, one 31 and
one 32. Nothing finer occurs at all.

This was the only backend whose injected figure survived contact with a tablet. WM_POINTER and
WinUI both measured 1 ms under `InjectSyntheticPointerInput` and both turned out to be
microsecond-resolved when drawn on; Qt measured 15.6 ms and is 15.6 ms.

That matters for anyone treating Qt as the reference, Krita included. All five rows below are
now hardware measurements except Avalonia:

| | distinct timestamps / points | step |
| --- | --- | --- |
| WM_POINTER | 2070 / 2070 | **1 µs** |
| WinUI 3 | 1878 / 1878 | **1 µs** |
| Wintab (high-res) | 1683 / 1683 | 1 ms |
| WPF | 885 / 2442 | 1 ms clock, 15.6 ms batches |
| **Qt** | **810 / 2280** | **15.6 ms** |
| Avalonia *(injected)* | 113 / 172 | 1 ms |

Qt and WPF land in a similar place by opposite routes, and the distinction is the useful part.
WPF stamps a whole `StylusPointCollection` at once, so its **delivery** is coarse while its
clock resolves to the millisecond. Qt's `QTabletEvent` is a `QSinglePointEvent` carrying one
point, so all 2280 are separate events — **Qt's coarseness is the clock alone.** A finer clock
would fix Qt and would do nothing for WPF.

One caution about reading this file's numbers, or any timing file. The greatest common divisor
of Qt's gaps is 1000 µs, and it means nothing: `gcd(15000, 16000)` is 1000, so alternating
between the two ticks of a 15.625 ms timer produces that figure arithmetically. The same
statistic was real evidence on the WM_POINTER recording, where gaps that small actually
occurred. Quote the minimum gap alongside the gcd.

All of this is synthetic injection, which stamps its own events, so each figure is an upper
bound on granularity rather than proof the hardware path is no better. It needs a tablet to
settle.

## Two Qt-specific things found while building it

**Qt's `emit` macro collides with `SelfTest::emit()`.** Qt defines `emit` as empty so that
`emit signalName()` reads as syntax, which turns `r.emit()` into `r.()`. Both translation units
`#undef emit` before including the shared header and say `Q_EMIT` instead.

**`QTabletEvent::TabletRelease` carries pressure 0.5, not 0.** Every other sample here gates
recording on `pressure > 0`, which is correct for its API and wrong for this one: it appends one
spurious point per stroke, at the previous point's position and a pressure the stroke never had.
Measured, not assumed — a 62-point injected stroke came out as 61 points at 700 and one at 512.
Contact is tracked from `TabletPress` and `TabletRelease` instead.

## The ribbon

The same seven sections as the other samples, in the same order: PEN API, BRUSH, PEN, BUTTONS,
POSITION, PRESSURE, ORIENTATION. Three fields cannot be filled the way the others fill them, and
are shown as unavailable rather than given a stand-in:

| field | the other samples | here |
| --- | --- | --- |
| POSITION Raw | tablet units, screen pixels or 0.01mm | `--`; Qt exposes no device-native coordinate |
| PEN Cursor | the driver's cursor number | `Pen` or `Eraser`; Qt reports a pointer type |
| PRESSURE Raw | the device's own range | reconstructed against 1024; Qt normalises to 0..1, and Norm shows what it actually gave |

`B3` is drawn and can never light. Qt reports a mouse-button mask, so the tip is the left button
and the barrel switches are right and middle — a pen with three barrel switches reports two. The
dot stays to make that visible beside the Wintab samples where the third does light.

Two Qt behaviours the ribbon has to correct for, both measured:

**Pressure reads 0 once the pen leaves the surface, not Qt's 0.5.** `TabletRelease` carries a
default pressure, so reporting it leaves every finished stroke showing half pressure — a value
the pen never applied, and one no other sample shows.

**The ribbon's height is rounded up to a whole number of device pixels.** Qt lays widgets out in
whole logical pixels, and at 2.25 a whole logical height is not a whole device height: the
ribbon wanted 211, which put the canvas origin at y=475.25 and made the framework resample the
entire surface to draw it between pixel rows. `L1.surface-alignment` caught it. The height is
grown to the next value whose product with the scale is whole, and recomputed when the window
moves to a display with a different scale.
