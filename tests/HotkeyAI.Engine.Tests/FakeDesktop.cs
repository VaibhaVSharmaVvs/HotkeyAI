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

    public ValueTask<bool> IsRunningAsync(string processName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RunningProcesses.Contains(processName));

    public async ValueTask TerminateAsync(
        string processName, bool force, CancellationToken cancellationToken)
    {
        await RecordAsync($"terminate:{processName}:{force}").ConfigureAwait(false);
        RunningProcesses.Remove(processName);
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

    public ValueTask<InputHazard> CheckHazardAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Hazard);

    public ValueTask SendChordAsync(
        IReadOnlyList<KeyName> keys, int repeat, CancellationToken cancellationToken) =>
        RecordAsync($"keys:{string.Join('+', keys)}x{repeat}");

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

    public async ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        await RecordAsync($"confirm:{message}").ConfigureAwait(false);
        return ConfirmAnswer;
    }
}
