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

## Three things that cost time, written down so they do not cost it again

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

## State

Working: the window, the Skia surface, the Skia-to-`WriteableBitmap` presentation at 1:1, and
drawing from WinUI pointer input with pressure.

Not yet done: the WinPenKit pen session, the standard seven-section ribbon with the pen API
switcher, `--record`, `--replay`, and the `--selftest` checks.
