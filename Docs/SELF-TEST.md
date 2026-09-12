# Self-test and replay

Every Scribble app accepts two flags that verify it without a tablet, without pen input, and without a person looking at the screen.

```bash
Scribble.Wpf.exe --selftest          # environment and drawing surface
Scribble.Wpf.exe --replay            # the above, plus the coordinate conversion
Scribble.Wpf.exe --replay my.csv     # the above, against your own recording
```

Both print a line-oriented report and **exit 0 only when every check passes**, so a script or an agent can branch on the exit code without parsing anything.

```
SELFTEST Scribble.Wpf
[PASS] L0.dpi-awareness        PerMonitorV2
[PASS] L0.window-placement     client 2672x1496 at 69,120; work area 3840x2052 at 0,0
[PASS] L0.scale                2.25x
[PASS] L1.surface-physical     bitmap 2672x1230, expected 2672x1230 (= ceil(1188x547 logical x 2.25))
[PASS] L1.surface-alignment    origin 69.00,386.00px
[PASS] L1.presentation-1to1    bitmap 2672x1230 presented at 2672.0x1230.0 device px
[PASS] L2.recording-subpixel   0.0% of 480 recorded points are on whole pixels
[PASS] L3.conversion-snap      0.0% of converted points land on whole device pixels
[PASS] L3.conversion-lossless  mean turn angle in 0.74 deg, out 0.74 deg (delta 0.00)
RESULT 9/9 passed
```

## Why these exist

Every bug found in these apps during the stroke-quality investigation was found by **a person looking at a stroke and saying it looked bumpy.** That works, and it does not scale: CI has no eyes, and neither does an agent following a build guide.

So the checks that hold without eyes are automated here, and the ones that do not are deliberately left out. The flags are not a replacement for drawing with a real pen — they are the part of that judgement that can be made mechanical.

The approach has already earned its keep. On its first runs, `--selftest` found **Scribble.Avalonia and Scribble.WinUI both rendering their canvas at 44% of the display's resolution** — two apps that had been signed off as correct, because fixing their coordinates made the strokes smooth enough to pass a visual check while the surface stayed wrong.

## The checks

Ordered so that **the first failure is the root cause**: each level assumes the ones above it pass.

### Level 0 — environment

Needs nothing. Every one of these invalidates all later measurements when wrong.

| check | catches |
| --- | --- |
| `L0.dpi-awareness` | anything below Per-Monitor V2, where Win32 returns virtualized coordinates that do not match what the pen reports |
| `L0.window-placement` | a window hanging off its monitor or under the taskbar, which **silently discards** pen input aimed there while continuing to run and paint |
| `L0.scale` | records the display scale; everything below is relative to it |

### Level 1 — drawing surface

Also needs nothing — no tablet, no pen, no person. Most of the surface bug classes in this repo are caught here, at launch, before anything is drawn.

| check | catches |
| --- | --- |
| `L1.surface-physical` | a canvas sized in logical units and magnified to fit — at 2.25x that is a surface drawn at 44% of the display's resolution, which no amount of coordinate precision survives |
| `L1.surface-alignment` | a surface landing on a fractional device pixel, so the framework resamples all of it to draw it between pixel rows, softening every edge while coordinates and resolution both still measure correct |
| `L1.presentation-1to1` | a correctly sized surface scaled back off the pixel grid on its way to the screen |

`L1.surface-alignment` reports both axes separately, because the error is routinely one-dimensional. One real instance was aligned in x and 0.64px out in y — which is how a check that scanned across a near-vertical stroke reported it clean.

### Levels 2 and 3 — the coordinate conversion (`--replay`)

Needs pen data, which the replay supplies from a recording instead of a tablet.

A recorded stroke is pushed through the application's **own** desktop-to-canvas conversion — the same function the pen goes through, not a reimplementation of it. A replay with its own arithmetic would be testing itself.

| check | catches |
| --- | --- |
| `L2.recording-subpixel` | the recording is already quantized, so nothing below it can fail |
| `L3.conversion-snap` | an integer-typed API truncating the position somewhere in the conversion |
| `L3.conversion-lossless` | the conversion changed the shape of the path |

**`L3.conversion-lossless` is the strongest check here, and the only one that needs no threshold.** A conversion is a translation and a uniform scale, both of which preserve angles exactly — so a lossless implementation reproduces the input's mean turn angle to the decimal. Comparing against a fixed number would require calibrating against how the stroke was drawn; comparing against the input calibrates itself.

That matters more than it sounds. Every other measurement tried during the investigation needed a reference value that depended on the stroke, and two of them produced confident wrong answers because of it.

## What is not covered

Three things, and a green report should not be read as a statement about any of them.

**The session.** A recording holds what the session *produced*, so replaying it exercises everything downstream of the session and nothing inside it. An application that converts perfectly can still be handed pre-quantized coordinates by its own session — which is exactly what WPF's stylus stack did. `L2.recording-subpixel` exists so this boundary stays visible rather than being mistaken for coverage. **This half needs real hardware.**

**Wintab, at all.** Synthetic pen injection does not reach the driver. Every claim about Wintab behaviour needs a tablet.

**Whether the stroke looks right.** Antialiasing and pressure response are measurable from a captured bitmap in principle, and are deliberately not automated: an image-derived metric reported a canvas as clean while it was being resampled, because it happened to scan the axis with no error. Checks that hold are automated; judgements about how a stroke looks belong to a person with a pen.

## Recordings

`--replay` with no argument uses `testdata/reference-stroke.csv`, found by walking up from the executable.

That stroke is synthetic but shaped like a slow hand-drawn curve: ~1.6px sampling, gentle curvature, and **no high-frequency noise** — which is the point, since any turn angle in the output much above the input's must have come from the conversion.

| | mean turn angle |
| --- | --- |
| reference stroke, clean | **0.74°** |
| the same stroke quantized to whole pixels | **18.71°** |
| measured on real hardware before the fix | 17.51° |

Two orders of magnitude between signal and noise, and the synthetic quantized figure lands within about a degree of what a tablet actually produced.

`--replay <path>` takes a recording of your own — `desktopX,desktopY,pressure`, with `#` comments and a header line. That is how a stream captured from real hardware gets held to the same assertions.

## Known flakiness

`L0.window-placement` depends on where the window manager puts the window. A window cascaded down far enough to sit under the taskbar fails the check — correctly, since input aimed there really is discarded, but it means repeated launches can differ. If these flags are ever made a CI gate, that check needs either a deterministic window position or a documented exemption.

## Implementation

`WinPenKit.Diagnostics.SelfTest` and `StrokeReplay` are shared by the four managed samples. `Scribble.Win32` and `Scribble.Rust` reimplement the same check ids, line format and exit code rather than binding to them — having no managed runtime underneath is the point of those two samples.

Adding the checks to an app of your own means exposing three things: the backing surface's pixel size, the canvas origin in device pixels, and the desktop-to-canvas conversion itself.
