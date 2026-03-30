# Building WinPenSession

## Prerequisites

- **Visual Studio 2022+** (or newer) with:
  - .NET 10 SDK
  - C++ Desktop Development workload (for PenSession.Native + Scribble.Win32)
  - Windows App SDK (for Scribble.WinUI)
- **Rust toolchain** (for Scribble.Rust): install via [rustup](https://rustup.rs/)

## .NET Projects

Open `WinPenSession.slnx` in Visual Studio, or from the command line:

```bash
dotnet build WinPenSession.slnx
```

This builds all managed projects except WinUI: PenSession, PenSession.Wpf, PenSession.WinForms, PenSession.Avalonia, PenSession.TestConsole, and the corresponding Scribble apps.

**WinUI projects** (PenSession.WinUI, Scribble.WinUI) require `msbuild` due to PRI resource generation — they cannot be built with the `dotnet` CLI. Build them from Visual Studio or with:

```bash
msbuild PenSession.WinUI/PenSession.WinUI.csproj -p:Configuration=Debug -p:Platform=x64 -restore
msbuild Scribble.WinUI/Scribble.WinUI.csproj -p:Configuration=Debug -p:Platform=x64 -restore
```

## C++ Projects

Open `NativeCpp.sln` in Visual Studio, or from the command line:

```bash
msbuild NativeCpp.sln -p:Configuration=Debug -p:Platform=x64
```

This builds PenSession.Native (the C ABI DLL) and Scribble.Win32. The solution has a project dependency so PenSession.Native builds first, and a post-build step copies `PenSession.Native.dll` to Scribble.Win32's output directory.

## Rust Project

```bash
cd Scribble.Rust
cargo build
```

The Rust build script (`build.rs`) expects `PenSession.Native.lib` in `../PenSession.Native/bin/Debug/x64/`. Build the C++ projects first.

You also need to copy the DLL to the Rust output directory:

```bash
cp PenSession.Native/bin/Debug/x64/PenSession.Native.dll Scribble.Rust/target/debug/
```

## Build Order

If building everything from scratch:

1. **C++ first** — `NativeCpp.sln` (produces PenSession.Native.dll/.lib)
2. **.NET** — `WinPenSession.slnx` (all managed projects)
3. **Rust** — `cargo build` in Scribble.Rust (links against PenSession.Native.lib)

## Output Locations

| Project | Output |
|---|---|
| PenSession.Native | `PenSession.Native/bin/Debug/x64/PenSession.Native.dll` |
| Scribble.Win32 | `Scribble.Win32/bin/Debug/x64/ScribbleCpp.exe` |
| Scribble.Rust | `Scribble.Rust/target/debug/scribble-rust.exe` |
| Managed projects | `{Project}/bin/Debug/net10.0-windows/` |

## Rebuild All (C++)

If you get a linker error `LNK1104: cannot open file 'PenSession.Native.lib'` during Rebuild All, it's a timing issue — Scribble.Win32 tried to link before PenSession.Native finished. This is fixed by the `ProjectReference` in Scribble.Win32's vcxproj. A regular Build (not Rebuild) always works.
