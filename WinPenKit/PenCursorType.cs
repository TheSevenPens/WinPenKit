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
}
