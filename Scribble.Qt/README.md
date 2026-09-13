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

**The backend is fixed at startup.** Qt chooses between WM_POINTER and WinTab while the Windows
platform plugin initialises, before `QApplication` is running. `--wintab` and `--pointer` select
it for the life of the process; switching means relaunching. Krita's UI has the same restriction
and says so. Nor can the application ask afterwards which one it got, so the ribbon reports what
this process requested — which is all Krita can report either.

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
ScribbleQt.exe                       # WM_POINTER, Qt's default on Windows
ScribbleQt.exe --wintab              # WinTab, tablet-native
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
[PASS] L1.surface-physical       bitmap 2700x1503, expected 2700x1503 (= ceil(1200x668 logical x 2.25))
[PASS] L1.surface-alignment      origin 572.00,311.00px
[PASS] L1.presentation-1to1      bitmap 2700x1503 presented at 2700.0x1503.0 device px
[PASS] L1.presentation-sampling  markers 675x375 surface px apart appeared 675.0x375.0 device px apart (x 1.000, y 1.000)
[PASS] L2.recording-subpixel     0.0% of 1225 recorded points are on whole pixels
[PASS] L3.conversion-snap        0.0% of converted points land on whole device pixels
[PASS] L3.conversion-lossless    mean turn angle in 4.86 deg, out 4.86 deg (delta 0.00)
[PASS] L3.origin-tracks-window   moved 37,23px; conversion followed
RESULT 11/11 passed
```

The replay figures above come from `testdata/avalonia-pointer-stroke.csv` — a stroke drawn by
hand through Avalonia's pointer path, taken through Qt's coordinate conversion, losing nothing.
That is the comparison this sample is for.

## Two Qt-specific things found while building it

**Qt's `emit` macro collides with `SelfTest::emit()`.** Qt defines `emit` as empty so that
`emit signalName()` reads as syntax, which turns `r.emit()` into `r.()`. Both translation units
`#undef emit` before including the shared header and say `Q_EMIT` instead.

**`QTabletEvent::TabletRelease` carries pressure 0.5, not 0.** Every other sample here gates
recording on `pressure > 0`, which is correct for its API and wrong for this one: it appends one
spurious point per stroke, at the previous point's position and a pressure the stroke never had.
Measured, not assumed — a 62-point injected stroke came out as 61 points at 700 and one at 512.
Contact is tracked from `TabletPress` and `TabletRelease` instead.
