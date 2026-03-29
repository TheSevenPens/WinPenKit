# Future Improvements

Remaining improvements for the WintabDN codebase.

## TODO

### Unit Testing

The P/Invoke layer in `WintabNative` can be wrapped behind an interface, allowing tests to mock Wintab responses without a physical tablet:

```csharp
public interface IWintabFunctions
{
    uint WTInfo(uint category, uint index, IntPtr output);
    HCTX WTOpen(HWND hwnd, ref WintabLogContext ctx, bool enable);
    bool WTClose(HCTX ctx);
    bool WTPacket(HCTX ctx, uint pktId, IntPtr pkt);
}
```

### NuGet Packaging

The WintabDN library has no WinForms or System.Drawing dependencies. It could be published as a standalone NuGet package separate from the test apps. Remaining work:

- Adding a `.nuspec` or SDK-style package properties
- Versioning strategy and publish pipeline

### ExtensionTestApp Modernization

The `ExtensionTestForm` has the same structural issues as the original `TestForm` - large monolithic form class, `MessageBox.Show()` error handling, and manual resource management. The same modernization patterns applied to LegacyPenTestApp should be applied here:

- Unify namespace to `ExtensionTestApp`
- Enable nullable reference types and implicit usings
- String interpolation, modern C# patterns
- Proper resource disposal
- Input validation and error handling to TraceMsg instead of MessageBox

### Configuration Externalization

Hard-coded values like `MAX_NUM_ATTACHED_TABLETS = 16` and `MAX_STRING_SIZE = 256` in the library should be configurable or at minimum documented as to why those specific values were chosen.

---

## Done

### Remove UI Dependencies from the Library

All 42 `MessageBox.Show()` calls removed from the WintabDN library. Exceptions now propagate to the caller. All `System.Windows.Forms` and `System.Drawing` dependencies fully removed (see "Remove Framework Dependencies" below).

### Implement IDisposable

`WintabContext` now implements `IDisposable` - `Dispose()` closes the native Wintab context handle if open, safe to call multiple times. `WintabData` now implements `IDisposable` - `Dispose()` automatically unsubscribes all registered event handlers (packet, status, info-change) by tracking them internally. Both can now be used with `using` blocks.

### Wrap Unmanaged Memory Allocations

Added `UnmanagedBuffer : IDisposable` to `WintabMarshalling.cs` and converted all 23 alloc/free call sites to use `using var buf = new UnmanagedBuffer(...)`. Fixed a memory leak in `MarshalDataExtPackets` where `IntPtr tmp` was allocated per packet in a loop but never freed. Removed the now-unused `AllocUnmanagedBuf` and `FreeUnmanagedBuf` static methods.

### Refactor TestForm

Decomposed from a single 893-line file into four focused classes:
- `ScribbleRenderer` (~160 lines) - bitmap-backed drawing, pens, paint/resize, render timer
- `TabletDataCapture` (~170 lines) - context open/close for system and digitizer, event wiring
- `WintabTestRunner` (~210 lines) - all Wintab API tests
- `TestForm` (~220 lines) - thin shell wiring UI events to the above

### Add Logging

Added `IWintabLog` interface (Info/Warn/Error) and `WintabLog` static class. Logger is opt-in via `WintabLog.Logger`. Library logs context open/close, event handler registration, data packet operations. LegacyPenTestApp implements `TextBoxLogger` with thread marshalling.

### Nullable Reference Types

Enabled across the entire solution. `GetDefaultDigitizingContext()` and `GetDefaultSystemContext()` now return `WintabContext?`. `GetPackets()` returns `WintabPacket[]?`. All handler fields, logger, and events properly annotated. Zero CS8* warnings.

### Modern C# Language Features

**LegacyPenTestApp:** string interpolation, expression-bodied members, `using` declarations, collection expressions, `ArgumentNullException.ThrowIfNull()`, pattern matching, `ApplicationConfiguration.Initialize()`.

**WintabDN library:** implicit usings enabled, primary constructors on exception classes and `MessageReceivedEventArgs`, pattern matching switch expression for message classification, `SetMessageState`/`ClassifyMessage` helper eliminating Watch/UnWatch duplication.

### Optimize Marshalling in WintabMarshalling

`MarshalDataExtPackets` rewritten to use `IntPtr.Add` + `Marshal.PtrToStructure` directly from the source buffer. Eliminated per-packet: bulk byte array copy, inner byte-slice loop, `AllocHGlobal` temporary buffer, and `Marshal.Copy`.

### DPI-Aware Coordinate Handling

Scribble uses raw Wintab coordinates directly - `Control.PointToClient()` handles physical-to-logical DPI conversion internally in PerMonitorV2. Added `WintabDpiHelper` utility for non-WinForms frameworks. Added System/Digitizer context switcher.

### Proper Async Event Handling

Packet handler draws to bitmap immediately but defers screen repaint and status bar updates to a render timer at ~60fps. Multiple packets between timer ticks are batched into a single repaint.

### Custom Exception Hierarchy

Added `WintabException` (base), `WintabContextException`, `WintabDataException`, `WintabExtensionException`. All generic `throw new Exception(...)` replaced across the library.

### Remove Framework Dependencies

WintabDN library is now fully framework-agnostic (no WinForms, no System.Drawing). Can be consumed from WPF, Avalonia, MAUI, or console apps.

- `MessageEvents.cs`: Replaced `Form` + `Application.Run()` with raw Win32 P/Invoke (`RegisterClassExW`, `CreateWindowExW`, `GetMessage`/`DispatchMessage` loop). Replaced `System.Windows.Forms.Message` with custom `WintabMessage` struct.
- `WintabExtensions.cs`: Replaced `System.Drawing.Image.FromFile()` + PNG re-encode with `File.ReadAllBytes()` (caller provides PNG file directly).
- `WintabDN.csproj`: Removed `UseWindowsForms`. Library targets `net10.0-windows` with no framework references.

### API Naming Consistency

Removed Hungarian notation `E` prefix and `Values` suffix from all public enums. Renamed WintabData methods to shorter names. 172 replacements across 10 files.

Enum renames:

| Before | After |
|---|---|
| `ECTXOptionValues` | `ContextOptions` |
| `ECTXStatusValues` | `ContextStatus` |
| `ECTXLockValues` | `ContextLocks` |
| `EWintabEventMessage` | `WintabEventMessage` |
| `EWintabPacketBit` | `PacketBit` |
| `EWintabPacketStatusValue` | `PacketStatus` |
| `EWintabPacketButtonCode` | `PacketButtonCode` |
| `EAxisDimension` | `AxisDimension` |
| `EWTICursorNameIndex` | `CursorNameIndex` |
| `EWTXExtensionTag` | `ExtensionTag` |
| `EWTExtensionTabletProperty` | `ExtensionTabletProperty` |
| `EWTExtensionIconProperty` | `ExtensionIconProperty` |

Method renames:

| Before | After |
|---|---|
| `SetWTPacketEventHandler` | `AddPacketHandler` |
| `RemoveWTPacketEventHandler` | `RemovePacketHandler` |
| `SetStatusEventHandler` | `AddStatusHandler` |
| `RemoveStatusEventHandler` | `RemoveStatusHandler` |
| `SetInfoChangeEventHandler` | `AddInfoChangeHandler` |
| `RemoveInfoChangeEventHandler` | `RemoveInfoChangeHandler` |
| `GetDataPacket` | `GetPacket` |
| `GetDataPacketExt` | `GetPacketExt` |
| `GetDataPackets` | `GetPackets` |
| `FlushDataPackets` | `FlushPackets` |

### Completed Bug Fixes and Cleanup

| Issue | Resolution |
|---|---|
| Inconsistent namespaces (`FormTestApp`, `WintabDN`, `LegacyPenTestApp`) | Unified to `LegacyPenTestApp` |
| Dead code (`m_pen`, `m_backPen`, `m_TABEXTX/Y`, `m_pkTimeLast`, `m_pkX/Y`) | Removed |
| Pen resource leak (`m_drawPens` re-added on every scribble enable, never disposed) | Created once, disposed on form close |
| Null-check-after-dereference in `OpenTestDigitizerContext` / `OpenTestSystemContext` | Null check moved before first use |
| Wrong exception types (`NullReferenceException` for argument validation) | `ArgumentNullException.ThrowIfNull()` |
| Exception wrapping loses stack trace (`throw new Exception("..." + ex.ToString())`) | Replaced with `TraceMsg` logging |
| `ClearDisplay` doesn't clear scribble bitmap | Now clears bitmap and resets last point |
| `MappingForm` no input validation on Apply | `try/catch FormatException` with user message |
| Modal `MessageBox.Show` for errors blocks the app | Errors go to TraceMsg log panel |
| Unnecessary WPF dependency (`UseWPF` in csproj) | Removed - no WPF code in LegacyPenTestApp |
| Empty `Properties/Resources.resx` and `Resources.Designer.cs` | Deleted |
| `Program.cs` using legacy init pattern | `ApplicationConfiguration.Initialize()` |
| `System.Windows.Forms` dependency in library | Fully removed; raw Win32 message window |
| `System.Drawing` dependency in library | Fully removed; `File.ReadAllBytes()` |
| `System.Windows.Forms.Message` in public API | Replaced with `WintabMessage` struct |
| Library swallows exceptions silently | Try/catch blocks removed; exceptions propagate |
| `MessageBox.Show` for image validation | Replaced with `FileNotFoundException` |
| Pointless `try/catch { throw; }` wrappers | Removed from all methods |
| Scribble used `Cursor.Position` workaround for DPI | Raw Wintab coords to `PointToClient()` |
| Scribble only supported system context | System/Digitizer combo box added |
| `Invalidate()` called per-packet (200+ Hz) | Render timer at ~60fps with dirty flag |
| No nullable refs in WintabDN library | `Nullable` enabled; all files annotated |
| No implicit usings in WintabDN library | `ImplicitUsings` enabled |
| Verbose exception classes (4 classes, 8 constructors) | Primary constructors: 4 one-liner classes |
| `MessageReceivedEventArgs` verbose constructor | Primary constructor |
| Watch/UnWatch duplicated switch blocks | Shared `ClassifyMessage` switch expression |
| Hungarian notation enums (`ECTXOptionValues`, etc.) | Renamed 12 enums, 10 methods (172 replacements) |
| Block-scoped namespaces with extra indentation | File-scoped namespaces across all 20 source files |
