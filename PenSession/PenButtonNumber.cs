namespace PenSession;

/// <summary>
/// Well-known button numbers from the low word of <see cref="PenPoint.Buttons"/>.
/// The actual button-to-number mapping depends on the stylus model and
/// the user's Wacom driver settings.
/// </summary>
public static class PenButtonNumber
{
    /// <summary>Pen tip contact (button 0).</summary>
    public const int Tip = 0;

    /// <summary>First barrel button, closest to tip (button 1).</summary>
    public const int Barrel1 = 1;

    /// <summary>Second barrel button, middle (button 2).</summary>
    public const int Barrel2 = 2;

    /// <summary>Third barrel button, farthest from tip (button 3).</summary>
    public const int Barrel3 = 3;
}
