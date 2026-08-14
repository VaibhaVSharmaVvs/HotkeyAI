using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Engine.Platform;

/// <summary>
/// Everything the engine needs from the operating system.
/// </summary>
/// <remarks>
/// <para>
/// The engine talks to this and nothing else, so it targets plain <c>net10.0</c> and runs its
/// tests on Linux CI. That is not tidiness: the safety controls — step caps, the panic abort,
/// per-action timeouts, the sensitive-window guard, the run-time path check — are the parts
/// that most need covering, and welding them to Win32 would make every one of them untestable
/// without a desktop session.
/// </para>
/// <para>
/// <c>HotkeyAI.Agent</c> supplies the real implementation; the tests supply a fake.
/// </para>
/// </remarks>
public interface IDesktop
{
    IProcesses Processes { get; }

    IWindows Windows { get; }

    IInput Input { get; }

    IFiles Files { get; }

    IClipboard Clipboard { get; }

    IPrompts Prompts { get; }
}

/// <summary>A window the engine has located.</summary>
/// <param name="Id">Opaque platform handle.</param>
/// <param name="ProcessName">Owning process, without the extension.</param>
/// <param name="Title">Current window title.</param>
/// <param name="IsElevated">
/// True if the window runs at a higher integrity level than this process. Synthetic input
/// cannot reach it and fails <i>silently</i>, so this must be checked rather than discovered.
/// </param>
public readonly record struct WindowRef(long Id, string ProcessName, string Title, bool IsElevated);

/// <summary>Why sending synthetic input right now would be unsafe.</summary>
public enum InputHazard
{
    /// <summary>Nothing in the way.</summary>
    None,

    /// <summary>Foreground is a UAC consent dialog.</summary>
    ConsentPrompt,

    /// <summary>Foreground is a credential prompt, or focus is in a password field.</summary>
    CredentialPrompt,

    /// <summary>Foreground runs elevated, so input would silently go nowhere.</summary>
    ElevatedWindow,
}

public interface IProcesses
{
    /// <summary>Resolve a logical app name to an executable, or null if not installed.</summary>
    ValueTask<string?> ResolveAsync(string logicalName, CancellationToken cancellationToken);

    ValueTask LaunchAsync(
        string executablePath,
        IReadOnlyList<string> argv,
        string? workingDirectory,
        CancellationToken cancellationToken);

    ValueTask<bool> IsRunningAsync(string processName, CancellationToken cancellationToken);

    ValueTask TerminateAsync(string processName, bool force, CancellationToken cancellationToken);
}

public interface IWindows
{
    ValueTask<WindowRef?> FindAsync(WindowSelector selector, CancellationToken cancellationToken);

    ValueTask<string?> ForegroundProcessAsync(CancellationToken cancellationToken);

    ValueTask FocusAsync(WindowRef window, CancellationToken cancellationToken);

    ValueTask MinimiseAsync(WindowRef window, CancellationToken cancellationToken);

    ValueTask MaximiseAsync(WindowRef window, CancellationToken cancellationToken);

    ValueTask CloseAsync(WindowRef window, CancellationToken cancellationToken);

    ValueTask MoveAsync(
        WindowRef window,
        WindowPosition position,
        string? monitor,
        CancellationToken cancellationToken);
}

public interface IInput
{
    /// <summary>Whether synthetic input can safely be sent to the foreground window.</summary>
    ValueTask<InputHazard> CheckHazardAsync(CancellationToken cancellationToken);

    ValueTask SendChordAsync(
        IReadOnlyList<KeyName> keys, int repeat, CancellationToken cancellationToken);

    ValueTask TypeTextAsync(string text, CancellationToken cancellationToken);

    ValueTask SendAppCommandAsync(AppCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Release every modifier this process is holding down.
    /// </summary>
    /// <remarks>
    /// The panic key calls this. An automation interrupted between key-down and key-up leaves
    /// Ctrl or Alt stuck from the OS's point of view, which makes the desktop unusable and
    /// looks like the machine has hung — a worse outcome than the automation that was aborted.
    /// </remarks>
    ValueTask ReleaseModifiersAsync();
}

public interface IFiles
{
    ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(
        string path, int depth, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string path, string? pattern, int depth, CancellationToken cancellationToken);

    ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken);

    ValueTask OpenAsync(string path, CancellationToken cancellationToken);
}

public interface IClipboard
{
    ValueTask<string> ReadAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(string text, CancellationToken cancellationToken);
}

public interface IPrompts
{
    /// <summary>Show the picker. Null means the user cancelled.</summary>
    ValueTask<string?> PickAsync(
        IReadOnlyList<string> items, string? prompt, CancellationToken cancellationToken);

    /// <summary>Ask for a value. Null means the user cancelled.</summary>
    ValueTask<string?> AskAsync(
        string prompt, string? defaultValue, CancellationToken cancellationToken);

    ValueTask NotifyAsync(string message, NotifyLevel level, CancellationToken cancellationToken);

    /// <summary>
    /// Confirm a destructive action. Safety control 5 — <c>terminate_process</c> prompts the
    /// first time an automation uses it.
    /// </summary>
    ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken);
}
