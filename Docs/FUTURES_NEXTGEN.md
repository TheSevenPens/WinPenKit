# Next-Generation WintabSession Architecture

**Status: Phases 1–6 complete. Phase 7 remaining.** This document tracks the phased implementation plan. What began as a speculative design has been fully implemented across managed (.NET) and unmanaged (C++, Rust) applications.

## Context

The current `WintabSession` in WintabDN encapsulates hard-won knowledge about using the Wintab API correctly:

- CXO_SYSTEM is required for Wacom driver packet delivery
- Digitizer hi-res mode: override OutExt to tablet-native range, convert via ScaleAxis through the system mapping
- Y-axis inversion via negated SysExtY
- Multi-monitor correctness through the system context's InOrg/InExt → SysOrg/SysExt mapping
- WTGetA to read back actual driver values after context open
- Fallback logic when hi-res open fails
- Thread-safe ConcurrentQueue for background packet handler → UI thread data transfer
- Button encoding (relative mode: action << 16 | buttonNumber)
- Eraser detection via cursor type, not button state

None of this is C#-specific. The knowledge applies to any language using Wintab. Yet today, a C++ or Rust developer would have to rediscover all of it from scratch.

## The Idea

Two projects that share the same session logic:

```
┌──────────────────────────────────────────────────────────────────┐
│                    WintabSessionNative (C++)                      │
│                                                                  │
│  Compiles to: PenSession.Native.dll (native, C ABI)              │
│                                                                  │
│  Contains:                                                       │
│   • Wintab32.dll dynamic loading                                 │
│   • Context creation (system + digitizer hi-res)                 │
│   • ScaleAxis with sign-flip handling                            │
│   • System mapping cache (InOrg/InExt → SysOrg/SysExt)          │
│   • Y-axis inversion                                            │
│   • Packet handler → PenPoint queue                              │
│   • Button decoding, eraser detection                            │
│   • Diagnostic logging                                           │
│                                                                  │
│  Exports (C ABI):                                                │
│   • WintabSession_Create() → handle                              │
│   • WintabSession_Start(handle, resolution) → error              │
│   • WintabSession_Stop(handle)                                   │
│   • WintabSession_Destroy(handle)                                │
│   • WintabSession_DrainPoints(handle, buf, count) → num          │
│   • WintabSession_GetMaxPressure(handle) → int                   │
│   • WintabSession_RefreshMapping(handle)                         │
│                                                                  │
│  PenPoint struct: plain C struct, no C++ types                   │
│                                                                  │
└──────────────────────┬───────────────────────────────────────────┘
                       │
          ┌────────────┴────────────┐
          │                         │
          ▼                         ▼
┌─────────────────────┐   ┌─────────────────────────────┐
│  Native consumers    │   │  WintabSessionManaged (C#)   │
│                     │   │                             │
│  C++ apps:          │   │  P/Invoke wrapper around    │
│   #include header   │   │  the native DLL             │
│   link to .lib      │   │                             │
│                     │   │  Exposes:                   │
│  Rust apps:         │   │   • WintabSession class     │
│   bindgen or        │   │   • PenPoint record struct  │
│   hand-written FFI  │   │   • Same API as today       │
│                     │   │                             │
│  Zig apps:          │   │  .NET apps see no change    │
│   @cImport or       │   │  in the public API          │
│   manual extern     │   │                             │
└─────────────────────┘   └─────────────────────────────┘
```

## Technical Analysis

### The C ABI boundary

The native DLL exports a flat C API (no C++ classes, no exceptions, no templates). This is the universal FFI boundary that every language can consume:

```c
// WintabSession.h

typedef struct WintabSession* WintabSessionHandle;

typedef enum {
    WINTAB_RESOLUTION_SCREEN = 0,
    WINTAB_RESOLUTION_DIGITIZER = 1
} WintabResolution;

typedef struct {
    double desktopX;
    double desktopY;
    int rawX;
    int rawY;
    unsigned int pressure;
    int azimuth;
    int altitude;
    int twist;
    int z;
    unsigned int status;
    unsigned int buttons;
    unsigned int cursor;
    int source;  // 0 = system, 1 = digitizer
} PenPoint;

// Lifecycle
WintabSessionHandle WintabSession_Create(void);
const char* WintabSession_Start(WintabSessionHandle h, WintabResolution res);
void WintabSession_Stop(WintabSessionHandle h);
void WintabSession_Destroy(WintabSessionHandle h);

// Data
int WintabSession_DrainPoints(WintabSessionHandle h, PenPoint* buf, int maxPoints);
int WintabSession_GetMaxPressure(WintabSessionHandle h);
int WintabSession_IsRunning(WintabSessionHandle h);

// Mapping
void WintabSession_RefreshMapping(WintabSessionHandle h);

// Diagnostics
const char* WintabSession_GetDebugInfo(WintabSessionHandle h);
const char* WintabSession_GetLogPath(void);
```

Key design constraints for the C ABI:
- **Opaque handle** for the session (not a pointer to a C++ class — callers can't accidentally access internals)
- **PenPoint is a plain C struct** with no padding surprises (all fields are natural alignment)
- **Strings returned as `const char*`** — owned by the session, valid until the next call to the same function
- **No exceptions** — errors returned as null-terminated strings (null = success)
- **No callbacks** — the consumer polls with `DrainPoints` (same as the current C# design)

### How each language consumes the DLL

**C++ (direct)**
```cpp
#include "WintabSession.h"
#pragma comment(lib, "PenSession.Native.lib")

auto session = WintabSession_Create();
WintabSession_Start(session, WINTAB_RESOLUTION_DIGITIZER);

PenPoint points[64];
int n = WintabSession_DrainPoints(session, points, 64);
for (int i = 0; i < n; i++) {
    // points[i].desktopX, .pressure, etc.
}

WintabSession_Destroy(session);
```

**Rust (FFI)**
```rust
extern "C" {
    fn WintabSession_Create() -> *mut c_void;
    fn WintabSession_Start(h: *mut c_void, res: i32) -> *const c_char;
    fn WintabSession_DrainPoints(h: *mut c_void, buf: *mut PenPoint, max: i32) -> i32;
    fn WintabSession_Destroy(h: *mut c_void);
}

#[repr(C)]
struct PenPoint {
    desktop_x: f64,
    desktop_y: f64,
    raw_x: i32,
    raw_y: i32,
    pressure: u32,
    // ...
}
```

Alternatively, Rust could use `bindgen` to auto-generate bindings from the C header.

**Zig**
```zig
const wt = @cImport({
    @cInclude("WintabSession.h");
});

const session = wt.WintabSession_Create();
_ = wt.WintabSession_Start(session, wt.WINTAB_RESOLUTION_DIGITIZER);

var points: [64]wt.PenPoint = undefined;
const n = wt.WintabSession_DrainPoints(session, &points, 64);
```

**C# (P/Invoke wrapper)**
```csharp
public class WintabSession : IDisposable
{
    [DllImport("PenSession.Native.dll")]
    private static extern IntPtr WintabSession_Create();

    [DllImport("PenSession.Native.dll")]
    private static extern IntPtr WintabSession_Start(IntPtr h, int resolution);

    [DllImport("PenSession.Native.dll")]
    private static extern int WintabSession_DrainPoints(IntPtr h,
        [Out] PenPoint[] buf, int maxPoints);

    // ... etc.
}
```

### What moves to the native DLL vs. what stays per-language

| Concern | Native DLL | Per-language |
|---|---|---|
| Wintab32.dll dynamic loading | Yes | No |
| Context creation (system + digitizer) | Yes | No |
| CXO_SYSTEM enforcement | Yes | No |
| ScaleAxis, Y-flip, mapping cache | Yes | No |
| Packet handler + PenPoint queue | Yes | No |
| Button/eraser decoding helpers | Yes (in header as inline/macros) | Optional wrappers |
| Desktop → canvas conversion | No | Yes (framework-specific) |
| Brush engine | No | Yes (app-specific) |
| DPI handling | No | Yes (framework-specific) |
| UI controls | No | Yes (framework-specific) |

### The C# transition path

The current C# `WintabSession` in WintabDN would be replaced by a P/Invoke wrapper around the native DLL. The public API stays the same — `Start()`, `Stop()`, `DrainPoints()`, `MaxPressure`, etc. Apps using `WintabSession` today would not need to change.

The transition:
1. Build the native PenSession.Native.dll in C++
2. Create the C# P/Invoke wrapper with the same public API as today's `WintabSession`
3. Ship PenSession.Native.dll alongside the C# apps
4. Remove the pure-C# Wintab context management code (WintabNative, WintabContext, WintabData, MessageEvents, etc.)

The app-level wrappers (`WintabSessionWinUI3`, `WintabSessionWinForms`) would continue to work — they only depend on `WintabSession` and `PenPoint`, not on the low-level Wintab API.

## Pros

- **Write the hard stuff once.** The context setup, ScaleAxis, Y-flip, CXO_SYSTEM requirement, multi-monitor mapping, button decoding — all implemented and tested once in C++, consumed everywhere.
- **Any language can use it.** C, C++, Rust, Zig, Go, Python (ctypes), C# (P/Invoke), and any language with C FFI support.
- **No knowledge duplication.** A Rust developer doesn't need to rediscover the CXO_SYSTEM requirement or the Y-axis negation trick. They get a DLL that just works.
- **Single source of truth for bug fixes.** A fix to ScaleAxis or the multi-monitor mapping propagates to all consumers automatically.
- **Performance.** Native code for the hot path (200+ Hz packet handling). Not that the C# version is slow, but native avoids any managed overhead.
- **Smaller per-language footprint.** Each language only needs a thin FFI binding, not a full Wintab implementation.

## Cons

- **Build complexity.** Two toolchains (C++ for the DLL, C# for managed apps). CI/CD must build both.
- **DLL deployment.** Every app must ship PenSession.Native.dll alongside the executable. Platform-specific (x64, ARM64).
- **Debugging across the boundary.** Stepping from C# into native code requires mixed-mode debugging. Rust/Zig tooling for debugging C DLLs varies.
- **ABI stability.** The PenPoint struct layout must never change without bumping a version. Adding a field is a breaking change for all consumers.
- **Memory management.** The session owns the PenPoint buffer. Callers must copy data out before the next `DrainPoints` call (or the DLL allocates per-call — but that's slower).
- **Threading model.** The DLL manages its own background thread for the Wintab message loop. Callers must understand that `DrainPoints` is thread-safe but other functions may not be.
- **Loss of C# ecosystem benefits.** No more NuGet-only deployment. Source Link and XML docs don't apply to the native DLL. IntelliSense works from the C# wrapper, not the native code.

## Challenges

### ABI stability and versioning

The `PenPoint` struct is the primary data contract. If a new field is added (e.g., `tangentPressure`), all consumers must be recompiled. Mitigation options:

- **Reserve padding fields** in the initial struct (e.g., `uint32_t reserved[8]`) for future use.
- **Version the struct** with a `structSize` field so the DLL can detect old vs. new consumers.
- **Use a flat getter API** instead of a struct (`WintabSession_GetPointDesktopX(handle, index) → double`) — flexible but much slower.

The reserved-padding approach is the most practical: define PenPoint with extra space now, document which fields are reserved, and promote reserved fields to named fields in future versions without breaking the ABI.

### Platform targeting

The native DLL must be compiled for each target architecture:
- x64 (most Windows PCs)
- ARM64 (Surface Pro X, Snapdragon laptops)
- x86 (legacy, probably not worth supporting)

The C# wrapper would use runtime architecture detection to load the correct native DLL (`runtimes/win-x64/native/PenSession.Native.dll`).

### Error handling across the boundary

C has no exceptions. The current design returns `const char*` (null = success, non-null = error message). The C# wrapper converts to exceptions or nullable strings. Rust consumers would convert to `Result<(), String>`. This is straightforward but means error messages are English-only static strings.

### Callback vs. polling

The current design uses polling (`DrainPoints`). An alternative is a callback model where the DLL calls a function pointer on each packet. This has lower latency but:
- Callbacks from native to managed code (C#) have overhead and threading constraints
- The callback runs on the Wintab background thread — the consumer must handle thread safety
- Polling is simpler, matches the existing architecture, and works identically across all languages

Recommendation: keep polling. The 60 fps render timer model is well-proven and avoids cross-language callback complexity.

### Coexistence with the pure-C# implementation

During the transition, both implementations could coexist:
- `WintabSession` (pure C#, current) — for apps that don't want the native DLL dependency
- `WintabSessionNative` (C# P/Invoke wrapper) — for apps that want the shared native implementation

The pure-C# version could eventually be deprecated once the native DLL is proven stable.

## Possible Implementation Phases

### Phase 1: Native DLL (C++)
- Port `WintabSession` logic to C++
- Define the C ABI header
- Build for x64 and ARM64
- Diagnostic logging to file (same format as current)
- Test with a minimal C++ consumer app

### Phase 2: C# P/Invoke wrapper
- Create `WintabSessionNative` class with same API as current `WintabSession`
- Ship native DLL alongside C# apps via NuGet runtimes folder
- Verify existing WinUI3 and WinForms apps work unchanged

### Phase 3: Rust/Zig bindings
- Generate or hand-write bindings
- Build sample apps in each language
- Validate multi-monitor, hi-res, button/eraser behavior

### Phase 4: Deprecate pure-C# implementation
- Once native DLL is proven stable across multiple apps and languages
- Keep the low-level C# Wintab API (WintabInfo, WintabExtensions) for apps that need extension control support not exposed through the session

---

## Option 2: Two Separate Implementations (Managed + Unmanaged)

Instead of a single native DLL consumed by everyone, maintain two independent WintabSession implementations:

1. **WintabSession (C#)** — the current implementation in WintabDN, consumed by .NET apps
2. **WintabSession (C++)** — a new native implementation, consumed by C++/Rust/Zig apps

Both implement the same logic and produce the same `PenPoint` data, but they don't share code at the binary level. They share **knowledge** (documented in the same repo, same test cases, same docs) but are separate codebases.

```
┌─────────────────────────────────┐   ┌─────────────────────────────────┐
│  WintabSession (C#)              │   │  WintabSession (C++)             │
│  Lives in: WintabDN.dll          │   │  Lives in: PenSession.Native.dll │
│                                 │   │                                 │
│  • Pure managed code             │   │  • Pure native code              │
│  • NuGet package                │   │  • C ABI exports                 │
│  • Source Link, XML docs        │   │  • Header file for consumers     │
│  • Debuggable with VS           │   │  • Debuggable with native tools  │
│                                 │   │                                 │
│  Consumers:                     │   │  Consumers:                     │
│   • WinUI 3 apps                │   │   • C++ apps                    │
│   • WinForms apps               │   │   • Rust apps (FFI)             │
│   • WPF apps                    │   │   • Zig apps (@cImport)         │
│   • Avalonia apps               │   │   • Any C ABI language          │
└─────────────────────────────────┘   └─────────────────────────────────┘
                │                                     │
                └──────────── shared ─────────────────┘
                         │
              ┌──────────┴──────────┐
              │  Shared Knowledge    │
              │                     │
              │  • Same docs        │
              │  • Same test cases  │
              │  • Same PenPoint    │
              │    field layout     │
              │  • Same behavioral  │
              │    spec             │
              └─────────────────────┘
```

### How "getting the code right the first time" changes the calculus

The main argument for Option 1 (single native DLL) is: **write the hard stuff once so nobody reimplements it wrong.** But if the code is already correct and well-documented, the risk of a second implementation diverging is much lower. Here's what changes:

**What we have today that didn't exist before:**

| Knowledge | Status |
|---|---|
| CXO_SYSTEM required for packets | Documented, tested, logged |
| ScaleAxis with sign-flip Y inversion | Implemented, documented with formula |
| Multi-monitor mapping via system context InOrg/InExt → SysOrg/SysExt | Documented, tested on 2 and 3 monitors |
| OutExt override to tablet-native range | Documented, fallback logic implemented |
| WTGetA (Refresh) after Open | Added to library, documented |
| Button encoding (relative mode) | Tested with 2 pens, documented with tables |
| Eraser detection via cursor type | Tested with physical eraser and driver-mapped button, documented |
| WTI_DEFCONTEXT doesn't deliver packets | Documented as a gotcha |
| Dual-context approach fails with Wacom driver | Documented — don't do it |
| Proximity detection is time-based, not pkStatus | Discovered, documented |

A C++ developer writing the second implementation has:
- The full behavioral spec in the docs
- The C# source as a reference implementation
- The diagnostic logging pattern to verify their implementation matches
- The exact PenPoint field layout to target
- The exact test scenarios (2 monitors, 3 monitors, Pro Pen 2, Pro Pen 3)

### Pros of Option 2 (vs Option 1)

- **No FFI boundary for C# apps.** Pure managed code. NuGet package, Source Link, XML docs, full VS debugging — no compromise. This is a significant developer experience advantage.
- **No FFI boundary for native apps.** C++ apps link directly to a C++ library. No opaque handles, no C ABI constraints, no string-based error returns. They can use C++ types (std::span, std::optional, etc.) naturally.
- **No ABI stability problem.** Each implementation evolves with its own consumers. Adding a field to the C# PenPoint is a source-compatible change (record struct). Adding a field to the C++ PenPoint is a recompile. Neither affects the other.
- **No mixed-mode debugging.** C# devs debug C#. C++ devs debug C++. No crossing the managed/native boundary.
- **No DLL deployment for C# apps.** WintabDN.dll is a managed assembly — simple NuGet. No native runtimes folder, no architecture-specific binaries.
- **No build complexity.** Each project uses its own natural toolchain. No cross-compilation step.
- **Independent release cycles.** A bug fix in the C# version ships immediately via NuGet without rebuilding the C++ version, and vice versa.
- **Idiomatic APIs in each language.** C# gets records, IDisposable, ConcurrentQueue. C++ gets RAII, std::vector, move semantics. No lowest-common-denominator C ABI.

### Cons of Option 2 (vs Option 1)

- **Two implementations to maintain.** Bug fixes must be applied in both places. If someone fixes a subtle ScaleAxis edge case in C#, they must remember to fix it in C++ too.
- **Potential behavioral drift.** Over time, the two implementations could diverge in subtle ways (rounding, error handling, edge cases) even with shared docs.
- **Knowledge duplication in code.** The ScaleAxis function, the context creation logic, the Y-flip — all written twice. The docs prevent someone from having to *discover* the knowledge, but they still have to *implement* it twice.
- **More total code.** Two implementations of ~400 lines each instead of one ~400-line native implementation + thin wrappers.

### Why the cons are manageable

**"Two implementations to maintain"** — The Wintab API is stable. It hasn't changed meaningfully since the 1.4 spec. The session logic we wrote is unlikely to need frequent changes. Once both implementations are correct and tested, maintenance is minimal.

**"Potential behavioral drift"** — Mitigated by:
- Shared test scenarios documented in the repo
- Same diagnostic log format — diff the logs from both implementations to verify identical behavior
- Same PenPoint field layout — the output is directly comparable
- The C# implementation serves as the reference; the C++ implementation is verified against it

**"Knowledge duplication in code"** — The functions are short. ScaleAxis is 10 lines. Context creation is ~50 lines. The total duplicated logic is maybe 200 lines. The documentation (which exists regardless of approach) is far larger than the code.

### When Option 1 wins instead

Option 1 (single native DLL) is better when:
- There are **many** consuming languages (5+) and maintaining a binding per language is cheaper than maintaining a full implementation
- The session logic is **complex and changing frequently** — a single source of truth prevents drift
- The consuming apps are **thin** — they don't have their own significant codebases that benefit from same-language debugging
- **Performance** of the packet handling path is critical and must be identical across all consumers

### When Option 2 wins instead

Option 2 (two implementations) is better when:
- There are **two language ecosystems** (managed and native) with clear boundaries
- The session logic is **stable and well-documented** — the risk of drift is low
- Developer experience (debugging, packaging, IDE support) matters more than code deduplication
- Each ecosystem has **different ergonomic needs** (NuGet vs CMake, IDisposable vs RAII)
- The team prefers **simplicity over cleverness** — two straightforward implementations over one with FFI plumbing

### Recommendation

**Option 2 is the pragmatic choice given the current state of the project.** The Wintab knowledge is now thoroughly documented. The C# implementation is proven. Writing the C++ implementation from the existing docs and reference code is straightforward — a developer who reads the documentation and the C# source can produce a correct C++ implementation in a day.

The cost of maintaining two ~400-line implementations is far lower than the cost of the FFI boundary, ABI stability constraints, mixed-mode debugging, and deployment complexity that Option 1 introduces.

**However, revisit if:**
- A third language ecosystem emerges (e.g., a Python binding is needed)
- The session logic grows significantly in complexity
- A critical bug is found that takes weeks to fix in both implementations

At that point, the single-DLL approach may become worth the overhead.

---

## Option 2 Implementation Plan

A phased approach that builds understanding progressively — each step informs the next.

### Phase 1: Clean up WintabDN

**Goal:** Solid foundation before splitting anything off.

Tasks:
- Address any remaining issues (there's at least one topic still unchecked)
- Reorganize CS files — some files contain many types bundled together. Split logically without exploding file count. The `_Internal.cs` separation is a start; there may be more to do.
- Ensure `PenPoint`, `WintabSession`, and the public API surface are stable and well-documented
- Final pass on the docs

**Why this matters:** Phase 2 forks from this codebase. Anything messy now gets duplicated later.

### Phase 2: Split WintabSession into its own project ✅ DONE

**Goal:** Decouple `WintabSession` from the full WintabDN library.

**Completed.** The session is now a separate project (`WintabSession/`) in namespace `WintabSession`, referencing WintabDN via fully-qualified names to make the dependency boundary explicit.

```
WintabSession.dll (namespace: WintabSession)
  ├── WintabSession, PenPoint, InputApi, WintabResolution
  ├── PenButtonAction, PenButtonNumber, PenCursorType
  └── References WintabDN.dll (all calls fully-qualified: WintabDN.WintabInfo.*, etc.)

WintabDN.dll (namespace: WintabDN)
  ├── WintabInfo, WintabContext, WintabData, WintabExtensions
  ├── WintabNative, MessageEvents, WintabMarshalling
  └── WintabLog, WintabException, WintabDpiHelper
```

**Dependency surface revealed** — WintabSession uses these WintabDN types:
- `WintabDN.WintabInfo` — `IsWintabAvailable()`, `GetDefaultSystemContext()`, `GetMaxPressure()`
- `WintabDN.WintabContext` — `Open()`, `Close()`, `Refresh()`, property accessors
- `WintabDN.WintabData` — constructor, `AddPacketHandler()`, `RemovePacketHandler()`, `GetPacket()`
- `WintabDN.MessageReceivedEventArgs` — packet handler signature
- `WintabDN.ContextOptions` — CXO_SYSTEM, CXO_MESSAGES flags
- `WintabPacket` — read pkX, pkY, pkNormalPressure, pkOrientation, pkZ, pkButtons, pkCursor, pkStatus, pkContext

### Phase 5: Internalize WintabDN dependency (deferred)

**Goal:** The managed session project calls Wintab directly, removing the WintabDN dependency. Deferred until after the unmanaged path (Phases 3–4) is proven.

Replace each WintabDN usage with direct Wintab API calls:

| WintabDN usage | Direct replacement |
|---|---|
| `WintabInfo.IsWintabAvailable()` | `LoadLibrary("Wintab32.dll")` check |
| `WintabInfo.GetDefaultSystemContext()` | `WTInfoA(WTI_DEFSYSCTX, 0, &logContext)` |
| `WintabInfo.GetMaxPressure()` | `WTInfoA(WTI_DEVICES, DVC_NPRESSURE, &axis)` |
| `WintabContext.Open()` | `WTOpenA(hwnd, &logContext, TRUE)` |
| `WintabContext.Close()` | `WTClose(hCtx)` |
| `WintabContext.Refresh()` | `WTGetA(hCtx, &logContext)` |
| `WintabData.GetPacket()` | `WTPacket(hCtx, serial, &pkt)` |
| `MessageEvents` | Create a hidden message window + message pump thread |
| `AddPacketHandler` | Register for WT_PACKET in the message window's WndProc |

The message window/pump is the most complex piece — it's what `MessageEvents` does in WintabDN. For the managed version, this stays as C# Win32 interop (RegisterClassEx, CreateWindowEx, GetMessage/DispatchMessage on a background thread — the same pattern MessageEvents already uses).

**After this phase:**
- `WintabSession.dll` is standalone — no WintabDN dependency
- It loads `Wintab32.dll` dynamically and calls the Wintab API directly
- The public API (`WintabSession`, `PenPoint`, `DrainPoints()`, etc.) is unchanged
- Apps that only need pen input don't need the full WintabDN library
- Apps that need extensions (ExpressKeys, Touch Rings) still use WintabDN separately

**What this reveals:** The exact set of Wintab API calls needed, the message pump pattern, the struct layouts. This is the blueprint for the unmanaged version.

### Phase 3: Build the unmanaged WintabSession

**Goal:** A native WintabSession library for C++/Rust/Zig consumers.

By this point we know:
- Exactly which Wintab functions to call and in what order
- The LOGCONTEXT struct fields that matter
- The PACKET struct layout
- The message pump threading pattern
- The ScaleAxis formula and Y-flip logic
- The PenPoint output format

The C# Phase 2 code (with fully-qualified WintabDN calls) is a line-by-line translation guide.

#### C++ vs Rust

| Consideration | C++ | Rust |
|---|---|---|
| **Wintab SDK compatibility** | Native fit — Wintab SDK ships C/C++ headers | Need hand-written FFI bindings or bindgen on the headers |
| **Win32 API ergonomics** | Native — RegisterClassEx, CreateWindowEx, message pump are standard C/Win32 | Requires `windows` crate or raw FFI. Verbose but works. |
| **Build system** | CMake or MSBuild. Well-understood on Windows. | Cargo. Excellent, but less common for Windows-only native DLLs. |
| **C ABI export** | Natural — `extern "C" __declspec(dllexport)` | `#[no_mangle] extern "C"` — works but requires explicit unsafe |
| **Memory safety** | Manual. Buffer overflows in the message pump or packet handling are possible. | Compiler-enforced. The unsafe blocks are isolated to FFI calls. |
| **Toolchain on Windows** | MSVC (Visual Studio), Clang, MinGW | rustup + MSVC target. Needs Visual Studio build tools for linking. |
| **Debugging** | Full Visual Studio native debugging | VS Code + CodeLLDB, or Visual Studio with limited Rust support |
| **Team familiarity** | You're planning C++ apps anyway | Learning curve if new to Rust, but the codebase is small (~400 lines) |
| **Consuming from C++** | Direct — same language, include header | Via C ABI — same as consuming any other C DLL |
| **Consuming from Rust** | Via C ABI (FFI) | Direct — same language, `use wintab_session;` |
| **Consuming from Zig** | Via C ABI (`@cImport`) | Via C ABI (`@cImport` on the C header that wraps the Rust lib) |
| **Consuming from C#** | P/Invoke on the C ABI exports | P/Invoke on the C ABI exports (identical) |
| **Testing** | googletest, Catch2, or ad-hoc | `cargo test` — built-in, excellent. But Wintab tests need a real tablet. |
| **CI/CD** | MSBuild in CI. Standard. | `cargo build` in CI. Straightforward. Needs MSVC linker. |
| **Binary size** | Small (~50KB for this scope) | Small with `opt-level = "z"` and `strip = true` (~100KB) |
| **Long-term maintenance** | Manual memory management risk in future changes | Compiler catches most issues in future changes |

#### Recommendation

**C++ is the lower-risk choice** given:
- Wintab is a C API with C headers — C++ is the natural host
- You're already planning C++ apps that will consume the library
- The Win32 message pump pattern is standard C/Win32 code
- Visual Studio provides seamless native debugging
- The codebase is small enough (~400 lines) that C++'s memory safety risks are manageable

**Rust is the more interesting choice** given:
- Memory safety for the message pump thread (the trickiest part)
- `cargo test` for automated testing
- Excellent cross-compilation support if you later target other platforms
- The `windows` crate provides ergonomic Win32 bindings
- ~400 lines of Rust is an approachable learning project
- If you're going to write Rust apps anyway, having the tablet library in Rust avoids FFI in the most common consumer

**Practical suggestion:** Start with C++. It's the shortest path from the C# code to a working native DLL. If you later want a Rust version, the C++ version serves as yet another reference implementation — at that point you'd have three references (docs, C#, C++) to guide the Rust port.

### Phase 4: Build an unmanaged scribble app

**Goal:** Prove the native WintabSession works end-to-end.

Options for the app framework:

| Framework | Language | Rendering | Effort |
|---|---|---|---|
| Raw Win32 + GDI/GDI+ | C++ | Bitmap-backed drawing (like WinForms) | Low — minimal framework overhead |
| Raw Win32 + Direct2D | C++ | GPU-accelerated | Medium — more setup, better performance |
| SDL2 | C++ or Rust | Cross-platform rendering | Medium — good abstraction, well-documented |
| Dear ImGui + Direct3D | C++ | Immediate-mode UI | Medium — fast iteration, popular for tools |
| winit + wgpu | Rust | GPU-accelerated, cross-platform | Medium-high — Rust ecosystem, modern |
| Zig + win32 | Zig | Raw Win32 | Medium — proves Zig consumption works |

**Recommended:** Raw Win32 + GDI+ (C++) for the first native scribble app. It's the closest analogue to the WinForms ScribbleRenderer — bitmap-backed drawing with a message pump. Minimal dependencies, straightforward debugging, proves the DLL works.

The app structure mirrors the WinUI3 scribble:
1. Create a window with a drawing area
2. Create a WintabSession, start in digitizer mode
3. On a timer (WM_TIMER at ~60fps):
   - `WintabSession_DrainPoints()` into a buffer
   - Convert desktop coords to client coords (`ScreenToClient`)
   - Draw line segments to a backing bitmap (GDI+)
   - `InvalidateRect` to trigger repaint
4. On WM_PAINT: blit the bitmap to the window

This is ~200 lines of Win32 boilerplate + ~50 lines of drawing logic.

### Phase summary

| Phase | Deliverable | Depends on | Status |
|---|---|---|---|
| 1. Clean up WintabDN | Solid C# codebase, stable API | — | ✅ Done |
| 2. Split WintabSession project | Separate DLL, fully-qualified WintabDN calls | Phase 1 | ✅ Done |
| 3. Unmanaged WintabSession | Native DLL (C++) with C ABI | Phase 2 (C# as reference) | ✅ Done |
| 4. Unmanaged scribble app | Scribble.Win32 with ribbon UI, DPI-aware, double-buffered | Phase 3 | ✅ Done |
| 5. Unified Pen Session | `IPenSession` with 7 backends, 5 packages, 7 scribble apps across 6 frameworks + 2 languages | Phases 2, 3 | ✅ Done |
| 6. WinForms + Cleanup | PenSession.WinForms (IMessageFilter), Scribble.WinForms, removed LegacyPenTestApp and WintabSession project | Phase 5 | ✅ Done |
| 7. Repo split | Create `WinPenSession` repo for PenSession + Scribble apps; keep `Wacom_WinTabDN` for WintabDN only | Phase 6 | Not started |
| 8. GitHub Actions CI/Release | Automated builds, artifact packaging, release on `release/v*` tags | Phase 7 | Not started |
| 9. NuGet Publishing | Package metadata, push to nuget.org, pre-release CI builds | Phase 8 | Not started |

Each phase is independently valuable:
- After Phase 1: better codebase for all current apps
- After Phase 2: cleaner dependency graph, session is a reusable package
- After Phase 3: native apps can use WintabSession
- After Phase 4: proven end-to-end native pipeline
- After Phase 5: paint apps can switch between input APIs at runtime without code changes
- After Phase 6: WinForms is a first-class framework; legacy projects removed; clean codebase
- After Phase 7: separate repos with clear ownership — WintabDN (low-level) and WinPenSession (unified SDK)
- After Phase 8: automated builds and downloadable releases on every tag
- After Phase 9: any developer can `dotnet add package PenSession` and start building

#### Phase 5: Unified Pen Session ✅ Done

**Goal:** Abstract over multiple Windows pen input APIs so a paint app can switch between them at runtime.

**Delivered — Libraries:**
- `IPenSession` interface with `Start(hwnd)`, `Stop()`, `DrainPoints()`, polling model
- 7 backend implementations across 5 C# packages + 1 C++ DLL:
  - `PenSession.dll` — WintabSystem, WintabDigitizer, WmPointer (framework-agnostic)
  - `PenSession.WinUI.dll` — WinUiPointer (WinUI 3 XAML events)
  - `PenSession.Wpf.dll` — WpfStylus (WPF StylusMove events)
  - `PenSession.Avalonia.dll` — AvaloniaPointer (Avalonia pointer events)
  - `PenSession.WinForms.dll` — WinFormsPointer (IMessageFilter WM_POINTER interception)
  - `PenSession.Native.dll` (C++) — pen_session C API with Wintab + WM_Pointer backends
- `PenSessionFactory` with runtime API discovery
- `PenSession.TestConsole` for headless Wintab testing
- PenPoint carries both tilt representations (Azimuth/Altitude + TiltX/TiltY), all in tenths of degree
- Runtime API switching via dropdown — no restart required (unlike Krita/Qt)
- Rust FFI bindings with safe `PenSession` wrapper and RAII `Drop`

**Delivered — Scribble apps (7 apps, 6 frameworks, 2 languages):**

| App | Language | Framework | Renderer | Backends |
|---|---|---|---|---|
| Scribble.Win32 | C++ | Win32 | GDI | System, Digitizer, WM_Pointer |
| Scribble.Rust | Rust | egui | tiny-skia | System, Digitizer, WM_Pointer |
| Scribble.WinUI | C# | WinUI 3 | SkiaSharp | System, Digitizer, WinUI Pointer |
| Scribble.Wpf | C# | WPF | SkiaSharp | System, Digitizer, WPF Stylus |
| Scribble.WinForms | C# | WinForms | SkiaSharp | System, Digitizer, WinForms Pointer |
| Scribble.Avalonia | C# | Avalonia | SkiaSharp | System, Digitizer, Avalonia Pointer |
| PenSession.TestConsole | C# | Console | N/A | System, Digitizer |

All apps feature: ribbon toolbar with API dropdown, brush size slider, clear button, pressure-sensitive drawing, and four-coordinate position display (Raw → Screen → App → Canvas).

**Key discoveries:**
- **Framework-specific input routing:** WM_POINTER subclassing only works in raw Win32 apps. WinUI 3, WPF, and Avalonia intercept pen input through their own stacks, requiring framework-specific session implementations.
- **WM_POINTER event coalescing:** `GetPointerPenInfoHistory` must use `count > 1` (not `count > 0`). The `count == 1` history path returns subtly different data that causes silent data loss.
- **HCTX is pointer-sized:** Using `uint` (4 bytes) instead of `IntPtr` (8 bytes on x64) in the PACKET struct silently shifts all fields.
- **Wintab/WM_POINTER driver conflict:** Once a Wintab context has been opened, some drivers suppress WM_POINTER for the process lifetime — but this was NOT observed in our testing (both coexist when sessions are stopped/started cleanly).
- **SkiaSharp standardized:** All managed scribble apps use SkiaSharp for bitmap-backed rendering (SKBitmap → WriteableBitmap). Scribble.Rust uses tiny-skia (pure Rust equivalent). Scribble.Win32 uses GDI (zero dependencies).

See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the full design, framework-specificity analysis, Qt/Krita comparison, and integration guides.

#### Phase 6: WinForms Support + Cleanup ✅ Done

**Goal:** Add WinForms as a first-class framework with its own PenSession extension, replace the legacy apps, and remove superseded projects.

**Delivered:**
- `PenSession.WinForms.dll` — `WinFormsPointerSession` using `IMessageFilter` to intercept `WM_POINTER` messages application-wide
- `Scribble.WinForms` — new WinForms scribble app with SkiaSharp rendering, FlowLayoutPanel ribbon, System/Digitizer/WinForms Pointer backends
- Removed `LegacyPenTestApp` — replaced by `Scribble.WinForms`
- Removed `WintabSession` project — superseded by `PenSession`

**Key discovery — NativeWindow.AssignHandle crash:** WinForms' `NativeWindow.AssignHandle` on a Form HWND causes a crash (exit code -1) because WinForms internally creates its own `NativeWindow` for each Form. Calling `AssignHandle` with the same HWND conflicts with WinForms' internal window procedure ownership. `SetWindowSubclass` also doesn't work reliably in WinForms. The solution is `IMessageFilter`, which intercepts messages at the application message pump level without touching HWND ownership. This is registered via `Application.AddMessageFilter()` and receives all messages before they reach any window procedure.

#### Phase 7: Repo Split

**Goal:** Separate the unified pen input SDK from the low-level Wintab library into distinct repositories.

- **`WinPenSession`** (new repo) — PenSession libraries, all Scribble apps, PenSession.Native, PenSession.TestConsole, all design/architecture docs. Fresh start (Option D) with key docs carrying the design rationale. README links back to Wacom_WinTabDN for historical context.
- **`Wacom_WinTabDN`** (existing repo) — WintabDN library, ExtensionTestApp. The original low-level Wintab .NET wrapper with all the improvements made during Phase 1.

The split is clean because PenSession has zero dependency on WintabDN — it has its own P/Invoke layer.

#### Phase 8: GitHub Actions CI/Release

**Goal:** Automated builds and downloadable release artifacts on every tagged release.

- `.github/workflows/build.yml` — builds all projects (C#, C++, Rust) on `windows-latest`
- Triggered by `release/v*` tags
- Produces artifacts: managed DLLs, native DLL, Scribble app binaries
- GitHub Releases with downloadable zip files

#### Phase 9: NuGet Publishing

**Goal:** Publish PenSession packages to nuget.org.

- Package metadata in each .csproj (PackageId, Version, Description, License)
- `dotnet pack` produces .nupkg files for PenSession, PenSession.WinUI, PenSession.Wpf, PenSession.WinForms, PenSession.Avalonia
- Optionally PenSession.Native.nupkg with the C++ DLL and headers
- GitHub Actions pushes to nuget.org on `release/v*` tags
- Pre-release packages on main branch pushes (`0.0.0-ci.N`)

See [FUTURES_NUGET.md](FUTURES_NUGET.md) for the full publishing plan.

---

## Open Questions

1. **Should the native DLL also handle desktop → canvas conversion?** Currently this is framework-specific (ClientToScreen + DPI for WinUI3, PointToClient for WinForms). The native DLL could accept an HWND and compute canvas-relative coordinates, but this ties it to Win32 windowing concepts that may not apply to all consumers (e.g., a headless recording tool).

2. **Should the native DLL support multiple simultaneous sessions?** The current C# implementation creates one session at a time. Multiple sessions would require multiple Wintab contexts and careful overlap management.

3. **Should the native DLL expose extension control (ExpressKeys, Touch Rings)?** This is a separate concern from pen input. It could be a separate DLL or a separate set of API functions in the same DLL.

4. **What about the ChatGPT dual-context approach?** We discovered that opening a system context disabled (for mapping reference) alongside a digitizer context (for hi-res packets) caused the Wacom driver to stop delivering packets. The native DLL should document this and use the proven single-context approach (system context with tablet-native OutExt override).

5. **License implications?** The current code is MIT-licensed Wacom sample code. A native DLL would be a new work — confirm it can be distributed under the same license.
