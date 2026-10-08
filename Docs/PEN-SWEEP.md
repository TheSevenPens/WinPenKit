# Measuring what a tablet's coordinates mean

`WinPenKit.SweepProbe` answers one question: **what do a session's positions mean?** How far does
the pen reach, where does Windows put the cursor for the same pen, how many millimetres is a pixel,
and is any of it cropped. It was written on 8 October 2026 while checking the physical size that
`IPenSession.PhysicalArea` reports, and it corrected two of its author's wrong conclusions along the
way. Both are recorded below because both were easy to make.

## Running it

```
dotnet run --project WinPenKit.SweepProbe -c Release -- info
dotnet run --project WinPenKit.SweepProbe -c Release -- sweep WintabDigitizer 40 sweep.txt
dotnet run --project WinPenKit.SweepProbe -c Release -- sweep WmPointer 40 sweep.txt
```

**`info`** needs no pen. It prints, in this order:

1. The probe's own **DPI awareness**. Read this before anything else. Every coordinate that follows
   is only meaningful if it says per-monitor.
2. The monitors and the virtual screen.
3. Windows' pointer devices and the two rectangles each is normalised between.
4. What Wintab says about itself: the X and Y axes (`DVC_X`, `DVC_Y`) and the four default contexts.
5. What each Wintab session's `PhysicalArea` reports.

**`sweep <api> [seconds] [out.txt]`** opens a window and logs, for every point, the position the
session reports, the Windows cursor, and the raw tablet value. Move the pen over the **whole
tablet, edge to edge, into every corner**. The window shows the raw range reached so far, live,
because nothing else can tell you (see lesson 2). It ends by fitting the cursor against the
reported position and against the raw counts, per axis.

For `WmPointer` the window covers the whole desktop and is semi-transparent: the pointer API
delivers only to the window under the pen, so the window has to be wherever the pen is mapped.

## How to read a sweep

- **Reported position against the cursor.** Slope 1.000, intercept near zero, r² near 1 means the
  session's `DesktopX/Y` *are* the cursor's coordinates. Anything else is a mapping error of the kind
  in issues 129 and 130.
- **Cursor against raw counts** gives pixels per count, per axis. A pixel is `1 / slope` counts, which
  at 100 counts a millimetre (what a Wacom reports) is `0.01 / slope` millimetres. This is an
  independent check of `PenPhysicalArea.MillimetresPerPixelX/Y`, which comes from the driver's own
  rectangles.
- **The raw range reached** is the tablet's live area. Compare it with the axis range from `info`
  (`DVC_X`, `DVC_Y`). If a range stops short, find out why before believing the tablet is smaller.

## What was measured

One machine, so treat it as an example and not a result about Wacom or Windows. Wacom Intuos Pro L
(2025), Wacom Professional Service (`WTabletServicePro`), Windows 11 build 26300. DISPLAY1 is
3840 x 2160 at 175% scaling and DISPLAY2 is 1920 x 1080 at 100%, at (1910, 2160), so the physical
desktop is **3840 x 3240**.

| | measured |
|---|---|
| Wintab axes | 0..34899 by 0..19499, centimetres, 1000 counts a centimetre: **349 x 195 mm** |
| Wintab system rectangle | 3840 x 3240, in all four default contexts, whatever the display mapping |
| tablet mapped to all displays | the cursor follows the pen over the whole L-shaped layout and stops where no monitor exists; raw x reached the full 0..34899, raw y was swept to 3530 and not further |
| reported position against the cursor | slope 1.000 on both axes, r² 0.9999 or better, in every sweep |
| cursor against raw, across | 0.1100 px a count, 0.0909 mm a pixel |
| cursor against raw, down | 0.1662 px a count, 0.0602 mm a pixel |
| WM_POINTER `PhysicalArea` | 349.01 x 195.01 mm, 0.090888 and 0.060188 mm a pixel, agreeing with Wintab to four digits |
| WM_POINTER raw units | hundredths of a millimetre, as `PenRawUnits.HundredthsOfMillimetre` says |
| mapped to DISPLAY1 only (Wintab) | raw y reached **6500 exactly** and no lower |
| mapped to DISPLAY2 only (Wintab and WM_POINTER) | raw x from **17360** and raw y to **6499**, the lower right of the tablet, in both APIs |

## Lessons

**1. Print the DPI awareness before you believe a coordinate.** An earlier reading of these numbers
concluded that the driver reported a "logical" desktop of 3840 x 3240 against a physical one of
6703 x 5670, and that the file's "desktop physical pixels" was therefore wrong. It was the probe.
A *system*-DPI-aware process on a system at 175% sees every monitor not at that scale stretched:
DISPLAY2, really 1920 x 1080 at (1910, 2160), appeared as 3360 x 1890 at (3343, 3780), which is
where 6703 x 5670 came from. The driver's 3840 x 3240 was the physical desktop all along. The
corrected figures are in the table above.

**The project setting does not set awareness by itself.** `ApplicationHighDpiMode` in a WinForms
project only takes effect through `ApplicationConfiguration.Initialize()`. This probe did not call
it, ran DPI-unaware, and printed DISPLAY1 shrunk to 2194 x 1234. The probe now calls it, and prints
its awareness first, so the next mistake of this kind is visible in the first line.

**2. The cursor cannot show you the tablet's edge.** It stops at the edge of a monitor, and with an
L-shaped layout the pen can move through regions where no monitor exists while the cursor sits still.
The person sweeping sees the cursor reach the screen's edge and believes they have reached the
tablet's. Use the raw counts.

**3. A round number at the end of a range is a clue, not a measurement.** Raw y stopped at exactly
6500. That is `19500 - 19500 x 2160 / 3240`: where DISPLAY1's bottom row falls in the driver's
rectangle. A hand sweeping slowly does not stop on a round number; something clipped it.

**4. On this driver, choosing one display crops the tablet.** Seen in both APIs with DISPLAY2, and in Wintab with DISPLAY1. It does not re-map the
tablet onto that display. Wintab's default contexts keep describing the whole 3840 x 3240 desktop,
and a service restart after the first change did not change them. The driver then discards the pen outside the chosen
monitor's part of that rectangle, so mapped to DISPLAY2 only the lower right of the tablet
(about 174 x 65 mm) delivers anything. The scale does not move, so millimetres a pixel stay right.
**What `PenPhysicalArea.MappedWidthMm/HeightMm` report is the context's rectangle, and neither API
says it has been cropped, so that figure can overstate the live area.** Do not read it as the area
the pen actually reaches.

**5. The pointer API delivers to the window under the pen, and the pen is mapped by the driver, not
by you.** A small window on one monitor receives nothing while the pen is mapped to another. Draw
the readout onto the form instead of putting a label on it: a child window takes the messages first.

**6. Know which pointer device you are looking at.** `POINTER_DEVICE_TYPE` is 1 integrated pen, 2
external pen, 3 touch, 4 touch pad. This Wacom appears as both pen types, with different display
rectangles (DISPLAY1, and the whole desktop). Points from the session came from the external pen
and were normalised onto 3840 x 3240. Reading the other entry gave a wrong conclusion that WM_POINTER
mapped to one monitor when it did not.

## Not established

- **The tablet's real size.** Both APIs take 349 x 195 mm from the driver, and nothing here measures
  the pad. A ruler across the active area would. Everything above is consistent with the driver and
  with itself, which is not the same as being right.
- **Any vendor but Wacom.** Whether another driver's pointer device rect is really hundredths of a
  millimetre, or what its display mapping does, has not been seen.
- **Whether the whole tablet delivers.** On this machine the lower third of the tablet was only
  reached in the cropped runs, not in a single sweep with the tablet mapped to the whole desktop,
  so the vertical scale across that last third is extrapolated.
- **A scaled monitor under a recorder window.** Everything above ran with the tablet mapped to
  monitors at their native scale or the desktop as a whole. A Wintab position over a monitor at a
  different scale from the window's has not been compared with that window's own coordinates.
