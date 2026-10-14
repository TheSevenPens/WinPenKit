using System.Globalization;

namespace WinPenKit;

/// <summary>
/// Which Windows system gestures and pen feedback a window opts out of. Windows asks a window
/// with <c>WM_TABLET_QUERYSYSTEMGESTURESTATUS</c> which system gestures it wants, and the window
/// answers with a mask of the ones to switch off. By default a window receives all of them.
/// </summary>
/// <remarks>
/// <para>
/// A drawing surface wants none of three things: the press-and-hold gesture (a held pen becomes
/// a right click, after a ring), the circle Windows draws when a pen button goes down, and the
/// wave it draws on pen up. Values and descriptions are from Microsoft's page for the message
/// (<c>tpcshrd.h</c>): <c>TABLET_DISABLE_PRESSANDHOLD</c> 0x1 ("disables press and hold
/// (right-click) gesture"), <c>TABLET_DISABLE_PENTAPFEEDBACK</c> 0x8 ("disables UI feedback on
/// pen up (waves)") and <c>TABLET_DISABLE_PENBARRELFEEDBACK</c> 0x10 ("disables UI feedback on
/// pen button down (circle)").
/// </para>
/// <para>
/// <b>Why the Scribble apps answer.</b> A real pen device (a driver such as a virtual HID
/// digitizer or a vendor's) draws a circle under the pen on a button press; a synthetic pen
/// injected through <c>InjectSyntheticPointerInput</c> was seen not to. The same window, answering
/// 0 and then 0x10, showed the circle and then did not. Windows sends the query on every pen
/// contact, whether or not a button is pressed. See the devnotes page on press-and-hold and pen
/// feedback.
/// </para>
/// <para>
/// The answer can be overridden from the environment (<see cref="OverrideVariable"/>,
/// hexadecimal) so each flag can be tried on its own without a rebuild; <c>0</c> answers "disable
/// nothing", which is Windows' default and the baseline for measuring what Windows does. The
/// number of times Windows has asked is kept, because whether the window is asked at all is the
/// first thing to know when a ring does not go away.
/// </para>
/// </remarks>
public static class TabletGestures
{
    /// <summary><c>WM_TABLET_QUERYSYSTEMGESTURESTATUS</c> = <c>WM_TABLET_DEFBASE</c> (0x02C0) + 12.</summary>
    public const uint QueryMessage = 0x02CC;

    public const uint DisablePressAndHold = 0x00000001;
    public const uint DisablePenTapFeedback = 0x00000008;
    public const uint DisablePenBarrelFeedback = 0x00000010;

    /// <summary>What a window answers unless overridden: no press-and-hold right click, no tap wave, no button circle.</summary>
    public const uint Default = DisablePressAndHold | DisablePenTapFeedback | DisablePenBarrelFeedback;

    /// <summary>Environment variable holding the mask as hexadecimal (<c>0x</c> optional).</summary>
    public const string OverrideVariable = "WINPENKIT_TABLET_GESTURE_FLAGS";

    private static long _queries;

    /// <summary>The mask the window answers with.</summary>
    public static uint Flags { get; } = ParseOverride(Environment.GetEnvironmentVariable(OverrideVariable), Default);

    /// <summary>How many times Windows has asked this process's windows which gestures they want.</summary>
    public static long Queries => Interlocked.Read(ref _queries);

    /// <summary>
    /// Hexadecimal text to a mask. Anything that is not hexadecimal (or is blank) gives
    /// <paramref name="fallback"/>, so a typo never silently switches the flags off.
    /// </summary>
    public static uint ParseOverride(string? text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>
    /// A window procedure hook: answers the gesture query with <see cref="Flags"/> and leaves every
    /// other message alone. Windows sends the query with <c>SendMessageTimeout</c>, so this does
    /// nothing but count and answer.
    /// </summary>
    public static IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        Answer(msg, Flags, ref handled);

    /// <summary>The hook's decision, with the mask as a parameter so it can be tested.</summary>
    public static IntPtr Answer(uint msg, uint flags, ref bool handled)
    {
        if (msg != QueryMessage) return IntPtr.Zero;
        Interlocked.Increment(ref _queries);
        handled = true;
        return (IntPtr)flags;
    }

    /// <summary>Text for a window title: how often Windows has asked and what the window answers.</summary>
    public static string TitleSuffix() => $"gesture queries {Queries}, answer 0x{Flags:X}";

    /// <summary>A sentence for a tooltip or a log: what the window answers and how often it has been asked.</summary>
    public static string Describe() =>
        $"Windows has asked about system gestures {Queries} time(s); the window answers 0x{Flags:X} " +
        "(0x1 press and hold, 0x8 pen tap wave, 0x10 pen button circle).";
}
