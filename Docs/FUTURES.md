# Futures

Known issues, planned improvements, and ideas for the future.

## Known Issues

### Wintab/WM_POINTER process-level interaction
The interaction between Wintab and WM_POINTER within a single process is driver-dependent. In our testing, runtime switching works cleanly. But some driver versions may suppress one API once the other has been used. Not fully characterized across all drivers.

### Capture region: rectangle-only filtering
`IPenSession.CaptureRegion` scopes capture by a screen **rectangle**, so a point passes whenever it falls within the region's bounds — even if another window is stacked on top of that area. There is no occlusion / z-order test yet. For the common case (scoping Wintab's desktop-global capture down to the app's own canvas) this is sufficient; true hit-testing against the window stack is future work. See [STYLUS.md → Spatial Scope](STYLUS.md#spatial-scope-capture-region).

## Planned Work

- **NuGet Publishing** — Publish WinPenKit packages to nuget.org. See [Planning/FUTURES_NUGET.md](Planning/FUTURES_NUGET.md).

## Ideas

### RealTimeStylus session
COM-based pen input API. Lower latency than WM_POINTER (sync plugins run on the pen thread). Only worth implementing if a specific latency advantage is demonstrated.

### Stroke smoothing / interpolation
PenPoint streams are raw samples. Drawing apps typically apply Catmull-Rom or cubic Bézier interpolation for smoother curves. This could be a shared utility in WinPenKit rather than per-app logic.

### Pressure curve mapping
Map raw pressure to a response curve (linear, S-curve, logarithmic) before the brush engine sees it. Common in drawing apps, could be a WinPenKit utility.

### Multi-touch discrimination
WM_POINTER supports both pen and touch. WinPenKit currently filters for `PT_PEN` only. Supporting touch alongside pen (palm rejection, gesture recognition) is a separate concern but uses the same API infrastructure.

### ARM64 support
WinPenKit.Native currently builds for x64 only. ARM64 build configuration would support Surface Pro X and Snapdragon laptops. The code is architecture-neutral — just needs the build target added.

### Remove WintabDN and ExtensionTestApp
WintabDN is only needed for ExtensionTestApp (tablet extensions). If extension support is added to WinPenKit directly, both can be removed, eliminating the last legacy dependency.

### Cross-platform (via octotablet)
WinPenKit is Windows-only. For cross-platform pen input, [octotablet](https://github.com/Fuzzyzilla/octotablet) (Rust) is the closest equivalent. A future `WinPenKit.Linux` or `WinPenKit.macOS` could wrap platform-native APIs, but this is a major scope expansion.

## Open Questions

1. **Unified button model.** PenPoint stores raw `Buttons` field. Wintab uses relative encoding `(action << 16) | buttonNumber`; WM_POINTER and the framework backends use absolute flag bitmasks. `PenButtonTracker` (added 2026-04) hides this difference for consumers — feed every PenPoint to the tracker and read `IsTipDown` / `IsBarrelDown(n)` / `IsEraser` regardless of source. The wire format itself is still split, and the per-button-identity ceiling stands: pointer-style backends only carry a single barrel flag, so B2/B3 remain Wintab-only. Fully normalizing the wire format (e.g. synthesizing Wintab-style events in non-Wintab backends) is still deferred.

2. **Should the native DLL also handle desktop → canvas conversion?** Currently this is framework-specific (ClientToScreen + DPI for WinUI3, PointToClient for WinForms). The native DLL could accept an HWND and compute canvas-relative coordinates, but this ties it to Win32 windowing concepts that may not apply to all consumers (e.g., a headless recording tool).

3. **Should the native DLL support multiple simultaneous sessions?** The current C# implementation creates one session at a time. Multiple sessions would require multiple Wintab contexts and careful overlap management.

4. **Should the native DLL expose extension control (ExpressKeys, Touch Rings)?** This is a separate concern from pen input. It could be a separate DLL or a separate set of API functions in the same DLL.

5. **What about the ChatGPT dual-context approach?** We discovered that opening a system context disabled (for mapping reference) alongside a digitizer context (for hi-res packets) caused the Wacom driver to stop delivering packets. The native DLL should document this and use the proven single-context approach (system context with tablet-native OutExt override).

6. **License implications?** The current code is MIT-licensed Wacom sample code. A native DLL would be a new work — confirm it can be distributed under the same license.
