# How to Use WintabDN

A guide for developers building applications with the WintabDN library.

## Prerequisites

- A Wacom tablet driver installed and a supported tablet attached
- .NET 10 SDK
- Reference the `WintabDN` project or its output `WintabDN.dll`

## Quick Start

```csharp
using WintabDN;

// 1. Check that Wintab is available
if (!WintabInfo.IsWintabAvailable())
{
    Console.WriteLine("Wintab not found. Is the tablet driver installed?");
    return;
}

// 2. Get a default context
WintabContext ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
ctx.Options |= (uint)ContextOptions.CXO_SYSTEM; // pen controls system cursor

// 3. Open the context
if (!ctx.Open())
{
    Console.WriteLine("Failed to open Wintab context.");
    return;
}

// 4. Create a data object and register for packet events
var wtData = new WintabData(ctx);
wtData.AddPacketHandler(OnPacket);

// 5. Handle packets
void OnPacket(object sender, MessageReceivedEventArgs e)
{
    uint pktID = (uint)e.Message.WParam;
    WintabPacket pkt = wtData.GetPacket((uint)e.Message.LParam, pktID);

    if (pkt.pkContext != 0)
    {
        Console.WriteLine($"X={pkt.pkX} Y={pkt.pkY} Pressure={pkt.pkNormalPressure}");
    }
}

// 6. When done, clean up
wtData.RemovePacketHandler(OnPacket);
ctx.Close();
```

Both `WintabContext` and `WintabData` implement `IDisposable`, so you can also use `using` blocks:

```csharp
using var ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
ctx.Open();

using var wtData = new WintabData(ctx);
wtData.AddPacketHandler(OnPacket);

// ... use the tablet ...
// Dispose() automatically closes the context and removes handlers.
```

## Contexts: System vs Digitizer

WintabDN supports two types of contexts. Both output **physical screen pixel** coordinates, but they differ in whether the pen drives the system cursor.

### System Context

```csharp
var ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
ctx.Options |= (uint)ContextOptions.CXO_SYSTEM;
```

The system context maps tablet input to screen coordinates. The `CXO_SYSTEM` flag makes the pen control the system cursor.

**Packet coordinates:** `pkX` and `pkY` are screen positions in physical pixels.

**Pros:**
- Pen moves the system cursor — intuitive for users
- Coordinates correspond to on-screen positions
- Works naturally with multi-monitor setups and tablet-to-monitor mapping
- Good default choice for most applications

**Cons:**
- Precision limited by screen resolution (tablet's native resolution is downscaled to screen pixels)
- Requires DPI conversion in Per-Monitor V2 mode (see [DPI Handling](#dpi-handling-system-context) below)
- Drawing area is wherever your window happens to be on screen

**Best for:** General applications, UI interaction, any case where the pen should behave like a mouse.

### Digitizer Context

The digitizer context gives you pen input **without** the `CXO_SYSTEM` flag, meaning the pen does not automatically drive the system cursor. This is useful when you want full control over cursor behaviour, or when you need to distinguish pen input from mouse input.

#### The multi-monitor trap: don't set OutExtX/Y to canvas dimensions

A common approach is to map the tablet directly to your drawing area:

```csharp
// *** DO NOT DO THIS on multi-monitor setups ***
var ctx = WintabInfo.GetDefaultDigitizingContext(ContextOptions.CXO_MESSAGES);
ctx.OutExtX = canvasWidth;   // broken when tablet is mapped to one monitor
ctx.OutExtY = canvasHeight;
```

This works on single-monitor setups, but **breaks on multi-monitor** because the Wacom driver applies its tablet-to-monitor mapping at a level below the Wintab context settings. If the tablet is mapped to one monitor (a common configuration), the driver clips the output range to that monitor's fraction of the virtual desktop — even though you set `OutExtX` to a custom value. The result: the full tablet surface only produces coordinates in a fraction of your output range, and strokes appear in a "small rectangle" instead of filling the canvas.

The problem scales with the number of monitors:
- 2 monitors side-by-side, tablet mapped to right monitor → strokes cover roughly **half** the canvas width
- 3 monitors (2 top + 1 below) → strokes cover roughly a **quarter** of the canvas

#### Recommended approach: use system context output range

Instead, start from the **system context defaults** (which produce physical screen pixel coordinates) and omit the `CXO_SYSTEM` flag. The Wacom driver's monitor mapping works correctly in screen coordinate space. Your packet handler then converts from physical screen pixels to canvas coordinates — the same conversion used for the system context.

```csharp
// Use system context defaults for the output range (screen coordinates).
// The Wacom driver's tablet-to-monitor mapping works correctly in this space.
var ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
// Do NOT add CXO_SYSTEM — we control the cursor ourselves.

// Flip Y axis: tablet origin is bottom-left, screen is top-left.
if (ctx.OutExtY > 0)
    ctx.OutExtY = -ctx.OutExtY;

ctx.Open();
```

**Packet coordinates:** `pkX` and `pkY` are physical screen pixels — the same as the system context. Convert to canvas coordinates using the same formula (see [DPI Handling](#dpi-handling-system-context) below).

**Pros:**
- Works correctly on multi-monitor setups regardless of tablet-to-monitor mapping
- No `CXO_SYSTEM` — pen does not drive the system cursor
- Same coordinate conversion as the system context — no separate code path needed
- Can switch between system and digitizer mode at runtime without changing conversion logic

**Cons:**
- Same screen-pixel precision as the system context (tablet native resolution is downscaled)
- System cursor does not track the pen (see cursor tracking note below)
- Requires the same DPI conversion as the system context

**Best for:** Applications that need to separate pen tracking from system cursor control, or that want to switch between system and digitizer modes at runtime.

#### High-resolution digitizer mode

The `Scribble.WinUI` sample implements a high-resolution digitizer mode that preserves the tablet's full native precision (~52,600 × ~29,600 units for a 5280 LPI tablet) instead of downscaling to screen pixels (~7,680 × ~3,760). This gives approximately **7x more resolution per axis** than the system context.

The approach:

1. **Read** the system context defaults (`WTI_DEFSYSCTX`) to get the tablet-to-screen mapping: `InOrg/InExt` (tablet native range) and `SysOrg/SysExt` (screen pixel range). This is read via `WTInfoA` — no live system context is opened.

2. **Open** a system context (`WTI_DEFSYSCTX`) with `CXO_SYSTEM` and `CXO_MESSAGES`, but override `OutOrg/OutExt` to match `InOrg/InExt` (the tablet's native range). This makes `pkX/pkY` arrive in tablet-native units instead of screen pixels.

3. **Convert** tablet-native `pkX/pkY` to desktop coordinates using the system mapping:

```csharp
// Normalize tablet position to 0..1
double nx = (pkX - sys.InOrgX) / (double)sys.InExtX;
// Map to screen coordinates (double precision — sub-pixel)
double desktopX = sys.SysOrgX + nx * sys.SysExtX;
```

4. **Convert** desktop coordinates to canvas-local DIPs using the same `ClientToScreen` + DPI formula as system mode.

**Why `CXO_SYSTEM` is required:** The Wacom driver does not deliver `WT_PACKET` messages from contexts without `CXO_SYSTEM`. Removing it causes the context to open successfully but produce no packets. This means the pen still drives the system cursor in both modes — the difference is the precision of `pkX/pkY` values in the packets.

**Why Y must be inverted in the mapping:** The tablet's native coordinate system has Y increasing upward (origin bottom-left), while screen coordinates have Y increasing downward (origin top-left). The system context normally handles this by negating `OutExtY`. When we override `OutExt` to tablet-native range, `OutExtY` is positive (tablet convention). The `ScaleAxis` conversion compensates by using a negative `SysExtY`, which triggers the opposite-sign inversion path in the axis scaling equation.

**Multi-monitor:** This approach works correctly on multi-monitor setups because the conversion goes through the system context's `SysOrg/SysExt` mapping, which reflects the driver's current tablet-to-monitor assignment regardless of how many monitors are connected.

#### Cursor tracking

Both system and digitizer hi-res modes use `CXO_SYSTEM`, so the pen drives the system cursor automatically in both modes. The Wacom driver requires `CXO_SYSTEM` for `WT_PACKET` delivery — without it, the context opens but produces no packets.

This means there is no "cursor-free" digitizer mode available through the Wacom driver's Wintab implementation. If you need pen input without cursor movement, you would need to use a different input API (e.g., Windows Pointer Input or Windows Ink).

#### Traditional digitizer context (single-monitor only)

If you are certain your application will only run on a single-monitor setup, you can use the traditional approach of mapping the tablet directly to your drawing area via `GetDefaultDigitizingContext`. This gives you the tablet's full native resolution (20,000+ units vs ~2,000 screen pixels) and avoids DPI conversion. See the WinForms scribble example at the end of this document for this approach.

**Warning:** Do not add `CXO_SYSTEM` to a `GetDefaultDigitizingContext` context. This flag overrides the custom output range with system cursor screen coordinates, breaking the digitizer coordinate mapping.

### Choosing a Context

| Scenario | Recommended Context | Notes |
|---|---|---|
| General pen input, UI interaction | System | Screen-pixel precision, simplest setup |
| Pen as mouse replacement | System | Cursor tracking is automatic |
| Drawing app, maximum precision | Digitizer (hi-res) | ~7x more resolution per axis than screen pixels; works on multi-monitor |
| Single-monitor, custom output range | Digitizer (traditional) | Full tablet resolution via custom OutExt; breaks on multi-monitor |

You can switch between contexts at runtime — close the current one and open a new one.

**Summary of the three approaches:**

| Approach | Precision | Multi-monitor | Cursor tracking |
|---|---|---|---|
| **System** (`CXO_SYSTEM`, screen pixel OutExt) | Screen pixels (~7,680) | Works | Automatic |
| **Digitizer hi-res** (`CXO_SYSTEM`, tablet-native OutExt) | Tablet native (~52,600) | Works | Automatic |
| **Digitizer traditional** (custom OutExt, no `CXO_SYSTEM`) | Tablet native | Broken | None |

Note: Both System and Digitizer hi-res require `CXO_SYSTEM` — the Wacom driver does not deliver `WT_PACKET` messages without it. The difference is the `OutExt` range: screen pixels vs tablet-native units.

## The WintabPacket

Every packet contains these fields:

| Field | Type | Description |
|---|---|---|
| `pkContext` | `HCTX` | Context that generated the event (0 if invalid) |
| `pkStatus` | `UInt32` | Status flags (proximity, queue errors, etc.) |
| `pkTime` | `UInt32` | Timestamp (absolute mode) or elapsed ms (relative) |
| `pkChanged` | `WTPKT` | Which fields changed since last packet |
| `pkSerialNumber` | `UInt32` | Sequential packet identifier |
| `pkCursor` | `UInt32` | Cursor type (pen, eraser, puck) |
| `pkButtons` | `UInt32` | Button state (absolute mode) or button event (relative) |
| `pkX` | `Int32` | X position |
| `pkY` | `Int32` | Y position |
| `pkZ` | `Int32` | Z position (height above tablet) |
| `pkNormalPressure` | `UInt32` | Pen tip pressure |
| `pkTangentPressure` | `UInt32` | Barrel pressure (airbrush) |
| `pkOrientation` | `WTOrientation` | Pen tilt and twist |

### Orientation (Tilt)

`WTOrientation` contains:

- `orAzimuth` - clockwise rotation around the Z axis (compass direction the pen points)
- `orAltitude` - angle between pen and tablet surface (90 = perpendicular)
- `orTwist` - rotation of the pen around its own axis (barrel rotation)

Not all tablets support tilt. Check with:

```csharp
var axes = WintabInfo.GetDeviceOrientation(out bool tiltSupported);
```

### Pressure

Pressure values range from 0 to a tablet-specific maximum. Query the maximum:

```csharp
int maxPressure = WintabInfo.GetMaxPressure();          // normal (tip)
int maxTangent  = WintabInfo.GetMaxPressure(false);     // tangential (barrel)
```

To normalize to 0.0-1.0:

```csharp
float normalized = (float)pkt.pkNormalPressure / maxPressure;
```

## DPI Handling (System Context)

Wintab system context coordinates are always in **physical screen pixels**, regardless of your application's DPI awareness mode. This means they will not match your UI framework's coordinate system on high-DPI displays unless you convert them.

### WinForms (.NET 10, PerMonitorV2)

.NET 10 WinForms runs with PerMonitorV2 DPI awareness. `Control.PointToClient()` handles the conversion from physical screen pixels to DPI-scaled **panel-local logical coordinates** automatically. The returned `Point` is relative to the panel's top-left corner — the WinForms equivalent of "canvas-local DIPs" in WinUI 3. Pass raw Wintab coordinates directly:

```csharp
// Wintab pkX/pkY are physical screen pixels - PointToClient handles DPI scaling
// and converts to panel-local logical coordinates in one call.
Point clientPoint = myPanel.PointToClient(new Point(pkt.pkX, pkt.pkY));
// clientPoint is now (0,0) at the panel's top-left corner, ready for drawing.
```

Do **not** manually scale the coordinates before calling `PointToClient()` — this would double-correct and produce wrong results.

### WinUI 3

#### Unpackaged apps require a DPI manifest

WinUI 3 (Windows App SDK) **packaged apps** (MSIX) get Per-Monitor V2 DPI awareness automatically. However, **unpackaged apps** (`WindowsPackageType=None`) do not — Windows defaults to basic/system DPI awareness, which means it applies bitmap scaling at non-100% display scales. The app will appear to work but the entire UI renders blurry, especially small text and crisp elements like toolbar ribbons.

The fix is to declare DPI awareness in `app.manifest`:

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  </windowsSettings>
</application>
```

- `dpiAwareness: PerMonitorV2` — the modern setting (Windows 10 1703+) that tells WinUI 3 the app handles per-monitor DPI changes natively, so Windows won't apply bitmap scaling.
- `dpiAware: true/pm` — fallback for older Windows 10 builds that don't understand the `dpiAwareness` element.

The manifest must also include the Windows 10 `supportedOS` GUID, or the DPI settings are ignored.

This is easy to miss because the app runs fine without it — just blurry. It's the same class of problem as native C++ apps needing `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` (see [Native C++ Gotchas](#3-dpi-awareness-is-required-for-correct-coordinates)).

#### Coordinate spaces

WinUI 3 with Per-Monitor V2 creates a three-way coordinate space interaction that you must navigate carefully:

| Coordinate space | Who uses it | Example (at 225% scaling, 3840px display) |
|---|---|---|
| **Physical screen pixels** | Wintab `pkX`/`pkY`, Win32 APIs under Per-Monitor V2 | 0–3840 |
| **DPI-virtualized screen** | Win32 APIs under DPI-unaware thread context | 0–1707 |
| **WinUI 3 DIPs** | XAML layout (`TransformToVisual`, `ActualWidth`, Canvas) | 0–1707 |

#### What are DIPs?

A **DIP** (Device-Independent Pixel) is WinUI 3's unit of measurement. 1 DIP = 1/96 of an inch, regardless of the physical display's pixel density. On a 96 DPI display, 1 DIP = 1 physical pixel. On a 225% scaled display (216 DPI), 1 DIP = 2.25 physical pixels.

All XAML layout values — `ActualWidth`, `TransformToVisual`, `Canvas` positions, `Line` coordinates — are in DIPs. When you place a `Line` element at `X1=100`, it appears 100 DIPs from the canvas origin, which is 225 physical pixels on a 225% display. WinUI 3 handles the DIP-to-physical scaling internally during rendering.

#### What are "canvas-local DIPs"?

Throughout this codebase, **canvas-local DIPs** (or just "canvas DIPs") means a DIP coordinate measured from the **top-left corner of the drawing canvas**, not from the screen origin or window origin. This is the coordinate space that XAML elements on the `Canvas` panel use:

- **(0, 0)** = top-left corner of the canvas
- **(canvasWidth, canvasHeight)** = bottom-right corner
- Negative values or values beyond the canvas size = outside the visible drawing area

This is distinct from:
- **Screen DIPs** — measured from the top-left of the virtual desktop (what you'd get from `pkX × (96/DPI)`)
- **Client DIPs** — measured from the top-left of the window's client area (includes the toolbar above the canvas)

The conversion from Wintab's physical screen pixels to canvas-local DIPs is:

```
canvasPoint = (pkXY − clientOrigin_physical) × (96 / DPI) − canvasOffset_dips
```

Where `canvasOffset_dips` is the canvas's DIP position within the client area (e.g., the height of the toolbar above it). This formula applies to **both** system and digitizer contexts (since both output physical screen pixels — see [Digitizer Context](#digitizer-context) above for why).

#### Step-by-step breakdown

```
pkXY                          Wintab physical screen pixels (screen origin)
 − clientOrigin_physical      → physical pixels from window's client area origin
 × (96 / DPI)                 → client-relative DIPs
 − canvasOffset_dips          → canvas-local DIPs (subtract toolbar height)
 ─────────────────────────
 = canvas-local DIPs          ready for XAML Line/Path coordinates on the Canvas
```

**Why subtract clientOrigin first, then scale?** The multiplication `× (96/DPI)` converts a *distance in physical pixels* to a *distance in DIPs*. If you scale `pkX` directly (`pkX × 96/DPI`) you get a DIP-based screen position, but then you'd need `clientOrigin` in DIPs too. Getting `clientOrigin` in DIPs requires calling `ClientToScreen` from a DPI-unaware thread — which works, but mixes two different coordinate queries that can become inconsistent. The subtract-then-scale approach keeps everything in physical pixels until the single conversion step, which is simpler and more robust.

#### The critical pitfall: mixing coordinate spaces

`clientOrigin` and `pkXY` **must** be in the same coordinate space. If you call `ClientToScreen` without ensuring Per-Monitor V2 thread context, Windows may return DPI-virtualized values instead of physical pixels. Subtracting a virtualized origin from physical Wintab coordinates produces a position error that **grows with the window's distance from the screen origin**:

```
error = clientOrigin × (1 − 96/DPI)
```

At 225% scaling with the window at physical X=4589:
- Virtualized clientOrigin.X = 4589 × (96/216) = 2039
- Physical pkX at the same point = 4589
- Error = 4589 − 2039 = **2550 pixels** of drift

The stroke appears correct near the screen origin but diverges dramatically as you draw farther across the canvas. On a multi-monitor setup where the window is on the second monitor (large X values), this error is immediately visible.

#### Correct implementation

```csharp
using System.Runtime.InteropServices;

// P/Invoke declarations
[DllImport("user32.dll")]
static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

[DllImport("user32.dll")]
static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

[DllImport("shcore.dll")]
static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType,
    out uint dpiX, out uint dpiY);

[DllImport("user32.dll")]
static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X; public int Y; }

// In your packet handler (works for both system and digitizer contexts):
var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

// 1. Client origin in PHYSICAL screen pixels + real monitor DPI.
//    Both queries MUST be inside a Per-Monitor V2 thread context to
//    guarantee physical (non-virtualized) values.
var clientOrigin = new POINT { X = 0, Y = 0 };
uint dpiX = 96, dpiY = 96;
var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
try
{
    ClientToScreen(hwnd, ref clientOrigin);
    var hMon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
    if (hMon != IntPtr.Zero)
        GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out dpiX, out dpiY);
}
finally { SetThreadDpiAwarenessContext(oldCtx); }

// 2. Canvas offset from the XAML content root, in DIPs.
//    TransformToVisual always returns DIPs regardless of thread DPI context.
var canvasOffset = myCanvas.TransformToVisual(null).TransformPoint(new Point(0, 0));

// 3. Convert the pen's physical-pixel distance from the client origin
//    to DIPs, then subtract the canvas's DIP offset.
Point canvasPoint = new Point(
    (pkt.pkX - clientOrigin.X) * (96.0 / dpiX) - canvasOffset.X,
    (pkt.pkY - clientOrigin.Y) * (96.0 / dpiY) - canvasOffset.Y);
```

#### Why this is different from WinForms

WinForms runs as Per-Monitor V2 DPI aware, and `PointToClient()` converts physical screen pixels to DPI-scaled logical client coordinates in one call — you pass raw Wintab coordinates and get back correctly scaled positions.

WinUI 3 has no equivalent single-call API. Its XAML layout engine works in DIPs, while Wintab reports physical pixels, and Win32 APIs may return either physical or virtualized values depending on the calling thread's DPI context. You must bridge these coordinate spaces manually by:

1. Explicitly requesting physical values (via `SetThreadDpiAwarenessContext`)
2. Computing the physical-pixel offset between pen and client origin
3. Converting to DIPs with the real DPI scale factor
4. Subtracting the XAML-reported canvas position (already in DIPs)

#### WintabSession classes: encapsulating the conversion

Both sample apps encapsulate all Wintab logic in a **session class** that keeps coordinate conversion, context lifecycle, and threading concerns out of the UI code. The two implementations share the same architecture but differ in how they handle DPI.

See the [Session Architecture](#session-architecture) section below for the full design.

### WintabDpiHelper (other frameworks)

For frameworks where the process is Per-Monitor DPI aware (WPF, raw Win32), WintabDN provides `WintabDpiHelper` to convert physical pixels to logical coordinates:

```csharp
// Convert Wintab physical pixels to DPI-scaled logical screen coordinates
var logical = WintabDpiHelper.PhysicalToLogical(pkt.pkX, pkt.pkY);
```

You can also query the DPI scale factor for a screen location:

```csharp
var (scaleX, scaleY) = WintabDpiHelper.GetDpiScale(pkt.pkX, pkt.pkY);
// scaleX = 2.25 means 225% scaling
```

**Note:** `WintabDpiHelper` uses `GetDpiForMonitor` which only returns the real DPI when called from a DPI-aware thread. WinUI 3 apps are typically Per-Monitor V2 aware (set by the Windows App SDK at startup), so `WintabDpiHelper` will return correct values. However, the WinUI 3 section above shows the full manual approach because WinUI 3 also requires converting between physical pixels and XAML DIPs — `WintabDpiHelper` only handles the DPI scaling part, not the XAML layout offset.

### Digitizer context

When using the **recommended** digitizer approach (system context output range, no `CXO_SYSTEM`), the DPI conversion is **identical** to the system context — both output physical screen pixels, both use the same `(pkXY − clientOrigin) × (96/DPI) − canvasOffset` formula. See the [WinUI 3 section](#winui-3) above.

When using the **traditional** digitizer approach (custom `OutExtX`/`OutExtY` mapped to canvas dimensions), no DPI conversion is needed — you define the output coordinate space, so there's no physical-vs-logical mismatch. However, this approach only works reliably on single-monitor setups (see [the multi-monitor trap](#the-multi-monitor-trap-dont-set-outextxy-to-canvas-dimensions)).

## Session Architecture

Both sample apps (`Scribble.WinUI` and `Scribble.WinForms`) use a **session class** that encapsulates all Wintab concerns: context creation, packet handling, coordinate conversion, and cursor synchronisation. The UI code never touches Wintab directly — it creates a session, starts it, and polls for results on a render timer.

### Why a session class?

Without it, your main window or form ends up with:
- P/Invoke declarations for DPI queries and cursor positioning
- Wintab context lifecycle management (open, close, wire/unwire handlers)
- Coordinate conversion logic (physical pixels → framework coordinates)
- Threading concerns (packet handler runs at 200+ Hz on a background thread)
- Cursor visibility concerns (WinUI 3 hides the cursor during pen contact)

This mixes low-level Win32/Wintab plumbing with UI framework code. The session class provides a clean boundary: Wintab complexity goes in, canvas-ready data comes out.

### The shared pattern

Both session implementations follow the same design:

```
┌─────────────────────────────────────────────────────────────────┐
│                        WintabSession                            │
│                                                                 │
│  Owns:                                                          │
│   • WintabContext + WintabData lifecycle                      │
│   • Packet handler (background thread, 200+ Hz)                 │
│   • Coordinate conversion (physical screen → canvas)            │
│                                                                 │
│  Input:                                                         │
│   • ICanvasInfo — canvas position/size, updated by UI thread    │
│                                                                 │
│  Output:                                                        │
│   • ConcurrentQueue<StrokeSegment> — canvas-ready line data     │
│   • PenTelemetry — latest packet + canvas point for display     │
│                                                                 │
│  API:                                                           │
│   • Start(digitizerMode) → status string                        │
│   • Stop() / Dispose()                                          │
│   • DrainSegments() — returns all queued segments               │
│   • GetTelemetry() — returns latest packet data                 │
│   • HasNewData — volatile flag for render-timer gating           │
│                                                                 │
└──────────────────────────────────┬──────────────────────────────┘
                                   │
                    ┌──────────────┴──────────────┐
                    │     UI (MainWindow/Form)     │
                    │                              │
                    │  60 fps render timer:         │
                    │   1. session.DrainSegments()  │
                    │   2. Create Line / DrawLine   │
                    │   3. Flush to canvas / bitmap │
                    │   4. session.GetTelemetry()   │
                    │   5. Update status display    │
                    │                              │
                    └──────────────────────────────┘
```

### ICanvasInfo: decoupling from the framework

The session never references framework-specific types (no `Canvas`, no `Control`, no `TransformToVisual`). Instead, it receives an `ICanvasInfo` interface that the UI implements:

**WinUI 3** — provides canvas width, height, and position in the window (DIPs):

```csharp
public interface ICanvasInfo
{
    double Width { get; }
    double Height { get; }
    Point PositionInWindow { get; }  // canvas offset from client origin, in DIPs
}
```

The UI thread updates these values on `SizeChanged` and on the render timer tick (to track window moves). The session reads them from the background packet handler — one-frame staleness is acceptable for a drawing app.

**WinForms** — only needs `PointToClient` (DPI conversion is built in):

```csharp
public interface ICanvasInfo
{
    Point PointToClient(Point screenPhysicalPixels);
}
```

A one-line adapter wraps the drawing panel:

```csharp
class PanelCanvasInfo(Control panel) : ICanvasInfo
{
    public Point PointToClient(Point p) => panel.PointToClient(p);
}
```

### StrokeSegment: framework-neutral output

The session doesn't create framework-specific drawing objects (`Line`, `Pen`, etc.). Instead, it produces `StrokeSegment` records — plain data with two points and a line width:

```csharp
public readonly record struct StrokeSegment(Point From, Point To, float Width);
```

The UI thread creates the appropriate drawing primitives:
- **WinUI 3:** `new Line { X1=seg.From.X, ... }` → `Canvas.QueueLine()`
- **WinForms:** `graphics.DrawLine(pen, seg.From, seg.To)` on a backing `Bitmap`

This is also a correctness improvement: in WinUI 3, `Line` is a `DependencyObject` that should be created on the UI thread. The session pattern naturally ensures this.

### Threading model

```
Background thread (200+ Hz):          UI thread (60 fps timer):
┌──────────────────────────┐          ┌──────────────────────────┐
│  OnPacket():             │          │  RenderTimer_Tick():     │
│   1. GetPacket()         │          │   1. DrainSegments()     │
│   2. Convert to canvas   │          │      (dequeue all)       │
│   3. Enqueue segment     │───────►  │   2. Create Line objects │
│   4. Update telemetry    │          │   3. Flush to canvas     │
│   5. Set HasNewData flag │          │   4. GetTelemetry()      │
│                          │          │   5. Update status text  │
└──────────────────────────┘          └──────────────────────────┘
```

- **StrokeSegments** use a `ConcurrentQueue<T>` — lock-free enqueue/dequeue, no contention.
- **PenTelemetry** (latest packet + canvas point) is protected by a `lock` since `WintabPacket` is a multi-field struct that can't be read atomically. The lock is held briefly on each packet and each render tick — no measurable overhead at 200 Hz / 60 fps.
- **HasNewData** is a `volatile bool` that gates the telemetry update. Without it, the render timer would reformat the status string 60 times per second even when the pen is idle.

### How the two sessions differ

| Concern | WintabSessionWinUI3 | WintabSessionWinForms |
|---|---|---|
| **DPI conversion** | Manual: P/Invoke `ClientToScreen` + `GetDpiForMonitor` in Per-Monitor V2 context, then `(pkXY − origin) × (96/DPI) − canvasOffset` | Automatic: `Control.PointToClient()` handles everything |
| **P/Invoke declarations** | 4 imports (`ClientToScreen`, `SetThreadDpiAwarenessContext`, `MonitorFromWindow`, `GetDpiForMonitor`) | None needed |
| **Cursor in digitizer mode** | Not tracked — WinUI 3 hides cursor during pen contact; user sees position through ink strokes | Automatic — Wacom driver moves cursor via standard Windows input pipeline |
| **ICanvasInfo** | Width, Height, PositionInWindow (all DIPs) | PointToClient(Point) only |
| **StrokeSegment Point type** | `Windows.Foundation.Point` (double) | `System.Drawing.Point` (int) |
| **Context creation** | Identical: `GetDefaultSystemContext` without `CXO_SYSTEM` for digitizer, with `CXO_SYSTEM` for system | Identical |
| **Y-axis flip** | Identical: negate `OutExtY` | Identical |
| **Digitizer precision** | Tablet-native (~52,600 units) — ~7x more per axis than screen pixels | Screen pixels via `PointToClient` (WinForms has no tablet-native mode yet) |

### Context creation: unified for both modes

Both sessions create contexts the same way:

```csharp
// System mode: pen drives cursor
var ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
ctx.Options |= (uint)ContextOptions.CXO_SYSTEM;

// Digitizer mode: same output range, no CXO_SYSTEM
var ctx = WintabInfo.GetDefaultSystemContext(ContextOptions.CXO_MESSAGES);
// (do NOT add CXO_SYSTEM)
```

Both use `GetDefaultSystemContext` — never `GetDefaultDigitizingContext` — so the output range is always physical screen pixels. The Wacom driver's tablet-to-monitor mapping works correctly in this coordinate space regardless of how many monitors are connected.

The only difference between modes is the `CXO_SYSTEM` flag: present = pen drives cursor, absent = application controls cursor.

### Stroke rendering pipeline

The session and UI work together to turn pen input into visible ink:

```
Wintab driver (200+ Hz)          Session (background thread)
┌─────────────────────┐          ┌──────────────────────────────────────┐
│ WM_PACKET message   │────────► │ OnPacket():                          │
│  pkX, pkY, pressure │          │  1. Convert physical → canvas DIPs   │
│                     │          │  2. If pressure > 0 and has previous │
│                     │          │     point, create StrokeSegment:     │
│                     │          │       From = previous point          │
│                     │          │       To   = current point           │
│                     │          │       Width = pressure × BrushSize   │
│                     │          │  3. Enqueue to ConcurrentQueue       │
└─────────────────────┘          └──────────────┬───────────────────────┘
                                                │
                                 UI thread (60 fps render timer)
                                 ┌──────────────┴───────────────────────┐
                                 │ RenderTimer_Tick():                   │
                                 │  1. DrainSegments() — dequeue all     │
                                 │  2. For each segment, create a XAML   │
                                 │     Line element:                     │
                                 │       X1/Y1 = From, X2/Y2 = To       │
                                 │       StrokeThickness = Width         │
                                 │       Round end caps for smooth joins │
                                 │  3. Add to DrawingCanvas              │
                                 │  4. Flush (commit to visual tree)     │
                                 └──────────────────────────────────────┘
```

**What a stroke looks like in the visual tree:** Each stroke is a chain of individual XAML `Line` elements, one per packet. At 200+ Hz, consecutive segments are typically 0.5–2 DIPs long, so the chain appears as a smooth curve. This is not a single `Polyline` or `PathGeometry` — each segment is an independent element in `Canvas.Children`.

**Stroke width formula:**

```
width = (pressure / maxPressure) × BrushSize + 0.5
```

- `pressure` — raw `pkNormalPressure` from the packet (0 to `maxPressure`)
- `maxPressure` — queried once at session creation via `WintabInfo.GetMaxPressure()`
- `BrushSize` — user-controlled slider (1–500 px), synced from the toolbar on each render tick
- `0.5` — minimum hairline width so near-zero pressure is still visible

**When segments are NOT produced:**

- `pressure == 0` (pen hovering) — no segment, but the position is tracked so the next stroke starts from the correct point
- Pen outside canvas bounds — no segment, position tracking is reset so re-entering the canvas starts a fresh stroke (no line from the outside edge)
- No previous point (`_hasLastPoint == false`) — first packet after pen-down or re-entry, position recorded but no segment yet

**Extending for different brush types:**

The current implementation uses a single `Line` per segment, which gives a basic pressure-sensitive round brush. To support other brush styles:

| Brush type | Approach |
|---|---|
| **Color / opacity** | Add fields to `StrokeSegment`; use them when creating the `Line` (set `Stroke` brush, `Opacity`) |
| **Calligraphy** | Use pen tilt (`orAzimuth`/`orAltitude`) to vary width or angle; create rotated `Rectangle` elements instead of `Line` |
| **Dab-based (Photoshop-style)** | Stamp `Ellipse` or `Image` elements at intervals along the segment path; control spacing and rotation from tilt/twist |
| **Airbrush / soft brush** | Use radial gradient `Ellipse` elements with varying opacity based on pressure |
| **High-performance** | Replace XAML elements with Win2D `CanvasDrawingSession` for GPU-accelerated rendering; the session's `StrokeSegment` queue feeds a Win2D draw loop instead of creating XAML objects |

For any approach beyond simple lines, consider adding `Pressure` (normalized 0–1), `Azimuth`, `Altitude`, and `Twist` fields to `StrokeSegment` so the UI thread has the full pen state available for rendering decisions.

## Logging

WintabDN has an optional logging system. Set `WintabLog.Logger` to receive diagnostic messages from the library (context open/close, handler registration, packet operations):

```csharp
public class MyLogger : IWintabLog
{
    public void Info(string message)  => Debug.WriteLine($"[Wintab] {message}");
    public void Warn(string message)  => Debug.WriteLine($"[Wintab WARN] {message}");
    public void Error(string message, Exception ex = null) =>
        Debug.WriteLine($"[Wintab ERROR] {message} {ex}");
}

// Enable logging
WintabLog.Logger = new MyLogger();

// Disable logging
WintabLog.Logger = null;
```

If no logger is set, all messages are silently dropped (zero overhead).

## Error Handling

WintabDN throws typed exceptions. Catch specific ones or the base `WintabException`:

```csharp
try
{
    ctx.Close();
}
catch (WintabContextException ex)
{
    // Context was invalid (already closed, never opened, etc.)
}

try
{
    var pkt = wtData.GetPacket(hCtx, pktID);
}
catch (WintabDataException ex)
{
    // Packet retrieval failed (invalid pktID, queue error, etc.)
}

// Or catch all Wintab errors
catch (WintabException ex)
{
    // Any library error
}
```

Exception hierarchy:

```
WintabException                  (base - general Wintab errors)
  WintabContextException         (context open/close/enable failures)
  WintabDataException            (packet/queue operation failures)
  WintabExtensionException       (extension property get/set failures)
```

Standard `ArgumentException` and `ArgumentNullException` are used for invalid arguments (null context, zero packet ID, etc.).

## Device Information

Query tablet capabilities before opening a context:

```csharp
// What device is connected?
string name = WintabInfo.GetDeviceInfo();
uint count = WintabInfo.GetNumberOfDevices();

// What are the axis ranges?
WintabAxis xAxis = WintabInfo.GetDeviceAxis(-1, AxisDimension.AXIS_X);
// xAxis.axMin, xAxis.axMax, xAxis.axResolution

// Pressure range
int maxPressure = WintabInfo.GetMaxPressure();

// Tilt support
var orientation = WintabInfo.GetDeviceOrientation(out bool hasTilt);

// Stylus info
bool active = WintabInfo.IsStylusActive();
string penName = WintabInfo.GetStylusName(CursorNameIndex.CSR_NAME_PRESSURE_STYLUS);
```

## Extension Controls (ExpressKeys, Touch Rings, Touch Strips)

For tablets with hardware controls, use the extension API:

```csharp
// Check if extensions are supported
uint expKeyMask = WintabExtensions.GetWTExtensionMask(ExtensionTag.WTX_EXPKEYS2);
uint ringMask   = WintabExtensions.GetWTExtensionMask(ExtensionTag.WTX_TOUCHRING);
uint stripMask  = WintabExtensions.GetWTExtensionMask(ExtensionTag.WTX_TOUCHSTRIP);

// Open an extended context and register for extension packets
var ctx = WintabInfo.GetDefaultDigitizingContext(ContextOptions.CXO_MESSAGES);
ctx.Open();

var wtData = new WintabData(ctx);
wtData.AddPacketHandler(OnExtPacket);

void OnExtPacket(object sender, MessageReceivedEventArgs e)
{
    uint pktID = (uint)e.Message.WParam;
    var pkt = wtData.GetPacketExt((uint)e.Message.LParam, pktID);

    // Express Key data
    byte tabletIdx = pkt.pkExpKey.nTablet;
    byte controlIdx = pkt.pkExpKey.nControl;
    bool pressed = pkt.pkExpKey.nState != 0;

    // Touch Ring data
    byte ringMode = pkt.pkTouchRing.nMode;
    uint ringPos = pkt.pkTouchRing.nPosition;
}
```

See the `ExtensionTestApp` project for a complete working example.

## Performance Tips

### Throttle Screen Updates

Pen tablets report packets at 200+ Hz. Don't call `Control.Invalidate()` or update UI labels on every packet. Instead:

```csharp
// Draw to a backing bitmap per-packet (fast)
graphics.DrawLine(pen, currentPoint, lastPoint);
dirty = true;

// Repaint the screen on a timer at ~60fps
timer.Tick += (s, e) =>
{
    if (dirty)
    {
        dirty = false;
        panel.Invalidate();
        statusLabel.Text = $"Pressure: {lastPressure}";
    }
};
```

### Use Bitmap-Backed Drawing

In .NET 10, `Control.CreateGraphics()` is ephemeral - anything drawn via it is erased on the next paint cycle. Instead, draw to a `Bitmap` and render it in the `Paint` event:

```csharp
var bitmap = new Bitmap(panel.Width, panel.Height);
var graphics = Graphics.FromImage(bitmap);

panel.Paint += (s, e) => e.Graphics.DrawImage(bitmap, Point.Empty);

// In your packet handler, draw to 'graphics' (the bitmap),
// then invalidate the panel to trigger a repaint.
```

### Enable Double-Buffering

To prevent flicker when repainting:

```csharp
typeof(Control)
    .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(panel, true);
```

## Complete Scribble Example (WinForms, single-monitor)

Here's a minimal WinForms scribble using the **traditional** digitizer approach — `OutExtX`/`OutExtY` mapped to the panel dimensions for direct tablet-to-canvas coordinate mapping. This gives maximum tablet precision but only works reliably on **single-monitor** setups (see [the multi-monitor trap](#the-multi-monitor-trap-dont-set-outextxy-to-canvas-dimensions)).

For multi-monitor-safe examples that use the recommended screen-coordinate approach with session classes, see:
- `Scribble.WinUI` — uses `PenSession` + `PenSession.WinUI` with manual DPI conversion
- `Scribble.WinForms` — uses `PenSession` + `PenSession.WinForms` with SkiaSharp rendering

```csharp
public class ScribbleForm : Form
{
    private WintabContext? ctx;
    private WintabData? wtData;
    private Bitmap? bitmap;
    private Graphics? gfx;
    private Point lastPoint = Point.Empty;
    private readonly int maxPressure = WintabInfo.GetMaxPressure();
    private volatile bool dirty;
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 16 };

    public ScribbleForm()
    {
        ClientSize = new Size(800, 600);
        DoubleBuffered = true;
        Paint += (s, e) => { if (bitmap != null) e.Graphics.DrawImage(bitmap, Point.Empty); };
        renderTimer.Tick += (s, e) => { if (dirty) { dirty = false; Invalidate(); } };

        StartScribble();
    }

    private void StartScribble()
    {
        bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        gfx = Graphics.FromImage(bitmap);
        gfx.Clear(Color.White);

        ctx = WintabInfo.GetDefaultDigitizingContext(ContextOptions.CXO_MESSAGES);
        ctx.OutOrgX = ctx.OutOrgY = 0;
        ctx.OutExtX = ClientSize.Width;
        ctx.OutExtY = ClientSize.Height;
        ctx.Open();

        wtData = new WintabData(ctx);
        wtData.AddPacketHandler(OnPacket);

        renderTimer.Start();
    }

    private void OnPacket(object? sender, MessageReceivedEventArgs e)
    {
        if (wtData == null || gfx == null) return;

        var pkt = wtData.GetPacket((uint)e.Message.LParam, (uint)e.Message.WParam);
        if (pkt.pkContext == 0) return;

        // Digitizer context: Y is inverted (tablet origin is bottom-left)
        var pt = new Point(pkt.pkX, ClientSize.Height - pkt.pkY);

        if (lastPoint == Point.Empty) lastPoint = pt;

        if (pkt.pkNormalPressure > 0)
        {
            float w = (float)pkt.pkNormalPressure / maxPressure * 5f + 1f;
            using var pen = new Pen(Color.Black, w);
            gfx.DrawLine(pen, pt, lastPoint);
            dirty = true;
        }

        lastPoint = pt;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        renderTimer.Stop();
        wtData?.Dispose();
        ctx?.Dispose();
        gfx?.Dispose();
        bitmap?.Dispose();
        base.OnFormClosing(e);
    }
}
```

## Native C++ Gotchas (PenSession.Native)

If you're building a native C/C++ consumer of Wintab (or using the `PenSession.Native.dll` C API), be aware of these pitfalls discovered during the Scribble.Win32 implementation.

### 1. Message-only windows don't receive WT_PACKET

The Wacom driver does **not** deliver `WT_PACKET` messages to message-only windows (created with `HWND_MESSAGE` as the parent). You must create a regular top-level window, even if it's hidden (zero size, never shown):

```cpp
// WRONG — no packets will arrive:
HWND hwnd = CreateWindowExW(0, cls, L"", 0, 0,0,0,0,
    HWND_MESSAGE, nullptr, hInst, nullptr);

// CORRECT — hidden top-level window:
HWND hwnd = CreateWindowExW(0, cls, L"", WS_OVERLAPPED, 0,0,0,0,
    nullptr, nullptr, hInst, nullptr);
```

### 2. Explicitly set lcPktData to match your PACKET struct

The default system context may not include all packet fields your `PACKET` struct expects. If `lcPktData` doesn't match your struct layout, the driver will write fields at wrong offsets, producing garbage data. Always set it explicitly:

```cpp
lc.lcPktData  = PK_PKTBITS_ALL;  // 0x1FFF — all standard fields
lc.lcPktMode  = 0;                // absolute mode
lc.lcMoveMask = PK_PKTBITS_ALL;   // generate packets on any field change
lc.lcBtnDnMask = 0xFFFFFFFF;
lc.lcBtnUpMask = 0xFFFFFFFF;
```

### 3. DPI awareness is required for correct coordinates

Wintab system context coordinates are always **physical screen pixels**. If your app is not DPI-aware, `ScreenToClient()` operates on virtualized coordinates and the pen position will drift progressively from the stroke as you move away from the top-left corner. The fix:

```cpp
// Call before creating any windows:
SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
```

Handle `WM_DPICHANGED` to reposition UI elements and update font sizes when the window moves between monitors with different DPI.

### 4. Windows min/max macros conflict with std::min/std::max

`<windows.h>` defines `min` and `max` as macros, which break `std::min()` and `std::max()` with cryptic syntax errors. Define `NOMINMAX` before including `<windows.h>`, or add it to your preprocessor definitions:

```xml
<PreprocessorDefinitions>WIN32_LEAN_AND_MEAN;NOMINMAX;...</PreprocessorDefinitions>
```

### 5. Double-buffer WM_PAINT to prevent ribbon/toolbar flicker

If your app repaints a toolbar area at high frequency (e.g., 60fps pen telemetry updates), direct-to-screen painting causes visible flicker. Use double-buffering and suppress background erase:

```cpp
case WM_ERASEBKGND:
    return 1;  // Skip erase — we paint everything ourselves.

case WM_PAINT: {
    PAINTSTRUCT ps;
    HDC hdc = BeginPaint(hwnd, &ps);

    // Create offscreen buffer.
    HDC mem_dc = CreateCompatibleDC(hdc);
    HBITMAP mem_bmp = CreateCompatibleBitmap(hdc, width, height);
    SelectObject(mem_dc, mem_bmp);

    // Draw everything to mem_dc...
    paint_toolbar(mem_dc);
    BitBlt(mem_dc, 0, toolbar_h, canvas_w, canvas_h, canvas_dc, 0, 0, SRCCOPY);

    // Single blit to screen.
    BitBlt(hdc, 0, 0, width, height, mem_dc, 0, 0, SRCCOPY);

    DeleteObject(mem_bmp);
    DeleteDC(mem_dc);
    EndPaint(hwnd, &ps);
}
```

Also use `WS_CLIPCHILDREN` on the main window style to prevent GDI from painting over child controls (combo boxes, sliders, buttons), which causes them to flicker independently.

### 6. Child controls need explicit DPI-scaled fonts

Win32 child controls (combo boxes, buttons) inherit the system's small bitmap font by default, which looks tiny on high-DPI displays. Create a DPI-scaled font and apply it via `WM_SETFONT`:

```cpp
HFONT font = CreateFontW(-MulDiv(13, dpi, 96), 0, 0, 0, FW_NORMAL,
    0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
SendMessageW(combo, WM_SETFONT, (WPARAM)font, TRUE);
SendMessageW(button, WM_SETFONT, (WPARAM)font, TRUE);
```

Recreate the font when `WM_DPICHANGED` fires. Don't forget to `DeleteObject` the old font.

### 7. Don't reposition child controls inside WM_PAINT

Moving controls via `SetWindowPos` inside `WM_PAINT` triggers repaints of both the parent and child, causing feedback loops and flicker. Instead, compute positions in a separate `layout_controls()` function called from `WM_CREATE`, `WM_SIZE`, and `WM_DPICHANGED`.

### 8. Close log files with RAII

The C++ `Logger` uses a static `FILE*` for the diagnostic log. Static locals in DLLs are not reliably destroyed on unload. Wrap the file handle in an RAII struct with a destructor, and provide an explicit `close()` method:

```cpp
struct State {
    std::mutex mutex;
    FILE* file = nullptr;
    ~State() { if (file) fclose(file); }
};

static State& get_state() {
    static State s;  // destroyed at process exit
    return s;
}
```

The C# `WintabSession` has the same pattern — `Dispose()` calls `CloseLog()` which flushes and closes the `StreamWriter`. Always dispose sessions on app shutdown to avoid leaked file handles.

### 9. Always handle Start() errors

Both the C# and C++ `WintabSession.Start()` return an error string (null/nullptr on success). **Always check the return value.** If Wintab is not installed or the context fails to open, ignoring the error leaves the app in a broken state with no user feedback.

```csharp
// C# — show error in the title bar or status area
var error = session.Start(resolution);
if (error != null)
{
    Title = $"App - {error}";
    return;
}
```

```cpp
// C++ — check before using the session
const char* error = wintab_session_start(session, resolution);
if (error) {
    snprintf(status, sizeof(status), "Start failed: %s", error);
    return;
}
```

### 10. HCTX in the PACKET struct is pointer-sized, not 32-bit

The `pkContext` field in the Wintab `PACKET` struct is an `HCTX`, which is a `HANDLE` — pointer-sized (8 bytes on x64, 4 bytes on x86). If you declare it as a fixed 4-byte type (`uint`, `DWORD`, `UInt32`), every field after it reads from the wrong offset on 64-bit systems. The data appears silently shifted: Y contains X's value, Pressure contains Z's value, etc.

```csharp
// WRONG on x64 — pkContext is 4 bytes, everything after it is misaligned:
public uint pkContext;

// CORRECT — pointer-sized, matches HCTX / HANDLE:
public IntPtr pkContext;
```

```cpp
// C++ is naturally correct — HCTX is typedef'd from HANDLE (void*):
typedef HANDLE HCTX;  // 8 bytes on x64
```

This is easy to miss because the struct compiles fine and the session appears to work — you get pen data, just with the wrong fields. Always verify struct sizes match the Wintab spec: `sizeof(PACKET)` should be 88 bytes on x64 (not 84).

### 11. WM_POINTER events coalesce — use GetPointerPenInfoHistory carefully

When the UI thread is busy (rendering, GPU work, layout), Windows coalesces multiple `WM_POINTERUPDATE` messages into one. Only the most recent position is delivered via `GetPointerPenInfo`. Intermediate positions are silently discarded, causing visible straight-line segments ("polygon strokes") instead of smooth curves.

The fix: use `GetPointerPenInfoHistory` for `WM_POINTERUPDATE` messages, but **only when `count > 1`**. For single events (`count == 1`), always use `GetPointerPenInfo` instead — the history API returns subtly different data for single events that causes silent data loss on some driver/OS combinations.

```cpp
if (msg == WM_POINTERUPDATE) {
    POINTER_PEN_INFO history[64];
    UINT32 count = 64;
    if (GetPointerPenInfoHistory(pointerId, &count, history) && count > 1) {
        for (int i = count - 1; i >= 0; i--)  // oldest first
            process_point(history[i]);
        return;
    }
}
// Single point fallback (most common case).
POINTER_PEN_INFO pen_info = {};
GetPointerPenInfo(pointerId, &pen_info);
process_point(pen_info);
```

**Critical: `count > 1`, not `count > 0`.** Using `count > 0` was found to break WM_POINTER completely in Scribble.Win32 — no pen data arrived at all. The `count == 1` history path silently consumed events without producing usable output.

PenSession's `WmPointerSession` handles this automatically. Apps using the framework-specific sessions (WinUI, WPF, WinForms, Avalonia) are not affected — those frameworks decoalesce pointer events internally.

### 12. WinForms: NativeWindow.AssignHandle crashes on Form HWNDs

Do **not** use `NativeWindow.AssignHandle` to subclass a WinForms Form HWND for `WM_POINTER` interception. WinForms internally creates its own `NativeWindow` for each Form, and calling `AssignHandle` with the same HWND conflicts with WinForms' internal window procedure ownership, causing a crash (exit code -1).

`SetWindowSubclass` also doesn't work reliably in WinForms for the same reason — WinForms manages its own window procedure chain.

**The solution is `IMessageFilter`**, which intercepts messages at the application message pump level without touching HWND ownership:

```csharp
public class WmPointerFilter : IMessageFilter
{
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WM_POINTERUPDATE || m.Msg == WM_POINTERDOWN || m.Msg == WM_POINTERUP)
        {
            // Process WM_POINTER message here
            return false; // false = let WinForms also process it
        }
        return false;
    }
}

// Register:
Application.AddMessageFilter(new WmPointerFilter());
```

This is how `PenSession.WinForms.dll` (`WinFormsPointerSession`) implements WM_POINTER support. The filter receives all messages before they reach any window procedure, so it works regardless of which Form or Control the pen is over.
