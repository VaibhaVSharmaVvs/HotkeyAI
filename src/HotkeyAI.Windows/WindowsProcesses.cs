using System.Diagnostics;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>Process launch, lookup and termination.</summary>
public sealed class WindowsProcesses(AppResolver resolver) : IProcesses
{
    public ValueTask<AppResolution> ResolveAsync(string logicalName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(resolver.ResolveForLaunch(logicalName));

    public ValueTask LaunchAsync(
        string executablePath,
        IReadOnlyList<string> argv,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var start = new ProcessStartInfo
        {
            FileName = executablePath,

            // The load-bearing line. ArgumentList passes each element as a separate argument,
            // and UseShellExecute stays false, so nothing on this path is ever parsed as a
            // command line. A plan variable containing "; del /q *" is an argument, not a
            // command — which is why the DSL can afford to have no shell primitive at all.
            UseShellExecute = false,
        };

        foreach (var argument in argv)
        {
            start.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            start.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(start);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsRunningAsync(string processName, CancellationToken cancellationToken)
    {
        var trimmed = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var running = Process.GetProcessesByName(trimmed);
        try
        {
            return ValueTask.FromResult(running.Length > 0);
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    public ValueTask TerminateAsync(
        string processName, bool force, CancellationToken cancellationToken)
    {
        var trimmed = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        foreach (var process in Process.GetProcessesByName(trimmed))
        {
            using (process)
            {
                try
                {
                    if (force)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    else
                    {
                        // Ask the main window to close so the application can save. Only fall
                        // back to Kill when it has no window to ask.
                        if (!process.CloseMainWindow())
                        {
                            process.Kill();
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or protected. The postcondition is what decides whether the
                    // action succeeded, so swallowing here does not hide a failure.
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
