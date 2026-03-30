# Futures

Known issues, planned improvements, and ideas for the future.

## Known Issues

### WPF rendering stutter
Scribble.Wpf has slight stutter compared to other scribble apps despite using the same SkiaSharp approach. Possible causes: `PointFromScreen` overhead per point, `DispatcherTimer` jitter, `Buffer.MemoryCopy` blocking, WPF compositor latency. Options: `SKElement`, `CompositionTarget.Rendering`, dirty-region copying, Direct2D interop. See [Planning/FUTURES_WPF_RENDERING.md](Planning/FUTURES_WPF_RENDERING.md).

### egui ribbon column width
Scribble.Rust's ribbon columns shift width when telemetry values change (e.g., position goes from 3 to 5 digits). Mitigated with `exact_height(130.0)` but columns still shift horizontally. Fix: monospace font for values, or `egui::Grid` with fixed column widths.

### Wintab/WM_POINTER process-level interaction
The interaction between Wintab and WM_POINTER within a single process is driver-dependent. In our testing, runtime switching works cleanly. But some driver versions may suppress one API once the other has been used. Not fully characterized across all drivers.

## Planned Work

### GitHub Actions CI/Release (Phase 8)
Automated builds for all projects (C#, C++, Rust) on `windows-latest`. Triggered by `release/v*` tags. Produces downloadable release artifacts.

### NuGet Publishing (Phase 9)
Publish PenSession packages to nuget.org. Package metadata in each .csproj, versioning from git tags, pre-release CI builds. See [Planning/FUTURES_NUGET.md](Planning/FUTURES_NUGET.md).

## Ideas

### RealTimeStylus session
COM-based pen input API. Lower latency than WM_POINTER (sync plugins run on the pen thread). Only worth implementing if a specific latency advantage is demonstrated.

### Stroke smoothing / interpolation
PenPoint streams are raw samples. Drawing apps typically apply Catmull-Rom or cubic Bézier interpolation for smoother curves. This could be a shared utility in PenSession rather than per-app logic.

### Pressure curve mapping
Map raw pressure to a response curve (linear, S-curve, logarithmic) before the brush engine sees it. Common in drawing apps, could be a PenSession utility.

### Multi-touch discrimination
WM_POINTER supports both pen and touch. PenSession currently filters for `PT_PEN` only. Supporting touch alongside pen (palm rejection, gesture recognition) is a separate concern but uses the same API infrastructure.

### ARM64 support
PenSession.Native currently builds for x64 only. ARM64 build configuration would support Surface Pro X and Snapdragon laptops. The code is architecture-neutral — just needs the build target added.

### Remove WintabDN and ExtensionTestApp
WintabDN is only needed for ExtensionTestApp (tablet extensions). If extension support is added to PenSession directly, both can be removed, eliminating the last legacy dependency.

### Cross-platform (via octotablet)
PenSession is Windows-only. For cross-platform pen input, [octotablet](https://github.com/Fuzzyzilla/octotablet) (Rust) is the closest equivalent. A future `PenSession.Linux` or `PenSession.macOS` could wrap platform-native APIs, but this is a major scope expansion.
