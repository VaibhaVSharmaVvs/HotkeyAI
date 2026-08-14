namespace HotkeyAI.Windows;

/// <summary>
/// Bringing a window to the foreground, against Windows' wishes.
/// </summary>
/// <remarks>
/// Extracted so that <see cref="WindowsWindows"/> and the picker overlay share one implementation.
/// The workaround below is subtle enough that two copies would drift, and the failure it addresses
/// is silent: <c>SetForegroundWindow</c> simply returns false and the window stays where it is.
/// </remarks>
public static class ForegroundWindow
{
    /// <summary>The window that currently owns the foreground, or zero if there is none.</summary>
    public static nint Current() => Native.GetForegroundWindow();

    /// <summary>
    /// Bring a window forward, falling back to input-queue attachment when Windows refuses.
    /// </summary>
    /// <returns>True if the window owns the foreground afterwards.</returns>
    /// <remarks>
    /// The foreground lock refuses <c>SetForegroundWindow</c> unless the calling process owns the
    /// foreground or received the last input event. The agent qualifies while handling
    /// <c>WM_HOTKEY</c>, and stops qualifying shortly after — so the picker, which opens a moment
    /// later, needs the fallback even though the automation was launched by a keypress.
    /// </remarks>
    public static bool Force(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        if (Native.IsIconic(window))
        {
            Native.ShowWindow(window, Native.SW_RESTORE);
        }

        if (Native.SetForegroundWindow(window))
        {
            return true;
        }

        var foreground = Native.GetForegroundWindow();

        if (foreground == 0)
        {
            return false;
        }

        var us = Native.GetCurrentThreadId();
        var them = Native.GetWindowThreadProcessId(foreground, out _);

        if (them == 0 || them == us)
        {
            return false;
        }

        // Attaching our input queue to the foreground window's thread makes us count as the
        // foreground thread for the duration, and the restriction no longer applies.
        // BringWindowToTop on its own raises the window without giving it focus.
        if (!Native.AttachThreadInput(us, them, attach: true))
        {
            return false;
        }

        try
        {
            Native.BringWindowToTop(window);
            return Native.SetForegroundWindow(window);
        }
        finally
        {
            // Always detach. A thread left attached to another process's input queue shares its
            // focus and keyboard state for as long as the agent lives, which is far worse than a
            // window that failed to come forward.
            Native.AttachThreadInput(us, them, attach: false);
        }
    }
}
