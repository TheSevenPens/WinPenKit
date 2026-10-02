# Getting Started

## Prerequisites

A Wacom tablet driver is needed for Wintab support. If no Wintab driver is available, the apps will still work with the built-in Windows Pointer support.

See [BUILD.md](BUILD.md) for build instructions, prerequisites, and build order.

## Projects

### WinPenKit Libraries

| Package | Purpose | Works in |
|---|---|---|
| **WinPenKit** | Core library — Wintab System, Wintab Digitizer, WM_POINTER | Any .NET app |
| **WinPenKit.Native** | C++ DLL with C ABI — same backends | Any native app (C++, Rust, Zig) |
| **WinPenKit.WinUI** | WinUI 3 pointer events | WinUI 3 apps |
| **WinPenKit.Wpf** | WPF stylus events | WPF apps |
| **WinPenKit.WinForms** | WinForms `IMessageFilter` | WinForms apps |
| **WinPenKit.Avalonia** | Avalonia pointer events | Avalonia apps |

### Scribble Apps

Eight demo apps. Seven use WinPenKit, across C#, C++ and Rust: Scribble.Win32, Scribble.Rust, Scribble.WinUI, Scribble.WinUINative, Scribble.Wpf, Scribble.WinForms and Scribble.Avalonia. They feature bitmap-backed rendering, ribbon UI, runtime API switching, and four-coordinate position display. The eighth, Scribble.Qt, uses Qt's own tablet support and no WinPenKit, as an independent comparison.

See [SCRIBBLE-APPS.md](SCRIBBLE-APPS.md) for details on each app.

### Other Projects

| Project | Purpose |
|---|---|
| **WinPenKit.TestConsole** | Console app for testing Wintab backends, with the clock self-test and the Wintab epoch and mapping probes |
| **WinPenKit.MappingWizard** | Checks whether each pen API puts the pen under the cursor across display and tablet configurations. See [MAPPING-WIZARD.md](MAPPING-WIZARD.md) |
| **Samples/ContextCount** | Plain C programs and scripts that read the Wintab driver's context counters. See [WINTAB-CONTEXT-LEAK.md](WINTAB-CONTEXT-LEAK.md) |

## See Also

- [ARCHITECTURE.md](ARCHITECTURE.md): How WinPenKit works, and its design decisions
- [HOW_TO_USE.md](HOW_TO_USE.md): Usage guide with gotchas and best practices
- [STYLUS.md](STYLUS.md): The Wintab and WM_POINTER input paths
- [TIMESTAMPS.md](TIMESTAMPS.md): Timestamp measurements per backend
- [SELF-TEST.md](SELF-TEST.md): The `--selftest` and `--replay` checks
- [MAPPING-WIZARD.md](MAPPING-WIZARD.md): Checking pen position against the cursor
- [devnotes](https://github.com/TheSevenPens/devnotes): General pen input knowledge (API comparisons, DPI handling, Wintab gotchas)
- [Wintab Basics](https://developer-docs.wacom.com/docs/icbt/windows/wintab/wintab-basics/): Wacom's Wintab documentation
