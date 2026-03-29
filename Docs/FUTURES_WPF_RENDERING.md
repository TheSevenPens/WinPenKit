# Managed App Rendering

All managed scribble apps (WinUI 3, WPF, Avalonia) are standardized on **SkiaSharp** for bitmap-backed stroke rendering.

## Current Approach (All Managed Apps)

`SKBitmap` + `SKCanvas` for drawing, pixel-copied to a framework-specific `WriteableBitmap` for display:

| App | Draw to | Display via | Copy method |
|---|---|---|---|
| Scribble.WinUI | `SKBitmap` via `SKCanvas` | WinUI `WriteableBitmap` | `IBuffer.AsStream()` |
| Scribble.Wpf | `SKBitmap` via `SKCanvas` | WPF `WriteableBitmap` | `Buffer.MemoryCopy` |
| Scribble.Avalonia | `SKBitmap` via `SKCanvas` | Avalonia `WriteableBitmap` | `Buffer.MemoryCopy` |
| Scribble.Win32 | GDI `HBITMAP` | `BitBlt` | Direct blit (no Skia) |

The pipeline per frame:
1. `session.DrainPoints()` — get 3-4 pen points from the PenSession
2. `SKCanvas.DrawLine()` — draw each segment to the SKBitmap (anti-aliased, round caps)
3. Pixel copy — bulk copy from SKBitmap to the framework's WriteableBitmap
4. Invalidate — tell the framework to display the updated bitmap

## Previous Approaches Tried (Scribble.WinUI)

1. **XAML Line elements** — Each stroke segment created a new `Line` in the visual tree. Visual tree grew to thousands of elements, causing layout stalls.
2. **Win2D CanvasRenderTarget** — GPU-accelerated, worked well but added a Win2D dependency. Replaced with SkiaSharp for consistency across all managed apps.

## Previous Approaches Tried (Scribble.Wpf)

1. **XAML Line elements** — Same visual tree growth problem as WinUI 3.
2. **RenderTargetBitmap** — WPF's CPU-only software renderer. Each `Render()` call triggered a full rasterization pass. Slower than SkiaSharp.

## Known Stutter (WPF)

Scribble.Wpf has slight stutter compared to the other apps. The SkiaSharp drawing itself is fast — the bottleneck is likely in:
- **`PointFromScreen`** per-point DPI context switching
- **`DispatcherTimer` jitter** — WPF's timer is not frame-synchronized
- **Pixel copy overhead** — ~3.4 MB per frame for a 1200x700 canvas
- **WPF compositor** — additional display latency vs direct blit

Scribble.WinUI, Scribble.Avalonia, and Scribble.Win32 do not have this stutter.

## WinUI 3 IBuffer Note

In WinUI 3 with CsWinRT 2.x (.NET 10 + Windows App SDK 1.7+), accessing the `WriteableBitmap` pixel buffer requires `IBuffer.AsStream()` from `System.Runtime.InteropServices.WindowsRuntime`. The older `[ComImport]`-based `IBufferByteAccess` COM pattern does not work — CsWinRT 2.x projects WinRT objects as `IInspectable` and don't support legacy QueryInterface casts.

## Options for Further Improvement

1. **Dirty-region copying** — Track the bounding box of new strokes and only copy that portion of the bitmap, reducing per-frame copy size.
2. **SkiaSharp native controls** — `SKElement` (WPF) or `SKXamlCanvas` (WinUI 3) render Skia content directly through the framework's composition surface, bypassing WriteableBitmap entirely.
3. **`CompositionTarget.Rendering`** (WPF) — Frame-synchronized timer instead of `DispatcherTimer`, may reduce WPF stutter.
4. **Direct2D via `D3DImage`** (WPF) — GPU-accelerated with no pixel copying. Production-grade but more complex setup.
