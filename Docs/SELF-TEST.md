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
[PASS] L3.conversion-lossless   mean turn angle in 0.74 deg, out 0.74 deg (delta 0.00)
[PASS] L3.origin-tracks-window  moved 37,23px; conversion followed
RESULT 10/10 passed
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
| `L1.presentation-1to1` | a correctly sized surface given a host of the wrong size, so it is scaled back off the pixel grid on its way to the screen |
| `L1.presentation-sampling` | a correctly sized surface in a correctly sized host, with only part of it drawn across the whole host |

### Why there are two presentation checks

`L1.presentation-1to1` compares the **host's layout size** against the surface's pixel count.
A host can cover exactly the right number of device pixels and still draw only part of the
surface across them, and that check passes on it.

Issue 70 was exactly that: a 2700px bitmap in a host covering 2700 device pixels, matching to
the pixel, while the framework rendered the top-left 1200x600 pixels of it stretched across the
whole host. Strokes landed 2.25 times too far from the canvas origin. The check passed before
the fix and after it.

No property could have caught it. `Bitmap.Size` said 1200 DIP, `Image.Bounds` said 1200 DIP,
the arranged desired size said 1200 — all correct, at the moment the ink was landing 2.25 times
too far out. The disagreement sat between `Size` and the draw, inside the framework. A check
reading those properties would have been a second instrument with the same blind spot.

So `L1.presentation-sampling` measures pixels instead. It draws two markers a known distance
apart into the surface, waits for them on the screen, and compares the distance between them
there. Because it is a ratio of two distances, it needs neither the canvas origin nor the
display scale — both cancel, which matters, since a wrong origin is one of the things it has to
be able to catch.

Reintroducing issue 70 into `Scribble.Avalonia` produces:

```
[PASS] L1.presentation-1to1      bitmap 2700x1351 presented at 2700.0x1351.0 device px
[FAIL] L1.presentation-sampling  markers 675x337 surface px apart appeared 1518.0x758.0 device
                                 px apart (x 2.249, y 2.249)  <- the surface is sampled at
                                 2.25x horizontally and 2.25x vertically, so ink lands that far
                                 from where the pen was
```

**Two things follow from measuring the screen.** The window has to be visible and unobscured,
so this is the one check that cannot run on a hidden window. And the application has to drive
it across two frames: draw the markers, present, then await the measurement. Finding both
markers is what says the frame landed, and two consecutive readings that agree is what says
nothing is still moving — Windows animates a window open by compositing it scaled up to its
final size, and a capture taken during that reads a few per cent small.

All six samples have it. `Scribble.Win32` and `Scribble.Rust` reimplement the measurement
rather than binding to the managed one, for the same reason the rest of their self test is a
reimplementation: neither has a .NET runtime under it. The check id and the line format match,
so one script still reads all six.

The three differ only in how the application waits for the frame, because that is the one part
each framework owns:

| sample | how it waits |
| --- | --- |
| WPF, WinForms, WinUI, Avalonia | `await`, which yields the UI thread |
| `Scribble.Win32` | pumps its own message queue; its checks run before the message loop exists |
| `Scribble.Rust` | polls once per egui frame and asks for a repaint |

A stretch introduced deliberately into each reports the factor it was given: 2.25x in Avalonia,
1.25x in `Scribble.Win32` (`StretchBlt` at 80%), 1.30x in `Scribble.Rust` (a host 1.3x the
pixmap). In all three `L1.presentation-1to1` passed on the same build.

`L1.surface-alignment` reports both axes separately, because the error is routinely one-dimensional. One real instance was aligned in x and 0.64px out in y — which is how a check that scanned across a near-vertical stroke reported it clean.

### Levels 2 and 3 — the coordinate conversion (`--replay`)

Needs pen data, which the replay supplies from a recording instead of a tablet.

A recorded stroke is pushed through the application's **own** desktop-to-canvas conversion — the same function the pen goes through, not a reimplementation of it. A replay with its own arithmetic would be testing itself.

| check | catches |
| --- | --- |
| `L2.recording-subpixel` | the recording is already quantized, so nothing below it can fail |
| `L3.conversion-snap` | an integer-typed API truncating the position somewhere in the conversion |
| `L3.conversion-lossless` | the conversion changed the shape of the path |
| `L3.origin-tracks-window` | the canvas origin is cached, and goes stale when the window moves |

**`L3.conversion-lossless` is the strongest check here, and the only one that needs no threshold.** A conversion is a translation and a uniform scale, both of which preserve angles exactly — so a lossless implementation reproduces the input's mean turn angle to the decimal. Comparing against a fixed number would require calibrating against how the stroke was drawn; comparing against the input calibrates itself.

That matters more than it sounds. Every other measurement tried during the investigation needed a reference value that depended on the stroke, and two of them produced confident wrong answers because of it.

**`L3.origin-tracks-window` covers the term the other three cannot see.** A conversion is an origin and a scale, and every check above holds the window still — so an origin that is simply wrong cancels out of all of them. The replay places its input relative to the origin the application reports, then the application subtracts the same value back off. `L1.surface-alignment` does not close the gap either: it asks whether the origin is a whole number, not whether it is the right one.

So this check moves the window a known distance and converts the same desktop point again. A conversion that reads the origin fresh reports a position shifted by exactly that distance; one that cached the origin reports what it did before.

`Scribble.Wpf` shipped with that fault. It cached the origin at bitmap-creation time, dragging the window raised no size change, and every stroke after a drag landed the drag distance away from the pen while all nine checks passed. A person drawing found it in seconds.

The window is moved and put back, so this check runs last. It skips on a maximized window, since moving one restores it. `Scribble.Rust` measures the same property across two frames rather than within one, because egui reads the window position once per frame into its input snapshot.

## What is not covered

Three things, and a green report should not be read as a statement about any of them.

**The session.** A recording holds what the session *produced*, so replaying it exercises everything downstream of the session and nothing inside it. An application that converts perfectly can still be handed pre-quantized coordinates by its own session — which is exactly what WPF's stylus stack did. `L2.recording-subpixel` exists so this boundary stays visible rather than being mistaken for coverage. **This half needs real hardware.**

**Wintab, at all.** Synthetic pen injection does not reach the driver. Every claim about Wintab behaviour needs a tablet.

**Whether the stroke looks right.** Antialiasing and pressure response are measurable from a captured bitmap in principle, and are deliberately not automated: an image-derived metric reported a canvas as clean while it was being resampled, because it happened to scan the axis with no error. Checks that hold are automated; judgements about how a stroke looks belong to a person with a pen.

`L1.presentation-sampling` also reads a captured bitmap, so it is worth saying what makes it different. It looks for two marks it put there itself, at coordinates it chose, and reports x and y separately — so the failure above, a metric that happened to scan the clean axis, is one it reports rather than one it can have. What it does not do is judge anything about the ink.

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

### Capturing one

`--record <path>` writes the session's stream to that format. Five of the six samples
implement it: `Scribble.Wpf`, `Scribble.Avalonia`, `Scribble.WinForms`, `Scribble.WinUI`
and `Scribble.Win32`. `Scribble.Rust` does not.

The path is required. A recorder that picked its own filename would overwrite the previous
capture, which is the one thing a person drawing a comparison pair cannot afford.

```
Scribble.Wpf.exe   --record wpf-stylus.csv
ScribbleCpp.exe    --record native-pointer.csv
```

Draw, then close the window: the recording is written on close, and the point count goes to
standard error. Killing the process loses the recording -- there is no incremental write. Positions are written with round-trip formatting and no rounding of any kind, because a recorder that quantized its own output would report every session as quantized.

This is what makes one session measurable against another. `--replay` on the result prints the recording's own mean turn angle as the `in` figure of `L3.conversion-lossless`, so two captures from the same hand on the same tablet can be compared directly:

```
Scribble.Wpf.exe --replay wpf-stylus.csv
[PASS] L3.conversion-lossless  mean turn angle in <this recording> deg, out ... 
```

Capture one stroke per API, drawn by the same hand at the same speed, and the `in` figures are directly comparable.

`L2.recording-subpixel` on the same run says whether that session delivered sub-pixel data at all, which is the question `--replay` alone can never answer about the session that produced its input.

## Window placement

`L0.window-placement` used to depend on launch history. Windows cascades each launch a little further down and to the right, so a sample that fitted on one run hung below the work area a few runs later and failed — correctly, since input aimed at the part hanging off really is discarded, but it made the check look unreliable.

The fix went into the samples rather than the check. Each one calls `WinPenKit.WindowPlacement.ClampToWorkArea` as soon as its window exists, which moves the window inside its monitor's work area and shrinks it first if it does not fit. `Scribble.Win32` and `Scribble.Rust` carry their own copies, as they do for the checks themselves.

Measured on a 3840x2052 work area, running `Scribble.Avalonia` twelve times in a row. Its client top cycles through three cascade positions:

```
463, 175, 340, 463, 175, 340, 463, 175, 340, 463, 175, 340
```

The third of those used to be 505, which put the bottom edge 23 pixels past the work area. 463 is the clamp. Thirty consecutive runs across all six samples now pass.

**Loosening the check was the wrong fix.** A window below the work area silently discards pen input aimed there, which is a real defect in an application a person is about to draw on. The check was reporting something true.

Two things still make the placement worth watching: a window dragged off the display by hand after startup, and a display-scale change that makes a window too large for the monitor it lands on. Neither is covered by a clamp at startup alone.

## Implementation

`WinPenKit.Diagnostics.SelfTest` and `StrokeReplay` are shared by the four managed samples. `Scribble.Win32` and `Scribble.Rust` reimplement the same check ids, line format and exit code rather than binding to them — having no managed runtime underneath is the point of those two samples.

Adding the checks to an app of your own means exposing three things: the backing surface's pixel size, the canvas origin in device pixels, and the desktop-to-canvas conversion itself.
