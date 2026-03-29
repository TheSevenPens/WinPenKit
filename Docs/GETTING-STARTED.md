# Getting Started 

## Test Environment
The Wintab .NET demo samples have been provided as two C# applications. To build these sample applications, you will need Windows 10 or above with the .NET 10 SDK and Visual Studio 2022 or above.

To test the applications, a Wacom tablet driver must be installed and a supported Wacom tablet must be attached. All Wacom tablets supported by the Wacom driver are supported by this API. Get the driver that supports your device at: https://www.wacom.com/support/product-support/drivers.


## Wintab SDK License  
```
Copyright (c) 2020, Wacom Technology Corporation
 
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
 
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## Install the Wacom tablet driver and verify tablet operation
In order to run the sample applications, it is necessary to install a Wacom tablet driver, which installs the necessary runtime components that support Wintab .NET. The driver can be found at: https://www.wacom.com/support/product-support/drivers

Once the driver has installed and you have rebooted your system, check your tablet driver installation by doing the following:

1. Attach a supported Wacom tablet.
1. Open the Wacom Tablet Properties application (from the Start menu, go to Wacom Tablet >  Wacom Tablet Properties) to confirm your tablet is recognized.
1. Use tablet Pen, ExpressKeys, Touch Ring, and/or Touch Strips to verify functionality.
1. If all of the above checks out, proceed to the next section to build/run the sample application.


## Build/run the sample applications

1. Open the `WintabDN.slnx` file in Visual Studio, or build from the command line with `dotnet build WintabDN.slnx`.
1. There are multiple projects in the solution:
	a. WintabDN (builds WintabDN.dll — the core library)
	b. PenSession (unified pen input abstraction)
	c. PenSession.WinUI (WinUI 3 extension)
	d. PenSession.Wpf (WPF extension)
	e. PenSession.Avalonia (Avalonia extension)
	f. PenSession.WinForms (WinForms extension using IMessageFilter)
	g. Scribble.WinUI (WinUI 3 drawing app)
	h. Scribble.Wpf (WPF drawing app)
	i. Scribble.WinForms (WinForms drawing app with SkiaSharp rendering)
	j. Scribble.Avalonia (Avalonia drawing app)
	k. ExtensionTestApp (ExpressKey/Touch Ring test app)
	l. PenSession.TestConsole (console test app)
1. From the top menu, select Build > Build Solution.
1. Once built, select the application to start (e.g., Scribble.WinUI, Scribble.WinForms, or ExtensionTestApp) and run the project from Visual Studio Local Windows Debugger.
1. As the app starts, there should be no warnings. If you do see warnings, be sure the driver is running with the attached, supported, tablet as described above.


## ExtensionTestApp
The ExtensionTestApp displays available ExpressKeys, Touch Rings (and modes), as well as Touch Strip information.

This application is an example of how to capture user interactions with tablet controls using Wintab .NET.

To only show attached tablets, reset settings from the Wacom Desktop Center Backup settings section.

![Multi-Touch Demo screen](./Media/sc-wdn-gs-xta.png)


## Scribble.WinForms
A WinForms drawing application using the unified `PenSession` library. Features:

* FlowLayoutPanel ribbon toolbar with API selector, brush size slider, clear button, pen telemetry
* **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` draws to an `SKBitmap`, pixel-copied to WinForms `Bitmap` for display
* Runtime API switching: System, Digitizer, WinForms Pointer

Uses `PenSession` + `PenSession.WinForms`. The WinForms Pointer backend uses `IMessageFilter` to intercept `WM_POINTER` messages application-wide (see gotchas below for why `NativeWindow.AssignHandle` cannot be used).

## Scribble.Avalonia
An Avalonia drawing application using the unified `PenSession` library. Features:

* Ribbon toolbar with API selector, brush size slider, clear button, pen telemetry
* **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` draws to an `SKBitmap`, pixel-copied to Avalonia `WriteableBitmap` for display
* Runtime API switching: System, Digitizer, Avalonia Pointer

Uses `PenSession` + `PenSession.Avalonia`. The Avalonia Pointer backend uses Avalonia's native `PointerMoved`/`PointerPressed` events.

## Scribble.Wpf
A WPF drawing application using the unified `PenSession` library. Features:

* Ribbon toolbar with API selector, brush size slider, clear button, pen telemetry
* **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` draws to an `SKBitmap`, pixel-copied to WPF `WriteableBitmap` for display
* Runtime API switching: System, Digitizer, WPF Stylus (WM_Pointer available but non-functional in WPF — see framework-specificity notes)
* `PointFromScreen` for automatic DPI-correct coordinate conversion

Uses `PenSession` + `PenSession.Wpf`. The WPF Stylus backend uses WPF's native `StylusMove`/`StylusDown` events, providing pen input without Wintab. See [FUTURES_WPF_RENDERING.md](FUTURES_WPF_RENDERING.md) for rendering performance notes across all managed apps.

## Scribble.WinUI
A WinUI 3 drawing application using the unified `PenSession` library. Features:

* Consolidated toolbar (`ScribbleRibbon`) with labeled sections arranged horizontally:
  - **APP** — input API selector (System/Digitizer/WM_Pointer/WinUI Pointer), Clear button, log link
  - **BRUSH** — size slider (1–500 px)
  - **PEN** — proximity indicator, cursor type
  - **BUTTONS** — tip, eraser, barrel 1/2/3 status indicators with raw hex value
  - **POSITION** — raw position type, raw coordinates, canvas coordinates, Z height
  - **PRESSURE** — raw value and normalized percentage
  - **ORIENTATION** — azimuth, altitude, twist, tiltX, tiltY (all in tenths of a degree)
* **SkiaSharp bitmap-backed rendering** — `SKCanvas.DrawLine()` draws to an `SKBitmap`, copied to WinUI `WriteableBitmap` via `IBuffer.AsStream()`. Standardized across all managed scribble apps.
* Pressure-sensitive drawing with runtime API switching (4 backends, no restart)
* Correct DPI handling on high-DPI multi-monitor setups (225%+ scaling)
* Digitizer hi-res mode preserving full tablet-native precision (~5280 LPI)

Uses `PenSession` + `PenSession.WinUI` via the `PenSessionWinUI3` wrapper, which handles the WinUI 3-specific `ClientToScreen` + DPI conversion from desktop coordinates to canvas-local DIPs.

## PenSession (Unified Pen Input)

The recommended entry point for new apps. Framework-agnostic unified pen input abstraction.

* **`PenSession.dll`** — `IPenSession` interface, `PenSessionFactory`, and three backend implementations: `WintabSystemSession`, `WintabDigitizerSession`, `WmPointerSession`. Works in any app type. No UI framework dependency.
* **`PenSession.WinUI.dll`** — WinUI 3 extension containing `WinUiPointerSession`. Requires Windows App SDK. WinUI 3 apps reference both packages.
* **`PenSession.Wpf.dll`** — WPF extension containing `WpfStylusSession`. Uses WPF's native `StylusMove`/`StylusDown` events. WPF apps reference both packages.
* **`PenSession.Avalonia.dll`** — Avalonia extension containing `AvaloniaPointerSession`. Uses Avalonia's native `PointerMoved`/`PointerPressed` events. Avalonia apps reference both packages.
* **`PenSession.WinForms.dll`** — WinForms extension containing `WinFormsPointerSession`. Uses `IMessageFilter` to intercept `WM_POINTER` messages application-wide. WinForms apps reference both packages. Note: `NativeWindow.AssignHandle` on a Form HWND crashes because it conflicts with WinForms' internal `NativeWindow` — `IMessageFilter` avoids this by not touching HWND ownership.
* **`PenSession.TestConsole`** — Console test app for verifying Wintab backends without a UI.

See [FUTURES_UNIFIED_SESSION.md](FUTURES_UNIFIED_SESSION.md) for the full design, framework-specificity details, and WinUI 3 integration guide.

## Scribble.Rust
A Rust drawing application using the `pen_session` C API via FFI. Features:

* **egui** immediate-mode UI — ribbon with API dropdown, brush slider, clear button, pen telemetry
* **tiny-skia** bitmap-backed rendering — `Pixmap` + `stroke_path` with round caps, uploaded as egui texture
* **pen_session.dll** via Rust FFI — safe `PenSession` wrapper with RAII `Drop` cleanup
* Runtime API switching: System, Digitizer, WM_Pointer
* DPI-aware coordinate conversion (physical desktop pixels → egui logical points)

Depends on `PenSession.Native.dll` (the C++ DLL) at runtime. Pure Rust otherwise — no C/C++ compilation needed. Total dependency footprint ~500 KB (tiny-skia + eframe).

## Native C++ (PenSession.Native + Scribble.Win32)

* **`PenSession.Native`** — Native DLL (`PenSession.Native.dll`) with C ABI. Exports both the legacy `wintab_session_*` API and the unified `pen_session_*` API with all three backends (System, Digitizer, WM_Pointer). Uses `GetPointerPenInfoHistory` for WM_POINTER to recover coalesced events.
* **`Scribble.Win32`** — Win32/GDI scribble app with ribbon UI, DPI-aware, double-buffered. Uses the unified `pen_session_*` API with runtime API switching.

## WintabDN (Low-Level)

The original .NET Wintab library (builds `WintabDN.dll`). Contains:

* **Low-level API** — `WintabInfo`, `WintabContext`, `WintabData`, `WintabExtensions` for direct Wintab access.
* Used by the `ExtensionTestApp` for ExpressKey/Touch Ring support. New apps should use `PenSession` instead for pen input.

## See Also
[Wintab - Basics](https://developer-docs.wacom.com/intuos-cintiq-business-tablets/docs/wintab-basics) - How to configure and write Wintab applications  

[Wintab - Reference](https://developer-docs.wacom.com/intuos-cintiq-business-tablets/docs/wintab-reference) - Complete API details 

[Wintab - FAQs](https://developer-support.wacom.com/hc/en-us/articles/12844524637975-Wintab) - Wintab programming tips  

## Where to get help  
If you have questions about the sample application or any of the setup process, please visit our Developer Support page at: https://developer.wacom.com/developer-dashboard/support.
