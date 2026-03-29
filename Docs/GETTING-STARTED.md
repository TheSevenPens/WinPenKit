# Getting Started

## Prerequisites

A Wacom tablet driver is needed for Wintab support. If no Wintab driver is available, the apps will still work with the built-in Windows Pointer support.

See [BUILD.md](BUILD.md) for build instructions, prerequisites, and build order.

## Projects

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

- [FUTURES_UNIFIED_SESSION.md](Planning/FUTURES_UNIFIED_SESSION.md) — PenSession architecture and design decisions
- [HOW_TO_USE.md](HOW_TO_USE.md) — Usage guide with gotchas and best practices
- [devnotes](https://github.com/TheSevenPens/devnotes) — General pen input knowledge (API comparisons, DPI handling, Wintab gotchas)
- [Wintab Basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/) — Wacom's Wintab documentation
