namespace HotkeyAI.Windows;

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct ScreenArea(int Left, int Top, int Width, int Height);

/// <summary>Monitor geometry, for callers that need to place a window themselves.</summary>
/// <remarks>
/// Exposed for the picker overlay, which has to open on the monitor the user is actually looking
/// at. It shares <see cref="WindowsWindows"/>'s monitor lookup rather than re-deriving it, so
/// <c>move_window</c> and the overlay cannot disagree about where a screen is.
/// </remarks>
public static class Screens
{
    /// <summary>
    /// The usable area of the monitor containing <paramref name="window"/>, excluding the taskbar.
    /// </summary>
    /// <remarks>
    /// Work area rather than full bounds: an overlay centred on the whole screen sits slightly
    /// too low, and on a bottom-docked taskbar can extend underneath it.
    /// </remarks>
    public static ScreenArea WorkAreaFor(nint window)
    {
        var rect = WindowsWindows.WorkArea(window, null);
        return new ScreenArea(rect.Left, rect.Top, rect.Width, rect.Height);
    }
}
