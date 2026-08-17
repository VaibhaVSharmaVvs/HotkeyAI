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
/// <remarks>
/// There used to be an <c>IsElevated</c> here, and nothing in the repository ever read it — while
/// computing it cost three syscalls for every visible window on every window search, including each
/// 150 ms poll of a <c>wait_for_window</c>. Security review 2026-08-17, finding L9.
/// <para>
/// Its absence is not a gap. The integrity check that matters is
/// <c>IInput.CheckHazardAsync</c>, which asks about the window that is actually about to receive
/// input, at the moment it is about to receive it — a field on a record fetched earlier would be a
/// weaker answer to a question that is only ever asked about the foreground.
/// </para>
/// </remarks>
public readonly record struct WindowRef(long Id, string ProcessName, string Title);

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

    /// <summary>
    /// The window that was going to receive this input is no longer the one in front.
    /// </summary>
    /// <remarks>
    /// Security review 2026-08-17, finding M7. The hazard check happened once per action, and a
    /// 2 000-character <c>type_text</c> occupies the foreground for ten seconds afterwards. Anything
    /// that takes focus in that window receives the remainder — a UAC prompt appearing, the user
    /// alt-tabbing to their password manager. The other hazards catch a *dangerous* new window;
    /// this catches a merely different one, which is the case Windows will happily accept.
    /// </remarks>
    FocusMoved,
}

/// <summary>
/// What resolving a logical app name produced.
/// </summary>
/// <param name="Path">The executable, or null when there is nothing to launch.</param>
/// <param name="Refusal">
/// Why a resolved executable was rejected, or null. Separate from "not installed" because the two
/// need different words: one is a missing application, the other is an application found somewhere
/// it should not be — which is a warning, not an absence. Security review 2026-08-17, finding H5.
/// </param>
public readonly record struct AppResolution(string? Path, string? Refusal)
{
    public static AppResolution None => new(null, null);

    public static AppResolution At(string path) => new(path, null);

    public static AppResolution Refused(string why) => new(null, why);
}

public interface IProcesses
{
    /// <summary>Resolve a logical app name to an executable.</summary>
    ValueTask<AppResolution> ResolveAsync(string logicalName, CancellationToken cancellationToken);

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

    /// <summary>
    /// An opaque identity for whichever window would receive input right now, or 0 for none.
    /// </summary>
    /// <remarks>
    /// Only ever compared, never interpreted — the executor captures it before a long piece of
    /// input and checks it has not changed partway through. A process name would be the cheaper
    /// signal and the wrong one: typing a password into the wrong document of the right
    /// application is exactly the mistake worth catching.
    /// </remarks>
    ValueTask<long> ForegroundWindowIdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Send one chord.
    /// </summary>
    /// <remarks>
    /// One, not <c>repeat</c> of them. The repeat loop lives in the executor so the sensitive-window
    /// guard is re-run between iterations — security review 2026-08-17, finding M7. A loop down here
    /// is a loop the safety controls cannot see into.
    /// </remarks>
    ValueTask SendChordAsync(IReadOnlyList<KeyName> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Type a run of text, one character at a time.
    /// </summary>
    /// <remarks>
    /// The executor calls this in short chunks rather than handing over a whole payload, for the
    /// reason above: typing paces at 5 ms per character, so a long string is many seconds during
    /// which nothing is checking where the characters are going.
    /// </remarks>
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
