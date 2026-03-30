# 

**Status: Phases 1–6 complete. Phase 7 remaining.** This document tracks the phased implementation plan. What began as a speculative design has been fully implemented across managed (.NET) and unmanaged (C++, Rust) applications.

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
