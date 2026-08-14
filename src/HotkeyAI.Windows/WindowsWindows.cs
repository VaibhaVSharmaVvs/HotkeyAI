using System.Diagnostics;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>Window location and manipulation.</summary>
public sealed class WindowsWindows : IWindows
{
    /// <summary>
    /// Shell window classes that must never match an automation's selector.
    /// </summary>
    /// <remarks>
    /// Straight from the Phase 0 spike. The only <c>explorer</c> window on a typical desktop is
    /// <c>Progman</c> — the desktop itself, titled "Program Manager". A plan asking to focus or
    /// close "explorer" would hit the desktop shell rather than a folder window, which is both
    /// useless and alarming. <c>WorkerW</c> is the wallpaper layer and has the same problem.
    /// </remarks>
    private static readonly HashSet<string> ShellClasses =
        new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "Shell_TrayWnd", "Button" };

    public ValueTask<WindowRef?> FindAsync(
        WindowSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);

        WindowRef? best = null;

        foreach (var window in Enumerate())
        {
            if (!Matches(window, selector))
            {
                continue;
            }

            // Prefer a window that is not minimised: an automation asking to focus something
            // almost always means the one the user can see.
            if (best is null || (Native.IsIconic((nint)best.Value.Id) && !Native.IsIconic((nint)window.Id)))
            {
                best = window;
            }
        }

        return ValueTask.FromResult(best);
    }

    public ValueTask<string?> ForegroundProcessAsync(CancellationToken cancellationToken)
    {
        var window = Native.GetForegroundWindow();
        if (window == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        Native.GetWindowThreadProcessId(window, out var processId);
        return ValueTask.FromResult(ProcessName(processId));
    }

    public ValueTask FocusAsync(WindowRef window, CancellationToken cancellationToken)
    {
        var handle = (nint)window.Id;

        // A minimised window cannot take focus, so restore first.
        if (Native.IsIconic(handle))
        {
            Native.ShowWindow(handle, Native.SW_RESTORE);
        }

        // SetForegroundWindow is subject to Windows' foreground lock: it refuses unless the
        // calling process owns the foreground or received the last input event. The agent
        // normally satisfies that, because handling WM_HOTKEY counts as receiving input — but
        // nothing else does. Running the same plan from the CLI, or from anything the user did
        // not just interact with, the call is refused and the window never comes forward.
        if (Native.SetForegroundWindow(handle))
        {
            return ValueTask.CompletedTask;
        }

        // The documented way out: attach our input queue to the foreground window's thread, so
        // that for the duration of the attachment we count as the foreground thread and the
        // restriction no longer applies. Attaching is why this works; BringWindowToTop alone
        // raises the window without giving it focus.
        var foreground = Native.GetForegroundWindow();
        if (foreground == 0)
        {
            return ValueTask.CompletedTask;
        }

        var us = Native.GetCurrentThreadId();
        var them = Native.GetWindowThreadProcessId(foreground, out _);

        if (them == 0 || them == us)
        {
            return ValueTask.CompletedTask;
        }

        if (!Native.AttachThreadInput(us, them, attach: true))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            Native.BringWindowToTop(handle);
            Native.SetForegroundWindow(handle);
        }
        finally
        {
            // Always detach. A thread left attached to another process's input queue shares its
            // focus and keyboard state for as long as the agent lives, which is a far worse
            // outcome than a window that failed to come forward.
            Native.AttachThreadInput(us, them, attach: false);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MinimiseAsync(WindowRef window, CancellationToken cancellationToken)
    {
        Native.ShowWindow((nint)window.Id, Native.SW_MINIMIZE);
        return ValueTask.CompletedTask;
    }

    public ValueTask MaximiseAsync(WindowRef window, CancellationToken cancellationToken)
    {
        Native.ShowWindow((nint)window.Id, Native.SW_MAXIMIZE);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(WindowRef window, CancellationToken cancellationToken)
    {
        // WM_CLOSE asks; it does not force. The application may prompt to save, which is the
        // whole reason close_window is preferred over terminate_process.
        Native.PostMessage((nint)window.Id, Native.WM_CLOSE, 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask MoveAsync(
        WindowRef window,
        WindowPosition position,
        string? monitor,
        CancellationToken cancellationToken)
    {
        var handle = (nint)window.Id;
        var area = WorkArea(handle, monitor);

        // Restore before moving: MoveWindow on a maximised window has no visible effect.
        if (position != WindowPosition.Maximized)
        {
            Native.ShowWindow(handle, Native.SW_RESTORE);
        }

        if (position == WindowPosition.Maximized)
        {
            Native.MoveWindow(handle, area.Left, area.Top, area.Width, area.Height, true);
            Native.ShowWindow(handle, Native.SW_MAXIMIZE);
            return ValueTask.CompletedTask;
        }

        var halfWidth = area.Width / 2;
        var halfHeight = area.Height / 2;

        var (x, y, width, height) = position switch
        {
            WindowPosition.LeftHalf => (area.Left, area.Top, halfWidth, area.Height),
            WindowPosition.RightHalf => (area.Left + halfWidth, area.Top, halfWidth, area.Height),
            WindowPosition.TopHalf => (area.Left, area.Top, area.Width, halfHeight),
            WindowPosition.BottomHalf => (area.Left, area.Top + halfHeight, area.Width, halfHeight),
            WindowPosition.TopLeftQuarter => (area.Left, area.Top, halfWidth, halfHeight),
            WindowPosition.TopRightQuarter => (area.Left + halfWidth, area.Top, halfWidth, halfHeight),
            WindowPosition.BottomLeftQuarter => (area.Left, area.Top + halfHeight, halfWidth, halfHeight),
            WindowPosition.BottomRightQuarter =>
                (area.Left + halfWidth, area.Top + halfHeight, halfWidth, halfHeight),
            WindowPosition.Centered => (
                area.Left + (area.Width / 4), area.Top + (area.Height / 4), halfWidth, halfHeight),
            _ => (area.Left, area.Top, area.Width, area.Height),
        };

        Native.MoveWindow(handle, x, y, width, height, true);
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Visible top-level windows with a title, excluding shell furniture.</summary>
    internal static List<WindowRef> Enumerate()
    {
        var found = new List<WindowRef>();

        Native.EnumWindows(
            (handle, _) =>
            {
                if (!Native.IsWindowVisible(handle) || Native.GetWindowTextLength(handle) == 0)
                {
                    return true;
                }

                if (ShellClasses.Contains(Native.GetWindowClass(handle)))
                {
                    return true;
                }

                Native.GetWindowThreadProcessId(handle, out var processId);

                found.Add(new WindowRef(
                    handle,
                    ProcessName(processId) ?? "",
                    Native.GetWindowTitle(handle),
                    Integrity.IsHigherThanUs(processId)));

                return true;
            },
            0);

        return found;
    }

    private static bool Matches(WindowRef window, WindowSelector selector)
    {
        if (selector.ProcessName is { } process
            && !string.Equals(window.ProcessName, process, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.TitleContains is { } fragment
            && !window.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.TitleRegex is { } pattern)
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        window.Title,
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromMilliseconds(250)))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutMarker)
            {
                return false;
            }
        }

        if (selector.ClassName is { } className
            && !string.Equals(
                Native.GetWindowClass((nint)window.Id), className, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Alias so the catch filter above reads clearly.</summary>
    private sealed class RegexMatchTimeoutMarker : Exception;

    private static string? ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process exited between enumeration and lookup, which is routine.
            return null;
        }
    }

    private static Native.Rect WorkArea(nint window, string? monitor)
    {
        var monitors = new List<Native.MonitorInfo>();

        Native.EnumDisplayMonitors(
            0,
            0,
            (handle, _, _, _) =>
            {
                var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
                if (Native.GetMonitorInfo(handle, ref info))
                {
                    monitors.Add(info);
                }

                return true;
            },
            0);

        if (monitors.Count == 0)
        {
            return new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }

        var chosen = monitor?.ToLowerInvariant() switch
        {
            null or "" => MonitorOf(window, monitors),
            "primary" => monitors.FirstOrDefault(m => (m.Flags & Native.MONITORINFOF_PRIMARY) != 0),
            "secondary" => monitors.FirstOrDefault(m => (m.Flags & Native.MONITORINFOF_PRIMARY) == 0),
            var index when int.TryParse(index, out var n) && n >= 1 && n <= monitors.Count =>
                monitors[n - 1],
            _ => MonitorOf(window, monitors),
        };

        return chosen.Work.Width == 0 ? monitors[0].Work : chosen.Work;
    }

    private static Native.MonitorInfo MonitorOf(nint window, List<Native.MonitorInfo> monitors)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var handle = Native.MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);

        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        return Native.GetMonitorInfo(handle, ref info) ? info : monitors[0];
    }
}
