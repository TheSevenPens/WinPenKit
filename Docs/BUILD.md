# Building WinPenKit

## Prerequisites

- **Visual Studio 2022+** (or newer) with:
  - .NET 10 SDK
  - C++ Desktop Development workload (for WinPenKit.Native + Scribble.Win32)
  - Windows App SDK (for Scribble.WinUI)
- **Rust toolchain** (for Scribble.Rust): install via [rustup](https://rustup.rs/)

## .NET Projects

Open `WinPenKit.slnx` in Visual Studio, or from the command line:

```bash
dotnet build WinPenKit.slnx
```

This builds all managed projects except WinUI: WinPenKit, WinPenKit.Wpf, WinPenKit.WinForms, WinPenKit.Avalonia, WinPenKit.TestConsole, and the corresponding Scribble apps.

**WinUI projects** (WinPenKit.WinUI, Scribble.WinUI) require Visual Studio or `msbuild` to build. From the command line:

```bash
msbuild WinPenKit.WinUI/WinPenKit.WinUI.csproj -p:Configuration=Debug -p:Platform=x64 -restore
msbuild Scribble.WinUI/Scribble.WinUI.csproj -p:Configuration=Debug -p:Platform=x64 -restore
```

## C++ Projects

Open `WinPenKitNative.sln` in Visual Studio, or from the command line:

```bash
msbuild WinPenKitNative.sln -p:Configuration=Debug -p:Platform=x64
```

This builds WinPenKit.Native (the C ABI DLL) and Scribble.Win32. The solution has a project dependency so WinPenKit.Native builds first, and a post-build step copies `WinPenKit.Native.dll` to Scribble.Win32's output directory.

## Rust Project

```bash
cd Scribble.Rust
cargo build
```

The Rust build script (`build.rs`) expects `WinPenKit.Native.lib` in `../WinPenKit.Native/bin/Debug/x64/`. Build the C++ projects first.

You also need to copy the DLL to the Rust output directory:

```bash
cp WinPenKit.Native/bin/Debug/x64/WinPenKit.Native.dll Scribble.Rust/target/debug/
```

## Build Order

If building everything from scratch:

1. **C++ first** — `WinPenKitNative.sln` (produces WinPenKit.Native.dll/.lib)
2. **.NET** — `WinPenKit.slnx` (all managed projects)
3. **Rust** — `cargo build` in Scribble.Rust (links against WinPenKit.Native.lib)

## Output Locations

| Project | Output |
|---|---|
| WinPenKit.Native | `WinPenKit.Native/bin/Debug/x64/WinPenKit.Native.dll` |
| Scribble.Win32 | `Scribble.Win32/bin/Debug/x64/ScribbleCpp.exe` |
| Scribble.Rust | `Scribble.Rust/target/debug/scribble-rust.exe` |
| Managed projects | `{Project}/bin/Debug/net10.0-windows/` |

## Rebuild All (C++)

If you get a linker error `LNK1104: cannot open file 'WinPenKit.Native.lib'` during Rebuild All, it's a timing issue — Scribble.Win32 tried to link before WinPenKit.Native finished. This is fixed by the `ProjectReference` in Scribble.Win32's vcxproj. A regular Build (not Rebuild) always works.
