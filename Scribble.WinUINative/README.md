# Scribble.WinUINative

The eighth Scribble sample, and the only one that reaches WinUI 3 without a managed runtime.
C++/WinRT for the UI, Skia's C API for the raster, WinPenKit's C ABI for the pen.

## Why it exists

`Scribble.WinUI` is WinUI 3 through C#. This is the same framework through C++, so the
difference between the two is the language and the binding, not the framework. It exists to be
the starting point for a WinUI application that cannot take a .NET dependency.

Verified rather than assumed: the running process loads **96 modules and no CLR** — no
`coreclr`, no `hostfxr`, no `System.*` — with `Microsoft.UI.Xaml.dll` among them.

## What it is built from

| | |
| --- | --- |
| UI | WinUI 3 via C++/WinRT, unpackaged (`WindowsPackageType=None`) |
| Raster | Skia, through the flat C API in `libSkiaSharp.dll` |
| Pen | `WinPenKit.Native`, the same C ABI `Scribble.Win32` uses |
| Checks | `Scribble.Win32/src/selftest.h`, shared unmodified |

`external/skia-c-api/README.md` covers where Skia comes from, why the C API rather than C++,
and how to verify the headers, the generated import library and the DLL agree.

## The UI is built in code, not XAML markup

Every control here is the same `Microsoft.UI.Xaml` type a `.xaml` file would produce; only the
construction differs. Two reasons: `Scribble.Qt` builds its ribbon the same way, so the two
native samples stay structurally parallel; and XAML markup in C++ needs an `.idl` per XAML
type plus the XAML compiler, none of which this sample would exercise for its own sake.

The trade is real: **this is not a template for markup-first WinUI.**

## The native WM_POINTER session does not work on WinUI 3

`Scribble.Win32` hands its own HWND to `pen_session_start` and the WM_POINTER session
subclasses it. That does not work here, and it fails quietly.

Measured: a session opened against the top-level window drains **zero** points. So does one
opened against either child window WinUI creates — `Microsoft.UI.Content.DesktopChildSiteBridge`,
which covers the whole content area, and `InputSiteWindowClass`. In every case the session
reports itself running, `max pressure` reads 1024, and nothing anywhere reports an error. WinUI
consumes pointer input through its own input site before a subclass on any of those windows
sees it.

So on WinUI, "pointer" means the framework's own events. `src/xamlpointer.h` takes
`PointerPressed`/`Moved`/`Released`/`Exited` and shapes them into `PenPoint`, so everything
downstream is identical to the native path. The managed `Scribble.WinUI` has the same shape for
the same reason: its pointer backend is `WinUiPointerSession`, not the WM_POINTER session.

**Wintab is unaffected.** A Wintab context runs on its own hidden pump window and uses the
application window only to bound the capture region, so it takes the native path here as it
does everywhere else. It needs the tablet to exercise.

The dropdown therefore offers Wintab, Wintab (high-res) and WinUI Pointer, and deliberately
does **not** offer the native WM_POINTER session — listing a backend measured not to work here
would be offering a choice that silently draws nothing.

## Five things that cost time, written down so they do not cost it again

**`XamlControlsResources` crashes this app.** The obvious translation of App.xaml's
`<XamlControlsResources/>` is to merge one into `Application.Resources`. Doing that fails at
startup with `0xC000027B` and no message; caught and printed, it is *"Cannot find a resource
with the given key: AcrylicBackgroundFillColorDefaultBrush."* Windows App SDK 1.7 already loads
the WinUI theme resources for an application that declares none, and merging that dictionary on
top shadows them with a set that lacks the system backdrop brushes. Leaving it out is not
skipping the styling — the controls render with their normal Fluent appearance without it.

**A C++ WinUI project trips an unsatisfiable MSBuild hook.** `Microsoft.Build.Msix.Pri.targets`
adds `AddProcessedXamlFilesToCopyLocal` to `GetCopyToOutputDirectoryItemsDependsOn` whenever
`DefaultXamlRuntime` is WinUI, but that target is only defined by the **.NET** markup compiler
targets, imported under `UsingMicrosoftNETSdk == true` — a condition a `.vcxproj` never meets.
The native markup compiler targets do not define it either. The build links the executable and
*then* reports failure. The project defines the target as a no-op.

**`Stretch` is the 1:1 presentation decision.** The surface is sized in physical pixels and the
`Image` element in `pixels / rasterizationScale`. With `Stretch::None` WinUI draws one bitmap
pixel per *logical* unit, so on a 2.25x display the surface renders 2.25x too large and is
clipped to the element — strokes land at the wrong place and most of the canvas is off-screen.
`Stretch::Fill` maps the whole bitmap onto the element, which with that sizing puts one bitmap
pixel on one physical pixel. This is the `L1.presentation-1to1` fault, arrived at by hand.

**The acceptance checks cannot run on the UI thread.** The presentation probe draws, then
waits for the drawing to reach the screen before capturing. Anything occupying the UI thread
while it waits stops WinUI presenting the frame being waited for: run from a `DispatcherTimer`
tick, the probe reported *"neither marker reached the screen"* on all 40 attempts, because none
had. Pumping the message queue inside that callback does not help — the compositor commit
happens when the callback returns. The checks now run on their own thread and marshal anything
touching XAML back; the capture is GDI against the screen and needs no thread affinity. With
that one change the probe went from failing every time to `841/841 px matched`, the same figure
`Scribble.WinUI` reports.

**Divide pressure by the right maximum.** Pressure normalises against the session's maximum, and the two backends do not share a session.
Reading it from the native session while the XAML source was running returned that object's
unstarted default of **1**, so a pressure of 850 became a stroke 5100 pixels wide and the canvas
went solid black. It looked like a rendering fault. It was a division by the wrong scale — the
same shape as every other "number whose units were assumed" in this repository.

## Building

`WinPenKit.Native` must be built first — this project links its import library and copies its
DLL:

```powershell
msbuild WinPenKitNative.sln -p:Configuration=Debug -p:Platform=x64
msbuild Scribble.WinUINative\Scribble.WinUINative.vcxproj -p:Configuration=Debug -p:Platform=x64
```

`libSkiaSharp.dll` is taken from the restored `SkiaSharp.NativeAssets.Win32` package, pinned to
the version `Directory.Build.props` gives the managed samples. Build a managed sample once so
NuGet has restored it; the project fails with a named error rather than a missing-DLL crash if
it is absent.

## Running

```powershell
Scribble.WinUINative.exe                      # draw
Scribble.WinUINative.exe --selftest           # the checks that need no pen data
Scribble.WinUINative.exe --selftest --replay my.csv
Scribble.WinUINative.exe --record my.csv      # written when the window closes
```

The backend choice is remembered in `HKCU\Software\TheSevenPens\Scribble.WinUINative`, the
same place and shape `Scribble.Qt` uses. Unlike Qt, switching takes effect immediately — a
WinPenKit session opens and closes at will and WinUI's pointer events are just handlers — so
there is no restart notice to show.

## State

Working, and verified: the window; the Skia surface; presentation at 1:1; the WinPenKit session
over the C ABI; the WinUI pointer path; drawing with pressure-derived width; the standard
seven-section ribbon; `--record`; `--replay`; and the full check suite at **12/12** with a
replay, 9/9 without.

**Wintab is verified**, on a Wacom DTH246 (Cintiq 24) on 13 Sep 2026. Both contexts open and
both report the backend they were asked for:

| | |
| --- | --- |
| Wintab (system) | 9/9, `requested Wintab, obtained Wintab` |
| Wintab (high-res) | 9/9, `requested Wintab (high-res), obtained Wintab (high-res)` |

The high-res row is the one worth having: that context degrades to screen pixels when it cannot
get tablet-native resolution, and `L0.pen-api` confirms it did not. Drawn by hand on the
display in both modes; the stroke tracks the nib.

### If Wintab will not open

Every context failed on this machine until the Wacom service was restarted, and the failure had
a distinctive shape worth recognising:

```
Start failed: Failed to open system context.
Start failed: Fallback context also failed to open.
```

with the native log showing `WTInfoA` answering correctly -- real device extents, the right
virtual desktop -- while every `WTOpenA` was refused, hi-res, fallback and system alike. Nothing
held `wintab32.dll`, the service was running, and the tablet was present and enumerated.

An elevated `Restart-Service WTabletServicePro -Force` fixed it outright. Queries working while
opens are refused is the signature; reach for the service before looking for a fault in the
sample. It reproduced identically through `WinPenKit.TestConsole`, which is how this was shown
not to be a defect here.
