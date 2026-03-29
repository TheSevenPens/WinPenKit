# WintabSession Consumer Guide

How to use `WintabSession` from the WintabDN library to receive pen input in any .NET UI framework.

## What WintabSession does

`WintabSession` handles everything Wintab:

- Opens Wintab contexts (system or digitizer hi-res mode)
- Receives packets at 200+ Hz on a background thread
- Converts tablet coordinates to desktop coordinates (`double` precision)
- Queues `PenPoint` records in a thread-safe `ConcurrentQueue`
- Manages the CXO_SYSTEM requirement, Y-axis inversion, multi-monitor mapping, hi-res OutExt override, and fallback logic

## What WintabSession does NOT do

- No framework-specific types (`Point`, `Canvas`, `Control`, etc.)
- No desktop → canvas/panel coordinate conversion (framework-specific)
- No brush engine (pressure → stroke width, color, dabs)
- No stroke continuity tracking (previous point, first-in-stroke)
- No canvas bounds checking
- No rendering

These are the consumer's responsibility.

## Minimal usage

```csharp
// 1. Create the session (once, at app startup or first use)
var session = new WintabSession();

// 2. Start receiving pen data
string? error = session.Start(WintabResolution.ScreenResolution);  // or true for hi-res
if (error != null)
{
    // Show error to user — Wintab not available, context failed, etc.
    ShowError(error);
    return;
}

// 3. Poll on a render timer (~60 fps)
//    DrainPoints() returns all accumulated PenPoints since the last call.
PenPoint[] points = session.DrainPoints();
foreach (var pt in points)
{
    // pt.DesktopX, pt.DesktopY — physical screen pixels (double)
    // pt.Pressure — raw pressure (0 to session.MaxPressure)
    // ... convert to canvas coords, feed to brush engine, etc.
}

// 4. Stop when done (or when switching modes)
session.Stop();

// 5. Restart in a different mode
session.Start(WintabResolution.DigitizerResolution);

// 6. Dispose when the app closes
//    This also closes the diagnostic log file.
session.Dispose();
```

**Important:** Always call `Dispose()` when the session is no longer needed (e.g., on window close). This stops the Wintab context and closes the diagnostic log file. Failing to dispose leaks file handles. In WinUI 3, wire this to the `Window.Closed` event; in WinForms, use the `FormClosing` event.

## PenPoint fields

Every `PenPoint` contains:

| Field | Type | Description |
|---|---|---|
| `DesktopX` | `double` | Physical screen X in pixels. Sub-pixel precision in digitizer mode. |
| `DesktopY` | `double` | Physical screen Y in pixels. Sub-pixel precision in digitizer mode. |
| `RawX` | `int` | Raw `pkX` from Wintab. Tablet-native units in digitizer mode (~0–52,600), screen pixels in system mode (~0–3,840). |
| `RawY` | `int` | Raw `pkY` from Wintab. |
| `Pressure` | `uint` | Pen tip pressure. 0 = hovering/not touching. Normalize: `(float)pt.Pressure / session.MaxPressure` |
| `Azimuth` | `int` | Pen compass direction in tenths of a degree (0–3600). Spherical representation. |
| `Altitude` | `int` | Pen angle from surface in tenths of a degree (0–900). 900 = perpendicular. Spherical representation. |
| `Twist` | `int` | Barrel rotation in tenths of a degree (0–3600). |
| `TiltX` | `int` | Planar tilt X in tenths of a degree (-900 to +900). Positive = tilt right. Computed from Azimuth/Altitude. |
| `TiltY` | `int` | Planar tilt Y in tenths of a degree (-900 to +900). Positive = tilt toward user. Computed from Azimuth/Altitude. |
| `Z` | `int` | Height above the tablet surface. |
| `Buttons` | `uint` | Button event encoded as `(action << 16) \| buttonNumber`. See [Buttons and Eraser Detection](#buttons-and-eraser-detection) below. |
| `Cursor` | `uint` | Cursor type ID. 13 = pen tip, 14 = eraser (observed Wacom values). See [Buttons and Eraser Detection](#buttons-and-eraser-detection). |
| `Source` | `InputApi` | `WintabSystem` or `WintabDigitizer`. |

## Buttons and Eraser Detection

### Button encoding

Wintab encodes button events in `PenPoint.Buttons` as:

```
Buttons = (action << 16) | buttonNumber
```

| Component | Bits | Values |
|---|---|---|
| **Action** (high word) | bits 16–31 | `0` = no event, `1` = released, `2` = pressed |
| **Button number** (low word) | bits 0–15 | `0` = tip, `1` = barrel 1, `2` = barrel 2, `3` = barrel 3 |

This is a button **event** encoding, not a bitmask. Each packet reports at most one button transition. When no button change occurs, `Buttons` is `0x00000000`.

Use the helpers:

```csharp
// Check specific button events
if (pt.IsButtonPressed(PenButtonNumber.Barrel1))  { /* side button 1 just pressed */ }
if (pt.IsButtonReleased(PenButtonNumber.Barrel2)) { /* side button 2 just released */ }
if (pt.IsTipPressed) { /* pen tip just touched surface */ }

// For continuous "is the pen touching" detection, use Pressure instead:
if (pt.Pressure > 0) { /* pen is on the surface */ }

// Extract raw action and number
PenButtonAction action = pt.ButtonAction;  // Pressed, Released, or None
int buttonNum = pt.ButtonNumber;            // 0, 1, 2, or 3
```

### Observed button mappings

Tested with two Wacom styluses on an Intuos Pro Large (PTK-870):

**Pro Pen 3 (ACP-500) — 3 barrel buttons, no eraser:**

| Physical button | Button number | Cursor | Notes |
|---|---|---|---|
| Tip contact | 0 | 13 (pen) | Reports pressure |
| Button 1 (closest to tip) | 1 | 13 (pen) | |
| Button 2 (middle) | 2 | 13 (pen) | |
| Button 3 (farthest from tip) | 3 | **14 (eraser)** | Driver maps this to eraser cursor |

**Pro Pen 2 (KP-504) — 2 barrel buttons, physical eraser:**

| Physical button | Button number | Cursor | Notes |
|---|---|---|---|
| Tip contact | 0 | 13 (pen) | Reports pressure |
| Button 1 (closest to tip) | 1 | 13 (pen) | |
| Button 2 (farther from tip) | 2 | 13 (pen) | |
| Eraser hover (flip pen) | (no button event) | **14 (eraser)** | Cursor changes on proximity alone |
| Eraser contact | 0 | **14 (eraser)** | Reports pressure; same button as tip |

### Eraser detection

The eraser is detected via `PenPoint.Cursor`, **not** via button number. When the eraser is active:

- `Cursor` changes to `14` (`PenCursorType.Eraser`)
- This happens **on hover** — before the eraser touches the surface
- Eraser contact uses button number `0` (same as tip) and reports pressure
- The only difference from pen-tip contact is the cursor type

```csharp
if (pt.IsEraser)
{
    // Eraser mode — erase strokes under the cursor
    if (pt.Pressure > 0)
    {
        // Eraser is touching the surface
        EraseAt(canvasPoint, pt.Pressure);
    }
}
else
{
    // Pen mode — draw strokes
    if (pt.Pressure > 0)
    {
        DrawAt(canvasPoint, pt.Pressure);
    }
}
```

### Important notes

- **Button numbers are stylus-specific.** The Pro Pen 3 has 3 barrel buttons (1, 2, 3); the Pro Pen 2 has 2 (1, 2). Other styluses may differ.
- **Cursor type values (13, 14) are observed from Wacom drivers** and may vary with different tablet/driver combinations. Always test with your specific hardware.
- **The driver can remap buttons.** Users can configure button assignments in the Wacom Tablet Properties app. The Pro Pen 3's button 3 → eraser mapping is a driver default, not hardcoded.
- **No bitmask.** Unlike other APIs where buttons are a bitmask of simultaneous states, Wintab's relative-mode encoding reports one button event per packet. To track which buttons are currently held, the app must maintain its own state by processing press/release events.

## Session properties

| Property | Type | Description |
|---|---|---|
| `IsRunning` | `bool` | Whether the session has an open context producing points. |
| `IsDigitizerMode` | `bool` | Whether the session is in digitizer hi-res mode. |
| `MaxPressure` | `int` | Maximum pressure the tablet can report. Queried once at construction. Use to normalize: `pt.Pressure / MaxPressure`. |
| `HasNewData` | `bool` | Whether new points have been enqueued since the last `DrainPoints()`. Note: `DrainPoints()` clears this flag. |
| `DebugInfo` | `string` | Diagnostic string showing context configuration (useful for troubleshooting). |
| `DiagnosticLogPath` | `string` (static) | Path to the log file in `%TEMP%`. Contains detailed context dumps from open/close operations. |

## Two modes

### System mode (`digitizerMode: false`)

- `pkX/pkY` are physical screen pixels (integer precision)
- `DesktopX/Y` are the same values cast to `double`
- `RawX/Y` are the same values
- Pen drives the system cursor
- Good for general pen input, UI interaction

### Digitizer hi-res mode (`digitizerMode: true`)

- `pkX/pkY` are tablet-native units (e.g., 0–52,600 for a 5280 LPI tablet)
- `DesktopX/Y` are computed via `ScaleAxis` from the system mapping — physical screen pixels with **sub-pixel precision** (`double`)
- `RawX/Y` are the tablet-native integers
- Pen still drives the system cursor (`CXO_SYSTEM` required by Wacom driver)
- ~7x more resolution per axis than system mode
- Good for drawing apps that need maximum precision

Both modes produce `DesktopX/Y` in the same coordinate space (physical screen pixels). The consumer's desktop → canvas conversion code is identical for both modes.

## Consumer responsibilities

### 1. Convert desktop → canvas coordinates

`DesktopX/Y` are in physical screen pixel space. The consumer must convert to canvas-local coordinates using framework-specific methods.

**WinUI 3:**
```csharp
// Must be called from Per-Monitor V2 thread context for physical pixels.
var clientOrigin = new POINT { X = 0, Y = 0 };
uint dpiX = 96, dpiY = 96;
var old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
try
{
    ClientToScreen(hwnd, ref clientOrigin);
    var hMon = MonitorFromWindow(hwnd, 2);
    if (hMon != IntPtr.Zero)
        GetDpiForMonitor(hMon, 0, out dpiX, out dpiY);
}
finally { SetThreadDpiAwarenessContext(old); }

var canvasPos = canvas.TransformToVisual(null).TransformPoint(new Point(0, 0));
var canvasPoint = new Point(
    (pt.DesktopX - clientOrigin.X) * (96.0 / dpiX) - canvasPos.X,
    (pt.DesktopY - clientOrigin.Y) * (96.0 / dpiY) - canvasPos.Y);
```

**WinForms:**
```csharp
// PointToClient handles DPI conversion automatically in .NET 10 (Per-Monitor V2).
var clientPoint = panel.PointToClient(
    new Point((int)Math.Round(pt.DesktopX), (int)Math.Round(pt.DesktopY)));
```

**WPF:**
```csharp
// WPF is Per-Monitor V2 aware. Use PointFromScreen on the canvas.
var screenPoint = new System.Windows.Point(pt.DesktopX, pt.DesktopY);
var canvasPoint = canvas.PointFromScreen(screenPoint);
```

### 2. Track stroke continuity

`WintabSession` does not track previous points or stroke state. The consumer must:

```csharp
Point? lastPoint = null;

foreach (var pt in session.DrainPoints())
{
    var canvasPoint = ConvertToCanvas(pt);

    // Bounds check
    if (OutsideCanvas(canvasPoint))
    {
        lastPoint = null;  // reset — next point starts a new stroke
        continue;
    }

    // Draw a segment if we have a previous point and pressure > 0
    if (lastPoint is { } from && pt.Pressure > 0)
    {
        DrawLine(from, canvasPoint, ComputeWidth(pt));
    }

    lastPoint = canvasPoint;
}
```

### 3. Implement brush logic

The session provides raw pressure — the consumer decides what to do with it:

```csharp
// Simple pressure → width
float width = (float)pt.Pressure / session.MaxPressure * brushSize + 0.5f;

// Opacity from pressure
float opacity = (float)pt.Pressure / session.MaxPressure;

// Calligraphy angle from azimuth
double angle = pt.Azimuth / 10.0;  // convert tenths of degrees to degrees

// Eraser detection
bool isEraser = pt.IsEraser;  // true when cursor type == PenCursorType.Eraser
```

### 4. Update telemetry display

Points arrive during hover (pressure=0) and drawing (pressure>0). To show live pen data:

```csharp
// On the render timer, after DrainPoints/DrainSegments:
var points = session.DrainPoints();
if (points.Length > 0)
{
    var latest = points[^1];  // last point in the batch
    UpdatePenDataDisplay(latest);
}
```

### 5. Handle mode switching

Stop and restart the session when the user switches modes:

```csharp
void SwitchMode(WintabResolution resolution)
{
    session.Stop();
    lastPoint = null;  // reset stroke state
    session.Start(resolution);
}
```

The canvas, strokes, and brush state are not affected by the switch.

## Threading model

```
Wintab background thread (200+ Hz):     UI thread (60 fps render timer):
┌────────────────────────────────┐       ┌─────────────────────────────────┐
│ OnPacket():                    │       │ RenderTick():                   │
│  1. Get packet from Wintab     │       │  1. DrainPoints() — dequeue all │
│  2. Convert to desktop coords  │       │  2. Convert to canvas coords    │
│  3. Enqueue PenPoint           │──────►│  3. Bounds check                │
│                                │       │  4. Feed brush engine           │
│                                │       │  5. Render to canvas            │
│                                │       │  6. Update telemetry display    │
└────────────────────────────────┘       └─────────────────────────────────┘
```

- `DrainPoints()` is safe to call from any thread (uses `ConcurrentQueue`)
- The consumer should call it on the UI thread to avoid cross-thread rendering issues
- One `DrainPoints()` call returns all points accumulated since the last call
- At 200 Hz packets and 60 fps polling, each drain returns ~3-4 points on average

## Diagnostics

If something isn't working, check the log file:

```csharp
string logPath = WintabSession.DiagnosticLogPath;
// Typically: C:\Users\<user>\AppData\Local\Temp\WintabSession.log
```

The log contains:
- Context configuration before and after `Open()` (InOrg/InExt, OutOrg/OutExt, SysOrg/SysExt, Options, Device)
- Whether the hi-res open succeeded or fell back to screen pixels
- The exact error if something failed
- Packet processing errors (if any occur during pen input)

The log file is closed automatically when the session is disposed. You can also call `WintabSession.CloseLog()` explicitly to flush and close it at any time.

Also check `session.DebugInfo` for a one-line summary of the current context configuration.

## Complete wrapper example (pseudocode)

```csharp
class MyAppSessionWrapper : IDisposable
{
    private readonly WintabSession _session = new();
    private CanvasPoint? _lastPoint;

    // App-specific: how to convert desktop → canvas
    private readonly Func<double, double, CanvasPoint> _desktopToCanvas;

    public MyAppSessionWrapper(Func<double, double, CanvasPoint> converter)
    {
        _desktopToCanvas = converter;
    }

    public void Start(bool digitizer) => _session.Start(resolution);
    public void Stop() { _session.Stop(); _lastPoint = null; }
    public void Dispose() => _session.Dispose();

    public int MaxPressure => _session.MaxPressure;
    public bool IsDigitizerMode => _session.IsDigitizerMode;

    // Called from render timer
    public void ProcessPenInput(Action<CanvasPoint, CanvasPoint, PenPoint> onStroke,
                                 Action<PenPoint, CanvasPoint>? onTelemetry)
    {
        var points = _session.DrainPoints();

        foreach (var pt in points)
        {
            var cp = _desktopToCanvas(pt.DesktopX, pt.DesktopY);

            if (_lastPoint is { } from && pt.Pressure > 0)
                onStroke(from, cp, pt);

            _lastPoint = cp;
        }

        if (points.Length > 0 && onTelemetry != null)
        {
            var last = points[^1];
            onTelemetry(last, _desktopToCanvas(last.DesktopX, last.DesktopY));
        }
    }
}
```

## Reference implementations

- **WinUI 3:** `Scribble.WinUI/WintabSessionWinUI3.cs` — desktop → canvas DIPs via `ClientToScreen` + DPI, canvas bounds checking, `StrokeSegment` with `BrushSize`
- **WinForms:** `LegacyPenTestAppWinForms/WintabSessionWinForms.cs` — desktop → panel via `PointToClient`, `StrokeSegment` with fixed width scale
