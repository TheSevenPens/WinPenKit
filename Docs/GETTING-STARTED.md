# Getting Started

## Prerequisites

- A Wacom tablet driver installed (for Wintab support). If no Wintab driver is available, the apps will still work with the built-in Windows Pointer support.
- Visual Studio 2022+ or .NET 10 SDK
- For C++ projects: MSBuild / Visual Studio C++ workload
- For Rust: `cargo` (Rust toolchain)

## Building

1. Open `WinPenSession.slnx` in Visual Studio, or build from the command line with `dotnet build WinPenSession.slnx`.
2. For the C++ projects (PenSession.Native + Scribble.Win32), open `NativeCpp.sln` or build with MSBuild.
3. For Scribble.Rust, run `cargo build` in the `Scribble.Rust` directory.
4. Select an app to run (e.g., Scribble.WinUI, Scribble.WinForms) and launch from Visual Studio or `dotnet run`.

## Projects in the Solution

### PenSession Libraries

| Package | Purpose | Works in |
|---|---|---|
| **PenSession** | Core library — Wintab System, Wintab Digitizer, WM_POINTER | Any .NET app |
| **PenSession.Native** | C++ DLL with C ABI — same backends | Any native app (C++, Rust, Zig) |
| **PenSession.WinUI** | WinUI 3 pointer events | WinUI 3 apps |
| **PenSession.Wpf** | WPF stylus events | WPF apps |
| **PenSession.WinForms** | WinForms `IMessageFilter` | WinForms apps |
| **PenSession.Avalonia** | Avalonia pointer events | Avalonia apps |

### Scribble Apps

Seven demo apps proving the SDK end-to-end across C#, C++, and Rust. All feature bitmap-backed rendering, ribbon UI, runtime API switching, and four-coordinate position display.

See [SCRIBBLE-APPS.md](SCRIBBLE-APPS.md) for details on each app.

### Other Projects

| Project | Purpose |
|---|---|
| **PenSession.TestConsole** | Headless console app for testing Wintab backends |
| **ExtensionTestApp** | WinForms app for tablet extension controls (ExpressKeys, Touch Rings) |
| **WintabDN** | Low-level Wintab .NET library — used by ExtensionTestApp only |

## See Also

- [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) — PenSession architecture and design decisions
- [HOW_TO_USE.md](HOW_TO_USE.md) — Usage guide with gotchas and best practices
- [devnotes](https://github.com/TheSevenPens/devnotes) — General pen input knowledge (API comparisons, DPI handling, Wintab gotchas)
- [Wintab Basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/) — Wacom's Wintab documentation
