using System.Diagnostics;
using System.Text.RegularExpressions;
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

        nint best = 0;

        foreach (var (handle, title) in Candidates())
        {
            if (!Matches(handle, title, selector))
            {
                continue;
            }

            // Prefer a window that is not minimised: an automation asking to focus something
            // almost always means the one the user can see.
            if (best == 0 || (Native.IsIconic(best) && !Native.IsIconic(handle)))
            {
                best = handle;
            }
        }

        // The process name is looked up once, for the winner, rather than once per window.
        // Security review 2026-08-17, finding L9: this used to build a full WindowRef for every
        // visible window — Process.GetProcessById plus three syscalls for an integrity level nothing
        // reads — on every pass, and a wait_for_window polls every 150 ms for up to its timeout.
        return ValueTask.FromResult(best == 0 ? null : Describe(best));
    }

    /// <summary>Turn a handle into the record the engine sees.</summary>
    private static WindowRef? Describe(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var processId);

        return new WindowRef(
            handle,
            ProcessName(processId) ?? "",
            Native.GetWindowTitle(handle));
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
        // Restores a minimised window and works around the foreground lock. The engine's
        // foreground_process_is postcondition is what reports the case where even that fails.
        ForegroundWindow.Force((nint)window.Id);
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

    /// <summary>
    /// Visible top-level windows with a title, excluding shell furniture — handle and title only.
    /// </summary>
    /// <remarks>
    /// Deliberately cheap. Security review 2026-08-17, finding L9: this built a full
    /// <see cref="WindowRef"/> per window, which meant <c>Process.GetProcessById</c> — a process-list
    /// read and an allocation — plus three syscalls for an integrity level that nothing anywhere
    /// consumed. Multiplied by every visible window, on every 150 ms poll of a
    /// <c>wait_for_window</c> that may run for its full timeout.
    /// <para>
    /// The title is read here because the common selector needs it and it is one cheap call. Anything
    /// dearer — the process name, the class — is looked up only when a selector asks, or once for the
    /// window that won.
    /// </para>
    /// </remarks>
    private static List<(nint Handle, string Title)> Candidates()
    {
        var found = new List<(nint, string)>();

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

                found.Add((handle, Native.GetWindowTitle(handle)));

                return true;
            },
            0);

        return found;
    }

    /// <summary>
    /// How long a single title may be tested against a selector's pattern.
    /// </summary>
    /// <remarks>
    /// A backstop, not the defence — <see cref="TitleOptions"/> is. Per window, and that is the cost
    /// worth knowing: the enumeration runs over every visible top-level window, and a
    /// wait_for_window polling every 150 ms can repeat the whole sweep for as long as its timeout
    /// allows.
    /// </remarks>
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Title patterns run on the non-backtracking engine, so they cannot blow up.
    /// </summary>
    /// <remarks>
    /// Security review 2026-08-17, finding M4. The review suggested a backtracking heuristic in the
    /// policy layer; this is better than a heuristic, because it is a guarantee. .NET's
    /// non-backtracking engine matches in time linear in the input, so <c>^(a+)+$</c> — the review's
    /// own example, which times out the ordinary engine — answers in single-digit milliseconds and
    /// there is no pattern that does not.
    /// <para>
    /// The trade is lookaround, backreferences and atomic groups, which this engine refuses at
    /// construction. That is a fair price for matching window titles, and
    /// <c>PolicyValidator</c> refuses such a pattern up front so the plan is rejected at authoring
    /// time rather than failing on a keypress.
    /// </para>
    /// </remarks>
    private const RegexOptions TitleOptions = RegexOptions.NonBacktracking;

    /// <summary>
    /// Whether one window satisfies a selector, buying only the information the selector asks for.
    /// </summary>
    /// <remarks>
    /// Ordered cheapest first, and that ordering is the fix for security review 2026-08-17 finding
    /// L9: the title arrives with the candidate, so a <c>titleContains</c> that does not match costs
    /// a string comparison and nothing else. The process name — a process-list read — and the window
    /// class are fetched only if a selector names them, and only for windows that got that far.
    /// </remarks>
    private static bool Matches(nint handle, string title, WindowSelector selector)
    {
        if (selector.TitleContains is { } fragment
            && !title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.TitleRegex is { } pattern)
        {
            try
            {
                if (!Regex.IsMatch(title, pattern, TitleOptions, RegexBudget))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Reported, not swallowed. Security review 2026-08-17, finding M4: the catch filter
                // here tested a private marker class that nothing in the repository ever throws, so
                // the real RegexMatchTimeoutException escaped, aborted the enumeration partway and
                // surfaced as a raw exception message. Returning false would be worse than that: a
                // catastrophic pattern would silently match nothing, and "no window found" is the
                // one answer that looks like an ordinary result.
                throw new InvalidOperationException(
                    $"The titleRegex \"{pattern}\" took longer than "
                    + $"{RegexBudget.TotalMilliseconds:F0} ms to test against a window title, so "
                    + "the selector was abandoned.");
            }
            catch (Exception invalid) when (invalid is ArgumentException or NotSupportedException)
            {
                // Also reported. An unparseable pattern is a fault in the plan, and silently
                // matching nothing hides it behind an empty result. Both should already have been
                // refused by PolicyValidator.CheckSelectors before the plan was installed —
                // NotSupportedException is what the linear-time engine raises for lookaround and
                // backreferences — so reaching here means a plan bypassed validation.
                throw new InvalidOperationException(
                    $"The titleRegex \"{pattern}\" cannot be used: " + invalid.Message);
            }
        }

        if (selector.ClassName is { } className
            && !string.Equals(
                Native.GetWindowClass(handle), className, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Last, because it is the dearest: Process.GetProcessById reads the process list and
        // allocates. By here the title and class have already ruled most windows out.
        if (selector.ProcessName is { } process)
        {
            Native.GetWindowThreadProcessId(handle, out var processId);

            if (!string.Equals(ProcessName(processId), process, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

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

    internal static Native.Rect WorkArea(nint window, string? monitor)
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
