using System.Runtime.InteropServices;

namespace HotkeyAI.Windows;

/// <summary>
/// Makes a window's title bar follow the app's own dark palette.
/// </summary>
/// <remarks>
/// WPF draws the client area but the title bar belongs to the shell, so a dark window opens with
/// a white caption unless it asks otherwise. It reads as a rendering bug rather than a choice.
/// <para>
/// The attribute is advisory: it is unsupported before Windows 10 20H1 and the call simply fails
/// there, which is why the result is ignored rather than checked.
/// </para>
/// </remarks>
public static partial class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        nint window, int attribute, in int value, int size);

    /// <summary>Ask the shell to draw this window's caption dark.</summary>
    public static void UseDarkTitleBar(nint window)
    {
        if (window == 0)
        {
            return;
        }

        var on = 1;
        _ = DwmSetWindowAttribute(window, UseImmersiveDarkMode, in on, sizeof(int));
    }
}
