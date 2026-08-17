using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// An in-memory desktop, so the engine's behaviour can be tested without one.
/// </summary>
/// <remarks>
/// Records every effect the engine attempted. Tests assert on <see cref="Effects"/> because
/// "the engine refused to do X" is only meaningful if you can show X was never attempted — for
/// a safety control, checking the returned error is not enough.
/// </remarks>
internal sealed class FakeDesktop : IDesktop, IProcesses, IWindows, IInput, IFiles, IClipboard, IPrompts
{
    public List<string> Effects { get; } = [];

    public IProcesses Processes => this;

    public IWindows Windows => this;

    public IInput Input => this;

    public IFiles Files => this;

    public IClipboard Clipboard => this;

    public IPrompts Prompts => this;

    // ------------------------------- knobs -------------------------------

    public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> InstalledApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<WindowRef> OpenWindows { get; } = [];

    public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ClipboardText { get; set; } = "";

    public string? ForegroundProcess { get; set; }

    public InputHazard Hazard { get; set; } = InputHazard.None;

    /// <summary>How many times the sensitive-window guard has been consulted.</summary>
    /// <remarks>
    /// Counted so a test can assert the guard runs *during* a long piece of input and not only
    /// before it — security review 2026-08-17, finding M7.
    /// </remarks>
    public int HazardChecks { get; private set; }

    /// <summary>Which window would receive input. Change it mid-run to simulate focus moving.</summary>
    public long ForegroundWindowId { get; set; } = 1;

    /// <summary>What the picker returns. Null simulates the user cancelling.</summary>
    public string? PickerChoice { get; set; }

    public string? InputAnswer { get; set; }

    public bool ConfirmAnswer { get; set; } = true;

    public int ModifiersReleasedCount { get; private set; }

    /// <summary>Runs before each effect, so a test can cancel or stall mid-run.</summary>
    public Func<string, Task>? OnEffect { get; set; }

    private async ValueTask RecordAsync(string effect)
    {
        Effects.Add(effect);

        if (OnEffect is { } hook)
        {
            await hook(effect).ConfigureAwait(false);
        }
    }

    // ------------------------------- processes -------------------------------

    public ValueTask<AppResolution> ResolveAsync(string logicalName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(InstalledApps.GetValueOrDefault(logicalName) is { } path
            ? AppResolution.At(path)
            : AppResolution.None);

    public async ValueTask LaunchAsync(
        string executablePath,
        IReadOnlyList<string> argv,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        await RecordAsync($"launch:{executablePath}:{string.Join('|', argv)}").ConfigureAwait(false);
        RunningProcesses.Add(executablePath);

        // Also under the name the process would actually have. Real plans launch an app and then
        // wait on `process_running` for it, and a fake that only remembers the full path can never
        // satisfy that — the postcondition polls for its whole timeout and the test pays for it in
        // wall-clock. Recording both keeps path-based assertions working and makes the realistic
        // launch-then-verify shape testable.
        var name = executablePath;
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length > 0)
        {
            RunningProcesses.Add(name);
        }
    }

    /// <summary>
    /// Set to make the process check throw, simulating the desktop layer failing outright.
    /// </summary>
    /// <remarks>
    /// A hook here rather than on <c>OnEffect</c> because it has to fire somewhere the executor's
    /// per-action catch cannot see: an exception from a dispatch is reported as one failed step, by
    /// design, and never reaches the run-level handler that security review 2026-08-17 finding L10 is
    /// about. Verification runs outside that guard, so a postcondition asking about a process is the
    /// shortest honest route to it.
    /// </remarks>
    public Exception? ProcessCheckThrows { get; set; }

    public ValueTask<bool> IsRunningAsync(string processName, CancellationToken cancellationToken) =>
        ProcessCheckThrows is { } boom
            ? throw boom
            : ValueTask.FromResult(RunningProcesses.Contains(processName));

    /// <summary>
    /// How many processes a name stands for. One unless a test says otherwise.
    /// </summary>
    /// <remarks>
    /// Security review 2026-08-17, finding L3: the confirmation prompt said "Close chrome?" while the
    /// terminate killed every process of that name. Testing the corrected prompt needs a fake that
    /// can have more than one.
    /// </remarks>
    public Dictionary<string, int> ProcessCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<int> CountAsync(string processName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            ProcessCounts.TryGetValue(processName, out var many)
                ? many
                : RunningProcesses.Contains(processName) ? 1 : 0);

    public async ValueTask<int> TerminateAsync(
        string processName, bool force, CancellationToken cancellationToken)
    {
        await RecordAsync($"terminate:{processName}:{force}").ConfigureAwait(false);

        var closed = await CountAsync(processName, cancellationToken).ConfigureAwait(false);

        RunningProcesses.Remove(processName);
        ProcessCounts.Remove(processName);

        return closed;
    }

    // ------------------------------- windows -------------------------------

    public ValueTask<WindowRef?> FindAsync(
        WindowSelector selector, CancellationToken cancellationToken)
    {
        var match = OpenWindows.FirstOrDefault(w =>
            (selector.ProcessName is null
             || string.Equals(w.ProcessName, selector.ProcessName, StringComparison.OrdinalIgnoreCase))
            && (selector.TitleContains is null
                || w.Title.Contains(selector.TitleContains, StringComparison.OrdinalIgnoreCase)));

        return ValueTask.FromResult(match == default ? null : (WindowRef?)match);
    }

    public ValueTask<string?> ForegroundProcessAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ForegroundProcess);

    public async ValueTask FocusAsync(WindowRef window, CancellationToken cancellationToken)
    {
        await RecordAsync($"focus:{window.ProcessName}").ConfigureAwait(false);
        ForegroundProcess = window.ProcessName;
    }

    public ValueTask MinimiseAsync(WindowRef window, CancellationToken cancellationToken) =>
        RecordAsync($"minimise:{window.ProcessName}");

    public ValueTask MaximiseAsync(WindowRef window, CancellationToken cancellationToken) =>
        RecordAsync($"maximise:{window.ProcessName}");

    public ValueTask CloseAsync(WindowRef window, CancellationToken cancellationToken) =>
        RecordAsync($"close:{window.ProcessName}");

    public ValueTask MoveAsync(
        WindowRef window,
        WindowPosition position,
        string? monitor,
        CancellationToken cancellationToken) =>
        RecordAsync($"move:{window.ProcessName}:{position}");

    // ------------------------------- input -------------------------------

    public ValueTask<InputHazard> CheckHazardAsync(CancellationToken cancellationToken)
    {
        HazardChecks++;
        return ValueTask.FromResult(Hazard);
    }

    public ValueTask<long> ForegroundWindowIdAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ForegroundWindowId);

    public ValueTask SendChordAsync(
        IReadOnlyList<KeyName> keys, CancellationToken cancellationToken) =>
        RecordAsync($"keys:{string.Join('+', keys)}");

    public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken) =>
        RecordAsync($"type:{text}");

    public ValueTask SendAppCommandAsync(AppCommand command, CancellationToken cancellationToken) =>
        RecordAsync($"appcommand:{command}");

    public ValueTask ReleaseModifiersAsync()
    {
        ModifiersReleasedCount++;
        Effects.Add("release-modifiers");
        return ValueTask.CompletedTask;
    }

    // ------------------------------- files -------------------------------

    public async ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(
        string path, int depth, CancellationToken cancellationToken)
    {
        await RecordAsync($"list-dirs:{path}").ConfigureAwait(false);
        return Directories.GetValueOrDefault(path, []);
    }

    public async ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string path, string? pattern, int depth, CancellationToken cancellationToken)
    {
        await RecordAsync($"list-files:{path}:{pattern}").ConfigureAwait(false);
        return Directories.GetValueOrDefault(path, []);
    }

    public ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExistingPaths.Contains(path));

    public ValueTask OpenAsync(string path, CancellationToken cancellationToken) =>
        RecordAsync($"open:{path}");

    // ------------------------------- clipboard -------------------------------

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ClipboardText);

    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        await RecordAsync($"clipboard:{text}").ConfigureAwait(false);
        ClipboardText = text;
    }

    // ------------------------------- prompts -------------------------------

    public async ValueTask<string?> PickAsync(
        IReadOnlyList<string> items, string? prompt, CancellationToken cancellationToken)
    {
        await RecordAsync($"pick:{items.Count}").ConfigureAwait(false);
        return PickerChoice;
    }

    public async ValueTask<string?> AskAsync(
        string prompt, string? defaultValue, CancellationToken cancellationToken)
    {
        await RecordAsync($"ask:{prompt}").ConfigureAwait(false);
        return InputAnswer;
    }

    public ValueTask NotifyAsync(
        string message, NotifyLevel level, CancellationToken cancellationToken) =>
        RecordAsync($"notify:{level}:{message}");

    /// <summary>The last question the user was asked, or null if they were never asked.</summary>
    /// <remarks>
    /// Kept separately from <see cref="Effects"/> because the wording is the thing under test for
    /// security review 2026-08-17 finding L3, and picking it back out of an effect string would mean
    /// the test asserting on a prefix it does not care about.
    /// </remarks>
    public string? ConfirmQuestion { get; private set; }

    /// <summary>Called for each confirmation, so a test can count them.</summary>
    public Action<string>? OnConfirm { get; set; }

    public async ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        ConfirmQuestion = message;
        OnConfirm?.Invoke(message);

        await RecordAsync($"confirm:{message}").ConfigureAwait(false);
        return ConfirmAnswer;
    }
}
