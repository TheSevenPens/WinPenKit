using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinPenKit.Wpf;

/// <summary>
/// Exact conversion between a WPF element's device-independent pixels and desktop
/// device pixels.
/// </summary>
/// <remarks>
/// <para>WPF's own <see cref="Visual.PointToScreen"/> and <see cref="Visual.PointFromScreen"/>
/// cannot be used for pen input. They take and return <see cref="Point"/>, which is a pair of
/// doubles, so they look precision-preserving - but internally they hand the value to Win32
/// <c>ClientToScreen</c>/<c>ScreenToClient</c>, which take an integer <c>POINT</c>. Every
/// coordinate that passes through them is truncated to a whole device pixel.</para>
/// <para>That is invisible for mouse input, which is integral to begin with, and fatal for pen
/// input, which is the one thing a digitizer context exists to deliver at sub-pixel resolution.
/// Measured on a 1.75x display: a Wintab digitizer stream with 4.11 degrees of mean turn angle
/// between segments came out the far side of <c>PointFromScreen</c> at 17.51 degrees, with 100%
/// of the results landing exactly on whole device pixels. That is visible as a faceted,
/// "bumpy" stroke.</para>
/// <para>This computes the same conversion without the round-trip. The only Win32 call is for
/// the window's client origin, which is genuinely integral, so no precision is lost.</para>
/// </remarks>
public static class WpfCoordinates
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// The element's top-left corner in desktop device pixels, along with the DPI scale
    /// relating its DIPs to those pixels. Returns null if the element is not connected to a
    /// window.
    /// </summary>
    public static (double OriginX, double OriginY, double ScaleX, double ScaleY)? GetTransform(
        Visual element)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source ||
            source.RootVisual is null)
        {
            return null;
        }

        var clientOrigin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(source.Handle, ref clientOrigin))
            return null;

        // Where the element sits inside the window, in DIPs. A GeneralTransform, so this keeps
        // its fractional part - unlike anything routed through a POINT.
        Point dipOffset;
        try
        {
            dipOffset = element.TransformToAncestor(source.RootVisual).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            // Not in the same visual tree, or not yet arranged.
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(element);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        return (clientOrigin.X + dipOffset.X * scaleX,
                clientOrigin.Y + dipOffset.Y * scaleY,
                scaleX,
                scaleY);
    }
}
