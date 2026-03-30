# 

**Status: Phases 1–7 complete. ** This document tracks the phased implementation plan. What began as a speculative design has been fully implemented across managed (.NET) and unmanaged (C++, Rust) applications.

### Phase summary

| Phase | Deliverable | Depends on | Status |
|---|---|---|---|
| 1. Clean up WintabDN | Solid C# codebase, stable API | — | ✅ Done |
| 2. Split WintabSession project | Separate DLL, fully-qualified WintabDN calls | Phase 1 | ✅ Done |
| 3. Unmanaged WintabSession | Native DLL (C++) with C ABI | Phase 2 (C# as reference) | ✅ Done |
| 4. Unmanaged scribble app | Scribble.Win32 with ribbon UI, DPI-aware, double-buffered | Phase 3 | ✅ Done |
| 5. Unified Pen Session | `IPenSession` with 7 backends, 5 packages, 7 scribble apps across 6 frameworks + 2 languages | Phases 2, 3 | ✅ Done |
| 6. WinForms + Cleanup | PenSession.WinForms (IMessageFilter), Scribble.WinForms, removed LegacyPenTestApp and WintabSession project | Phase 5 | ✅ Done |
| 7. Repo split | Create `WinPenSession` repo for PenSession + Scribble apps; keep `Wacom_WinTabDN` for WintabDN only | Phase 6 | ✅ Done |
| 8. GitHub Actions CI/Release | Automated builds, artifact packaging, release on `release/v*` tags | Phase 7 | Not started |
| 9. NuGet Publishing | Package metadata, push to nuget.org, pre-release CI builds | Phase 8 | Not started |

See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the full design, framework-specificity analysis, Qt/Krita comparison, and integration guides.

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
