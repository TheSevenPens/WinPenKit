# Input Architecture

**Status: Implemented.** This was the early design discussion that evolved into the PenSession library. See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the final design and [FUTURES_GENERAL.md](FUTURES_GENERAL.md) for the phased implementation plan.

---

Original design discussion for supporting multiple input APIs (Wintab + Windows Pointer) across multiple UI frameworks (WinUI 3, WPF, Avalonia, WinForms) with runtime switching.

## Requirements

- Apps must support **runtime switching** between input APIs (some may require restart)
- The canvas, brush engine, and stroke history survive a switch — only the input source changes
- Drawing apps will be built in WinUI 3, WPF, Avalonia, and WinForms
- Each app should be able to use Wintab (System and Digitizer) and the native Windows Pointer API

## Layered Architecture

```
┌─────────────────────────────────────────────────────┐
│  App (WinUI3 / WPF / Avalonia / WinForms)           │
│                                                      │
│  ┌─────────────┐  ┌──────────┐  ┌────────────────┐  │
│  │ Input API    │  │  Brush   │  │    Canvas /    │  │
│  │ Selector UI  │  │  Engine  │  │    Renderer    │  │
│  └──────┬───────┘  └────▲─────┘  └────────────────┘  │
│         │               │                            │
│  ┌──────▼───────────────┴──────────────────────────┐ │
│  │  CanvasAdapter (per framework)                   │ │
│  │   - Converts PenPoint (desktop) → canvas coords │ │
│  │   - Feeds brush engine                          │ │
│  │   - Owns render timer / event wiring            │ │
│  └──────────────────────▲──────────────────────────┘ │
└─────────────────────────┼────────────────────────────┘
                          │  PenPoint stream
┌─────────────────────────┴──────────────────────────┐
│  IInputSession (in shared library)                  │
│   - Start() / Stop() / Dispose()                    │
│   - Produces PenPoints (desktop coords, double)     │
│   - Framework-agnostic                              │
│                                                      │
│  Implementations:                                    │
│   ├── WintabSystemSession                            │
│   ├── WintabDigitizerSession                         │
│   └── (future) PointerInputSession                   │
└─────────────────────────────────────────────────────┘
```

## PenPoint — The Universal Data Format

All input sessions produce `PenPoint` records. This is the universal currency between the input layer and the app.

```csharp
record struct PenPoint
{
    double DesktopX, DesktopY;   // physical screen pixels (double for sub-pixel)
    int RawX, RawY;              // native values from the API
    uint Pressure;               // raw, API-specific range
    int MaxPressure;             // for normalization
    int Azimuth, Altitude, Twist;
    int Z;
    uint Buttons;
    bool IsFirstInStroke;        // pen just entered or pressure just started
    InputApi Source;              // enum: WintabSystem, WintabDigitizer, WindowsPointer
}
```

**Key design decision:** PenPoint contains **raw pen data** (position, pressure, tilt, buttons) — not brush engine output (stroke width, color, opacity). Mapping pressure to width is a brush decision, not an input API concern. The current `StrokeSegment` with a `Width` field would move to the app/brush layer.

## What Lives Where

| Concern | Where | Why |
|---|---|---|
| Context creation, packet handling, ScaleAxis, Y-flip, CXO_SYSTEM | `IInputSession` impls in shared lib | Nobody should have to rediscover these |
| Desktop → canvas conversion | App's CanvasAdapter | Framework-specific (DPI, PointToClient, TransformToVisual) |
| Brush engine (pressure→width, color, dabs) | App layer | App-specific creative decisions |
| Render timer / polling | App's CanvasAdapter | Different APIs have different threading models |
| Input API selector UI | App layer | UI is framework-specific |

## Runtime Switching Flow

```csharp
// User picks "Wintab Digitizer" from dropdown
adapter.Stop();                              // stops current session
var session = new WintabDigitizerSession();  // or use a factory
adapter.Start(session);                      // wires up new session, starts polling
// Canvas and brush state are untouched
```

## Threading Considerations

- **Wintab sessions** deliver packets on a background thread at 200+ Hz. The app polls via a render timer (60 fps). A `ConcurrentQueue<PenPoint>` bridges the two.
- **Windows Pointer sessions** deliver events on the UI thread already. No render timer needed — the adapter can feed the brush engine directly from the event handler.
- The adapter must handle both models transparently. One approach: the adapter always uses a render timer, and the Pointer session simply enqueues immediately when the event arrives. Simpler but adds one frame of latency for Pointer input.

## Brush Engine (Future)

The brush engine is currently app-specific (each app creates `Line` elements or calls `Graphics.DrawLine`). A future shared brush engine could:

- Accept consecutive `PenPoint`s
- Apply brush-specific logic (pressure→width, tilt→angle, dab spacing, etc.)
- Produce framework-neutral rendering commands
- Let each app's renderer execute those commands (XAML elements, GDI+, Win2D, SkiaSharp, etc.)

This is a separate concern from the input architecture and would be designed later.

## Key Lessons Learned

These are hard-won discoveries from the Wintab implementation that informed this design:

1. **CXO_SYSTEM is required** for the Wacom driver to deliver WT_PACKET messages. Contexts without it open successfully but produce no packets.

2. **Multi-monitor trap:** Setting `OutExtX/Y` to canvas dimensions breaks when the tablet is mapped to one monitor — the driver clips output to the mapped monitor's fraction of the output range.

3. **Hi-res digitizer approach:** Override `OutExt` to tablet-native range (`InExt`) and convert through the system context's `InOrg/InExt → SysOrg/SysExt` mapping. This gives ~7x more resolution per axis while handling multi-monitor correctly.

4. **Y-axis inversion:** Tablet origin is bottom-left (Y up), screen is top-left (Y down). The `SysExtY` in the mapping must be negated to trigger the correct inversion in the `ScaleAxis` transform.

5. **WTGetA (Refresh):** The driver can modify context values during `WTOpen`. Always read back actual values after opening.

6. **WTI_DEFCONTEXT vs WTI_DEFSYSCTX:** The Wacom driver may not deliver packets from `WTI_DEFCONTEXT` contexts. Use `WTI_DEFSYSCTX` as the base for both modes.
