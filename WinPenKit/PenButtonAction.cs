namespace WinPenKit;

/// <summary>
/// Button action codes from the high word of <see cref="PenPoint.Buttons"/>.
/// Wintab encodes button events as: <c>(action &lt;&lt; 16) | buttonNumber</c>.
/// </summary>
public enum PenButtonAction
{
    /// <summary>No button event.</summary>
    None = 0,

    /// <summary>A button was released.</summary>
    Released = 1,

    /// <summary>A button was pressed.</summary>
    Pressed = 2,
}
