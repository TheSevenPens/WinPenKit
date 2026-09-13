using System.Runtime.InteropServices;

namespace WinPenKit.Diagnostics;

/// <summary>
/// One marker the application draws into its drawing surface so the probe can find it again
/// on screen. Coordinates and size are in surface pixels.
/// </summary>
/// <param name="X">Left edge, in surface pixels.</param>
/// <param name="Y">Top edge, in surface pixels.</param>
/// <param name="Size">Width and height, in surface pixels.</param>
/// <param name="R">Red channel of the fill colour.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
public readonly record struct PresentationMarker(int X, int Y, int Size, byte R, byte G, byte B)
{
    /// <summary>The marker's centre, in surface pixels.</summary>
    public double CentreX => X + Size / 2.0;

    /// <summary>The marker's centre, in surface pixels.</summary>
    public double CentreY => Y + Size / 2.0;
}

/// <summary>
/// Measures the rate a drawing surface is sampled at by drawing two markers into it and
/// finding them again in a screen capture.
/// </summary>
/// <remarks>
/// <para><see cref="SelfTest.CheckPresentation1To1"/> compares the host's layout size against
/// the surface's pixel count. Both can be right while the framework draws part of the surface
/// across the whole host. That happened in issue 70: a 2700px bitmap, a host covering 2700
/// device pixels, and the top-left 1200x600 pixels of the bitmap stretched across it. Strokes
/// landed 2.25 times too far from the canvas origin, and every property involved --
/// <c>Bitmap.Size</c>, <c>Image.Bounds</c>, the arranged desired size -- reported correctly at
/// the same moment. A check reading those properties cannot catch it, because the properties
/// are the thing that is wrong.</para>
/// <para>So this measures pixels. Two markers a known distance apart in the surface must land
/// that same distance apart on the screen. The measurement is a ratio of two distances, which
/// is why it needs neither the canvas origin nor the display scale: both cancel. That matters,
/// because a wrong origin is one of the things it has to be able to catch.</para>
/// <para><b>The application drives this, in three steps.</b> Draw the markers through
/// <see cref="Draw"/>, present a frame the way the application normally does, then await
/// <see cref="MeasureAsync"/>. The await is not a courtesy: the probe watches the screen until
/// the markers appear, and the thread it is called from is usually the one that has to render
/// them.</para>
/// <para>Finding both markers is itself the signal that the frame landed, so there is no timed
/// wait to tune and no framework-specific frame handshake. It does mean the window has to be
/// visible and unobscured -- this check measures what reached the display, and nothing reaches
/// the display from a hidden window.</para>
/// <para>The markers are drawn into the live surface and are not erased. An application that
/// carries on running should clear its canvas afterwards.</para>
/// <para><c>Scribble.Win32</c> and <c>Scribble.Rust</c> carry their own copy of this
/// measurement, for the same reason their whole self test is a reimplementation: neither has a
/// managed runtime under it. A change to what this reports should be made in all three.</para>
/// </remarks>
public sealed class PresentationProbe
{
    private readonly int _surfaceWidth;
    private readonly int _surfaceHeight;

    /// <summary>
    /// Places two markers in a surface of the given size.
    /// </summary>
    /// <remarks>
    /// <para>They sit at 8% and 33% along both axes. A quarter of the surface separates them,
    /// which is a long enough baseline that centroid noise is nowhere near the tolerance, and
    /// close enough to the origin that both still land inside the host when the surface is
    /// magnified up to about 3x. That matters for the report rather than the verdict: a
    /// magnified surface throws the far marker off screen, and a check that can only say
    /// "not found" says much less than one that can say 2.25x.</para>
    /// <para>Distinct colours rather than one repeated, so each centroid is computed from
    /// pixels that can only belong to that marker.</para>
    /// </remarks>
    public PresentationProbe(int surfaceWidth, int surfaceHeight)
    {
        _surfaceWidth = surfaceWidth;
        _surfaceHeight = surfaceHeight;

        // Large enough to survive being drawn at less than 1:1 and still leave a core of
        // unblended pixels, small enough not to cover anything.
        int size = Math.Max(12, Math.Min(surfaceWidth, surfaceHeight) / 40);

        First = new PresentationMarker(
            (int)(surfaceWidth * 0.08), (int)(surfaceHeight * 0.08), size, 255, 0, 255);
        Second = new PresentationMarker(
            (int)(surfaceWidth * 0.33), (int)(surfaceHeight * 0.33), size, 0, 255, 255);
    }

    /// <summary>The magenta marker, nearer the surface origin.</summary>
    public PresentationMarker First { get; }

    /// <summary>The cyan marker, further from the surface origin.</summary>
    public PresentationMarker Second { get; }

    /// <summary>
    /// Draws both markers by calling back into the application, which owns the surface and is
    /// the only code that knows how to put a filled rectangle on it.
    /// </summary>
    public void Draw(Action<PresentationMarker> fillRect)
    {
        fillRect(First);
        fillRect(Second);
    }

    /// <summary>
    /// Watches the window until both markers appear, measures the distance between them, and
    /// records the result as <c>L1.presentation-sampling</c>.
    /// </summary>
    /// <param name="test">The report to record into.</param>
    /// <param name="hwnd">The window to capture. Its whole rect is searched, so the caller does
    /// not have to know where its canvas sits inside it.</param>
    /// <param name="timeout">How long to keep looking before giving up.</param>
    public async Task MeasureAsync(SelfTest test, IntPtr hwnd, TimeSpan timeout)
    {
        const string Id = "L1.presentation-sampling";

        if (hwnd == IntPtr.Zero)
        {
            test.Skip(Id, "no window handle");
            return;
        }

        double wantDx = Second.CentreX - First.CentreX;
        double wantDy = Second.CentreY - First.CentreY;
        if (wantDx < 1 || wantDy < 1)
        {
            test.Skip(Id,
                $"surface {_surfaceWidth}x{_surfaceHeight} is too small to place markers in");
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        Centroid? a = null, b = null;
        Shot? last = null;
        int attempts = 0;
        bool settled = false;
        double prevDx = double.NaN, prevDy = double.NaN;

        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            var shot = Capture(hwnd);
            if (shot != null)
            {
                a = shot.Find(First.R, First.G, First.B);
                b = shot.Find(Second.R, Second.G, Second.B);
                last = shot;

                if (a != null && b != null)
                {
                    // Both markers visible is not enough. Windows animates a window open by
                    // compositing it scaled up to its final size, so a capture taken during
                    // that reads a few per cent small -- 0.966 and 0.976 on consecutive runs of
                    // the same build whose canvas an independent capture measured at exactly
                    // 1:1. Two consecutive readings that agree mean nothing is still moving,
                    // which is a property of the measurement rather than a guess about how long
                    // an animation lasts.
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    if (Math.Abs(dx - prevDx) < 0.5 && Math.Abs(dy - prevDy) < 0.5)
                    {
                        settled = true;
                        break;
                    }
                    prevDx = dx; prevDy = dy;
                }
            }

            // Yields the calling thread, which is usually the one that has to draw the frame
            // these markers are in.
            await Task.Delay(50);
        }

        if (a == null || b == null)
        {
            // Which one is missing says what went wrong. Nothing at all means nothing was
            // drawn where this could see it. The near marker alone means the surface is
            // magnified far enough to have carried the far one outside its host, which is a
            // failure of this check's subject rather than of its conditions.
            string why = a == null && b == null
                ? "neither marker reached the screen  <- the window is hidden or obscured, or " +
                  "no frame was presented"
                : a == null
                    ? "only the far marker reached the screen  <- unexpected; the near marker " +
                      "sits closer to the surface origin and should always be in view"
                    : "only the near marker reached the screen  <- the surface is magnified " +
                      "enough to carry the far marker outside its host, by more than about 3x";

            test.Check(Id, false, $"{why} (after {attempts} captures)");
            return;
        }

        if (!settled)
        {
            test.Check(Id, false,
                $"the window never stopped moving after {attempts} captures  <- it is still " +
                "animating, resizing, or being redrawn, and no reading can be trusted while " +
                "that is true");
            return;
        }

        double gotDx = b.X - a.X;
        double gotDy = b.Y - a.Y;
        double rx = gotDx / wantDx;
        double ry = gotDy / wantDy;

        // One per cent. The markers are tens of pixels across and their centroids land well
        // inside a pixel, so this sits far above the measurement's noise and far below any
        // scaling error worth reporting: the smallest one seen in practice was 2.25x.
        bool ok = Math.Abs(rx - 1.0) < 0.01 && Math.Abs(ry - 1.0) < 0.01;

        string saved = "";
        if (!ok && last != null)
        {
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "presentation-sampling.bmp");
                SaveBmp(last, p);
                saved = $"; capture written to {p}";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        test.Check(Id, ok,
            $"markers {wantDx:F0}x{wantDy:F0} surface px apart appeared {gotDx:F1}x{gotDy:F1} " +
            $"device px apart (x {rx:F3}, y {ry:F3}; {a.Pixels}/{b.Pixels} px matched, " +
            $"{First.Size * First.Size} drawn)" +
            (ok ? "" : $"{saved}  <- the surface is sampled at {rx:F2}x horizontally and " +
                       $"{ry:F2}x vertically, so ink lands that far from where the pen was"));
    }

    // -- Screen capture -------------------------------------------
    // From the screen rather than through PrintWindow. PrintWindow asks the window to render
    // again into a device context, and what this check is about is what the compositor put on
    // the display -- a second render is a different measurement wearing the same name.

    private sealed class Shot
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Bgra { get; init; }

        /// <summary>
        /// The centroid of every pixel close to the given colour, or null when too few match to
        /// be a marker rather than a stray blend along some edge.
        /// </summary>
        public Centroid? Find(byte r, byte g, byte b)
        {
            const int Tolerance = 48;
            long sumX = 0, sumY = 0, n = 0;

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width * 4;
                for (int x = 0; x < Width; x++)
                {
                    int i = row + x * 4;
                    if (Math.Abs(Bgra[i + 2] - r) > Tolerance) continue;
                    if (Math.Abs(Bgra[i + 1] - g) > Tolerance) continue;
                    if (Math.Abs(Bgra[i + 0] - b) > Tolerance) continue;
                    sumX += x; sumY += y; n++;
                }
            }

            if (n < 16) return null;
            return new Centroid(sumX / (double)n, sumY / (double)n, n);
        }
    }

    private sealed record Centroid(double X, double Y, long Pixels);

    /// <summary>
    /// Writes a capture to an uncompressed 32-bit BMP. A failing check that cannot show what
    /// it saw leaves the reader to re-derive it, which is how the first three readings of
    /// issue 70 came out wrong.
    /// </summary>
    private static void SaveBmp(Shot shot, string path)
    {
        const int HeaderBytes = 14 + 40;
        int pixelBytes = shot.Width * shot.Height * 4;

        using var f = File.Create(path);
        using var w = new BinaryWriter(f);

        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(HeaderBytes + pixelBytes);
        w.Write(0); w.Write(HeaderBytes);

        w.Write(40);
        w.Write(shot.Width);
        w.Write(-shot.Height);        // top-down, matching the capture
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0); w.Write(pixelBytes);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        w.Write(shot.Bgra, 0, pixelBytes);
    }

    private static Shot? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT wr)) return null;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // top-down, so row 0 is the top of the window
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            dib = CreateDIBSection(memDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            old = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, wr.Left, wr.Top, SRCCOPY | CAPTUREBLT))
                return null;

            var bytes = new byte[w * h * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            return new Shot { Width = w, Height = h, Bgra = bytes };
        }
        finally
        {
            if (old != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, int rop);
}
