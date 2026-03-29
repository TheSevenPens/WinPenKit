# WintabDN Library Architecture

## Overview

WintabDN is a managed .NET wrapper around the native Wintab API (`Wintab32.dll`), which provides pen tablet input on Windows. The library is organized in layers, from low-level P/Invoke at the bottom to high-level query and data capture APIs at the top.

```
┌─────────────────────────────────────────────────────┐
│                  Client Application                  │
│    (LegacyPenTestApp, Scribble.WinUI, etc.)       │
│                                                     │
│    App wrapper (WintabSessionWinUI3 / WinForms)     │  ← Desktop→Canvas conversion,
│       converts PenPoint → canvas coords + brush     │     brush logic, rendering
│    UI controls (ScribbleRibbon, DrawingCanvas, etc.)    │
├─────────────────────────────────────────────────────┤
│              WintabSession + PenPoint                │  ← Session Layer (new)
│    Context lifecycle, ScaleAxis, multi-monitor,      │     Framework-agnostic
│    CXO_SYSTEM, hi-res OutExt, ConcurrentQueue       │
├─────────────┬──────────────┬────────────────────────┤
│ WintabInfo │ WintabData  │  WintabExtensions     │  ← High-Level API
├─────────────┴──────┬───────┴────────────────────────┤
│   WintabContext   │       MessageEvents             │  ← Context & Events
├────────────────────┴─────────────────────────────────┤
│                   WintabNative                       │  ← P/Invoke Layer
├──────────────────────────────────────────────────────┤
│                   Wintab32.dll                       │  ← Native Driver
└──────────────────────────────────────────────────────┘

  Support:  WintabMarshalling  |  WintabException  |  WintabLog  |  WintabDpiHelper
```

## Components

### Session Layer

**WintabSession.cs** - Framework-neutral session that encapsulates all Wintab complexity. This is the recommended entry point for new applications. Handles:

- Context creation (system or digitizer hi-res mode)
- The `CXO_SYSTEM` requirement (Wacom driver won't deliver packets without it)
- Digitizer hi-res: overrides `OutExt` to tablet-native range, converts via `ScaleAxis` through the system context's `InOrg/InExt → SysOrg/SysExt` mapping
- Y-axis inversion (tablet origin bottom-left → screen origin top-left)
- Multi-monitor mapping (works regardless of tablet-to-monitor assignment)
- Thread-safe `ConcurrentQueue<PenPoint>` for background-thread-to-UI-thread data transfer
- Fallback to screen-pixel mode if hi-res open fails
- Diagnostic file logging (`%TEMP%\WintabSession.log`)

**PenPoint.cs** - Framework-neutral pen data record struct containing desktop coordinates (`double` precision), raw tablet values, pressure, orientation (azimuth/altitude/twist), Z height, buttons, and cursor type. Also defines `InputApi` and `WintabResolution` enums.

Consumers create a `WintabSession`, call `Start(resolution)`, poll `DrainPoints()` on a render timer, convert `DesktopX/Y` to canvas coordinates using framework-specific methods, and feed the results to their brush engine. See [WINTAB_SESSION_GUIDE.md](WINTAB_SESSION_GUIDE.md) for the full consumer guide.

### P/Invoke Layer

**WintabNative.cs** - Direct P/Invoke declarations for every Wintab32.dll export (`WTOpenA`, `WTClose`, `WTPacket`, etc.). Also defines the managed type wrappers used throughout the library:

| Type | Wraps | Purpose |
|---|---|---|
| `HWND` | `IntPtr` | Window handle |
| `HCTX` | `UInt32` | Wintab context handle |
| `WTPKT` | `UInt32` | Packet data bit mask |
| `FIX32` | `UInt32` | Fixed-point number |

All other library components depend on this layer. It has no internal dependencies.

### Context & Events

**WintabContext.cs** - Managed wrapper around a Wintab context handle. Owns the context lifecycle (open, close, enable, overlap ordering). Implements `IDisposable` for automatic cleanup. Also defines the core data structures:

- `WintabLogContext` - the 40+ field struct that configures a context
- `WintabAxis` / `WintabAxisArray` - axis range and resolution info
- `ContextOptions` - context option flags (`CXO_SYSTEM`, `CXO_MESSAGES`, etc.)

**MessageEvents.cs** - Bridges native Windows messages and .NET events. Creates a hidden Win32 window (via raw P/Invoke: `RegisterClassExW`/`CreateWindowExW`) on a background thread with a `GetMessage`/`DispatchMessage` loop to receive Wintab messages. Messages are dispatched to subscribers via `SynchronizationContext.Post` to marshal back to the caller's thread. Uses the framework-agnostic `WintabMessage` struct instead of `System.Windows.Forms.Message`.

Exposes three events:
- `PacketMessageReceived` - pen data packets (WT_PACKET, WT_PACKETEXT, WT_CSRCHANGE)
- `StatusMessageReceived` - context state changes (WT_CTXOPEN, WT_CTXCLOSE, etc.)
- `InfoChgMessageReceived` - device configuration changes (WT_INFOCHANGE)

This component has no framework dependencies - only Win32 P/Invoke.

### High-Level API

**WintabInfo.cs** - Static query methods for device capabilities and default contexts. Entry point for most applications:

- `IsWintabAvailable()` - check if driver is running
- `GetDefaultSystemContext()` / `GetDefaultDigitizingContext()` - preconfigured contexts
- `GetMaxPressure()`, `GetDeviceAxis()`, `GetDeviceOrientation()` - hardware queries
- `GetNumberOfDevices()`, `GetStylusName()` - device enumeration

**WintabData.cs** - Packet capture and queue management. Created with a `WintabContext`, wires up event handlers to `MessageEvents`, and provides methods to retrieve packets:

- `AddPacketHandler()` / `RemovePacketHandler()` - subscribe to pen data
- `GetPacket()` - retrieve a single packet by ID
- `GetPackets()` - retrieve multiple packets (remove or peek)
- `GetPacketExt()` - retrieve extended packet (includes ExpressKey/TouchRing data)

Implements `IDisposable` - automatically unsubscribes all event handlers on dispose.

Also defines the packet structures:
- `WintabPacket` - position, pressure, tilt, buttons, timestamp
- `WintabPacketExt` - extended data including ExpressKey and TouchRing/Strip events

**WintabExtensions.cs** - API for tablet hardware controls (ExpressKeys, Touch Rings, Touch Strips). Provides property get/set for control configuration, icon images, and override labels. Image files are read directly as bytes (no System.Drawing dependency).

### Support Components

**WintabMarshalling.cs** - Unmanaged memory marshalling utilities.
- `UnmanagedBuffer` - RAII wrapper (`IDisposable`) for `Marshal.AllocHGlobal`. Used everywhere a native buffer is needed, guaranteeing cleanup via `using`.
- `MarshalDataPackets()` / `MarshalDataExtPackets()` - bulk marshal packet arrays from unmanaged memory.

**WintabException.cs** - Exception hierarchy for typed error handling:

```
WintabException                   ← general Wintab errors
  WintabContextException          ← context open/close/enable failures
  WintabDataException             ← packet/queue operation failures
  WintabExtensionException        ← extension property get/set failures
```

**WintabLog.cs** - Optional logging abstraction. Applications set `WintabLog.Logger` to an `IWintabLog` implementation to receive diagnostic messages. If null (default), all logging is a no-op.

**WintabDpiHelper.cs** - DPI conversion utilities for system context coordinates. Wintab reports physical screen pixels; this helper converts to DPI-scaled logical coordinates for DPI-aware processes. Note: in .NET 10 WinForms with PerMonitorV2, `Control.PointToClient()` handles this automatically. In DPI-virtualized processes (such as WinUI 3), `WintabDpiHelper` and other Win32 DPI APIs return virtualized values — the application must temporarily switch the thread to Per-Monitor V2 awareness via `SetThreadDpiAwarenessContext` to get the real DPI. See [HOW_TO_USE.md](HOW_TO_USE.md#winui-3) for details.

## Dependency Map

Arrows point from dependent to dependency.

```
WintabSession ────┬──→ WintabInfo
                  ├──→ WintabContext
                  ├──→ WintabData
                  └──→ WintabLog

WintabInfo ──────┬──→ WintabNative
                  ├──→ WintabMarshalling
                  ├──→ WintabContext
                  ├──→ WintabLog
                  └──→ WintabException

WintabData ──────┬──→ WintabNative
                  ├──→ WintabMarshalling
                  ├──→ WintabContext
                  ├──→ MessageEvents
                  ├──→ WintabLog
                  └──→ WintabException

WintabContext ───┬──→ WintabNative
                  ├──→ MessageEvents
                  ├──→ WintabLog
                  └──→ WintabException

WintabExtensions ┬──→ WintabNative
                  ├──→ WintabMarshalling
                  ├──→ WintabContext
                  └──→ WintabException

MessageEvents ────┴──→ WintabException

WintabMarshalling ────────────  (no WintabDN deps; references packet structs from WintabData)
WintabException ──────  (no deps)
WintabLog ────────────  (no deps)
WintabDpiHelper ──────  (no deps; uses Win32 P/Invoke directly)
WintabNative ─────────  (no deps; pure P/Invoke)
```

## External Dependencies

| Component | External Dependency | Reason |
|---|---|---|
| `WintabNative` | `Wintab32.dll` | Native tablet driver API |
| `MessageEvents` | `user32.dll` | Hidden window for message pump (raw Win32 P/Invoke) |
| `WintabExtensions` | `System.IO` | File reading for OLED display icon images |
| `WintabDpiHelper` | `user32.dll`, `shcore.dll` | DPI queries (`MonitorFromPoint`, `GetDpiForMonitor`) |
| All marshalling | `System.Runtime.InteropServices` | `Marshal`, `DllImport`, struct layout |

## Data Flow

### Packet capture flow

```
Wintab32.dll
    │  (native Windows message: WM_WT_PACKET)
    ▼
MessageEvents.MessageWindow.WndProc()     [background thread]
    │  (SynchronizationContext.Post)
    ▼
MessageEvents.PacketMessageReceived       [UI thread]
    │  (event handler registered by WintabData)
    ▼
Application's packet handler
    │  (calls WintabData.GetPacket)
    ▼
WintabNative.WTPacket()                  [P/Invoke into Wintab32.dll]
    │  (returns raw packet in unmanaged buffer)
    ▼
Marshal.PtrToStructure → WintabPacket    [managed struct]
```

### Context lifecycle

```
WintabInfo.GetDefaultSystemContext()     ← creates + configures WintabContext
    │
    ▼
WintabContext.Open()                     ← P/Invoke WTOpenA via MessageEvents.WindowHandle
    │
    ▼
WintabData(context)                      ← wraps context for packet capture
    │
    ▼
WintabData.AddPacketHandler()     ← subscribes to MessageEvents.PacketMessageReceived
    │
    ▼
... receive packets ...
    │
    ▼
WintabData.Dispose()                     ← unsubscribes all handlers
WintabContext.Dispose()                  ← P/Invoke WTClose
```

## Thread Model

- **UI thread** - where the application runs and packet handlers are invoked
- **Message loop thread** - background thread in `MessageEvents` running `Application.Run()` to receive native Wintab messages. Messages are posted to the UI thread via `SynchronizationContext.Post`.
- **Wintab driver thread** - the native driver delivers messages to the message loop thread's window handle

The `MessageWindow` uses a `ReaderWriterLockSlim` to protect the message routing dictionaries, since registration happens on the UI thread while `WndProc` runs on the message loop thread.

## File Summary

### Session Layer (separate project: `WintabSession/`)

Namespace: `WintabSession`. References `WintabDN` via fully-qualified names (e.g., `WintabDN.WintabInfo.GetDefaultSystemContext()`) to make the dependency boundary explicit.

| File | Role |
|---|---|
| `WintabSession.cs` | Session lifecycle, ScaleAxis, multi-monitor mapping, ConcurrentQueue output |
| `PenPoint.cs` | PenPoint readonly record struct with button/eraser helpers |
| `InputApi.cs` | InputApi enum (WintabSystem, WintabDigitizer) |
| `WintabResolution.cs` | WintabResolution enum (ScreenResolution, DigitizerResolution) |
| `PenButtonAction.cs` | PenButtonAction enum (None, Released, Pressed) |
| `PenButtonNumber.cs` | PenButtonNumber constants (Tip, Barrel1, Barrel2, Barrel3) |
| `PenCursorType.cs` | PenCursorType constants (PenTip, Eraser) |

### Low-Level API

| File | Role |
|---|---|
| `WintabInfo.cs` | Device queries, default context factories |
| `WintabContext.cs` | Context lifecycle (open, close, enable, refresh) |
| `WintabData.cs` | Packet capture and event wiring |
| `WintabExtensions.cs` | Tablet control extensions (ExpressKeys, rings, strips) |

### Data Structures and Enums

| File | Role |
|---|---|
| `WintabPackets.cs` | Packet structs (WintabPacket, WintabPacketExt, WTOrientation, WTRotation, etc.) |
| `WintabPackets_Enums.cs` | Packet enums (WintabEventMessage, PacketBit, PacketStatus, PacketButtonCode) |
| `WintabContext_Structs.cs` | Context structs (WintabAxis, WintabAxisArray, WintabLogContext) |
| `WintabContext_Enums.cs` | Context enums (ContextOptions, ContextStatus, ContextLocks, AxisDimension) |
| `WintabExtensions_Enums.cs` | Extension enums (ExtensionTag, ExtensionTabletProperty, EWTIExtensionIndex) |
| `WintabTypes.cs` | Wintab type wrappers (HWND, HCTX, WTPKT, FIX32 — all readonly structs) |
| `WintabTypes_Enums.cs` | Type enums (CursorNameIndex) |

### Infrastructure

| File | Role |
|---|---|
| `WintabNative.cs` | P/Invoke declarations + WTInfo query index enums |
| `MessageEvents.cs` | Windows message pump and event dispatch |
| `WintabException.cs` | Exception hierarchy |
| `WintabLog.cs` | Logging interface and static entry point |
| `WintabDpiHelper.cs` | DPI scale queries and coordinate conversion |

### Internal

| File | Role |
|---|---|
| `Internal/UnmanagedBuffer.cs` | Type-safe RAII wrapper for unmanaged memory |
| `Internal/WintabMarshalling_Internal.cs` | Legacy marshalling helper methods |
| `WintabExtensions_Internal.cs` | Extension marshalling structs and constants |

See [PUBLIC_API.md](../WintabDN/PUBLIC_API.md) for the complete type inventory.
