namespace HotkeyAI.Windows;

/// <summary>
/// GDI handles the framework hands out but does not clean up.
/// </summary>
/// <remarks>
/// Exists so the UI project can release an icon handle without doing its own P/Invoke.
/// <c>Bitmap.GetHicon</c> creates a handle the caller owns, and <c>Icon.FromHandle</c> explicitly
/// does not take ownership of it — so the obvious code leaks one GDI handle per call, forever.
/// </remarks>
public static class GdiHandle
{
    /// <summary>Release an icon handle obtained from <c>Bitmap.GetHicon</c>.</summary>
    public static void DestroyIcon(nint icon)
    {
        if (icon != 0)
        {
            Native.DestroyIcon(icon);
        }
    }
}
