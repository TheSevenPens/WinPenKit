# Skia's C API, for `Scribble.WinUINative`

Everything here is third-party. It is vendored rather than fetched at build time so that the
sample builds without network access and so that the exact Skia the sample was tested against
is visible in the history.

## What this is, and what it is not

Skia's own API is C++, and it is built for static linking — the C++ classes are not exported
from any shared library. Alongside that, Skia carries a **flat C API** under `include/c`, and
that is what a shared build can export.

`libSkiaSharp.dll` is a native build of Skia that exports exactly that C API: 812 `sk_*`
functions, 67 `gr_*`, and no C++ symbols at all. It ships inside the SkiaSharp NuGet package
but contains no managed code — a C++ program links it like any other native DLL.

Choosing it over a C++ Skia build buys one thing that matters to this repository: **it is the
same binary the four managed Scribble samples load.** Same Skia build, same version, same code
path, so a stroke drawn here and a stroke drawn in `Scribble.WinUI` come out of the same
rasteriser. That makes `Scribble.WinUINative` a comparison in which the renderer is held
constant and only the language and UI layer change.

What it costs is idiomatic C++. `sk_canvas_draw_line(canvas, ...)` rather than
`canvas->drawLine(...)`, and lifetimes managed by hand. `src/skia_raii.h` wraps that back up.

## Provenance

| | |
| --- | --- |
| DLL | `SkiaSharp.NativeAssets.Win32` **3.119.4**, `runtimes/win-x64/native/libSkiaSharp.dll` |
| Skia milestone | **m119** (the DLL reports it; the package version number carries it too) |
| Headers | `mono/skia` at **`7dbfc07dd33181f84e0958afb7ee805c6c769f0b`** |
| Why that commit | it is `externals/skia` at SkiaSharp tag `v3.119.4`, so the headers are the ones this DLL was compiled from |
| Licence | BSD-3-Clause, `LICENSE` in this directory, Copyright 2011 Google Inc. |

The commit is pinned rather than a branch on purpose. The C API is stable but not frozen, and a
header set that drifts from the DLL gives link errors at best and a silent ABI mismatch at
worst — the same class of fault `pen_session_get_point_size` exists to catch on WinPenKit's own
ABI.

## `libSkiaSharp.def` and the import library

The NuGet package ships a DLL and a PDB. **It ships no import library and no headers**, so
neither `#include` nor `/link` works against it out of the box.

`libSkiaSharp.def` lists all 924 exports. The build runs `lib.exe /DEF:` over it to produce
`lib/x64/libSkiaSharp.lib`, which is a generated artifact and is not committed.

Regenerate the `.def` after changing the pinned SkiaSharp version:

```powershell
tools\regen-skia-def.ps1
```

## Verifying the three pieces agree

Headers, import library and DLL are three separately-sourced things that have to match. A build
that links proves less than it appears to, because an import library generated from a `.def`
resolves any name in that file whether or not the DLL's implementation is what you expect.

`src/skia_probe.cpp` draws a known stroke into a raster surface and reads the pixels back:

```
centre = 0xFF000000   (opaque black, the stroke)
corner = 0xFFFFFFFF   (opaque white, the clear)
fully black pixels = 420
PASS skia c api is live
```

Run it after changing any of the three. Checking the return codes alone would pass against a
DLL that drew nothing.
