namespace WinPenKit;

/// <summary>
/// Well-known cursor type IDs from <see cref="PenPoint.Cursor"/>.
/// The cursor type identifies the tool currently in use. Wintab assigns
/// these when the pen enters proximity of the tablet.
/// </summary>
public static class PenCursorType
{
    /// <summary>Pen tip cursor (observed value: 13).</summary>
    public const uint PenTip = 13;

    /// <summary>Eraser cursor (observed value: 14).</summary>
    public const uint Eraser = 14;

    /// <summary>
    /// The cursor number with its name for the two known values (<c>13 (pen)</c>, <c>14 (eraser)</c>); any other
    /// number is returned bare, because a guessed name would read as a measurement.
    /// </summary>
    public static string Describe(uint cursor) => cursor switch
    {
        PenTip => $"{cursor} (pen)",
        Eraser => $"{cursor} (eraser)",
        _ => cursor.ToString(),
    };
}
