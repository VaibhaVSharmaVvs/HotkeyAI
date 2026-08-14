using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Ui;

/// <summary>
/// The WPF implementation of the engine's user-facing prompts.
/// </summary>
/// <remarks>
/// Swapped in for <c>ConsolePrompts</c> wherever there is a desktop to draw on. The engine cannot
/// tell the difference, which is the point of <see cref="IPrompts"/> — the CLI keeps the console
/// versions so automations stay runnable from a terminal and from CI.
/// <para>
/// Every method here hops to <see cref="UiThread"/> and blocks the calling thread until the user
/// answers. That is correct: the automation genuinely is waiting on a choice. The agent runs
/// executions off its main thread, so the hotkey pump and the panic key keep running throughout.
/// </para>
/// </remarks>
public sealed class WpfPrompts : IPrompts
{
    public async ValueTask<string?> PickAsync(
        IReadOnlyList<string> items, string? prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        return await Safely("the picker", null, () =>
        {
            var window = new PickerWindow(items, prompt);
            using var cancellation = Register(window, cancellationToken);
            return window.Pick();
        }).ConfigureAwait(false);
    }

    public async ValueTask<string?> AskAsync(
        string prompt, string? defaultValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Safely("the input prompt", null, () =>
        {
            var window = new InputWindow(prompt, defaultValue);
            using var cancellation = Register(window, cancellationToken);
            return window.Ask();
        }).ConfigureAwait(false);
    }

    public ValueTask NotifyAsync(
        string message, NotifyLevel level, CancellationToken cancellationToken)
    {
        // Deliberately not awaited. A toast is an announcement, not a question, and an automation
        // that paused for three seconds on every notify would be unusable.
        UiThread.Shared.Post(() => ToastWindow.Post(message, level));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        // A cancelled run declines rather than throwing. This gate guards destructive actions,
        // so the safe answer is the one to give when nobody is there to answer.
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return await Safely("the confirmation prompt", false, () =>
        {
            var window = new ConfirmWindow(message);
            using var cancellation = Register(window, cancellationToken);
            return window.Confirm();
        }).ConfigureAwait(false);
    }


    /// <summary>
    /// Run an overlay, and treat a crash as the user declining rather than as a fatal error.
    /// </summary>
    /// <remarks>
    /// The agent hosts these windows in its own process, and that process owns every hotkey and
    /// the panic key. An unhandled exception on the UI thread would therefore take the whole
    /// automation system down because a prompt failed to draw — which is precisely the coupling
    /// PLAN.md's two-process split exists to avoid, and the reason it has to be paid for here
    /// instead. Not hypothetical: WPF's caret needed culture data that invariant globalization
    /// refused to provide, and the first run of the picker killed the process outright.
    /// <para>
    /// Failing to null or false is the safe direction. The engine already treats a cancelled
    /// picker as a failed action and reports it, so a broken overlay stops the automation and
    /// says so rather than silently choosing something.
    /// </para>
    /// </remarks>
    private static async Task<T> Safely<T>(string what, T fallback, Func<T> work)
    {
        try
        {
            return await UiThread.Shared.InvokeAsync(work).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failing overlay must never take the agent down with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Hotkey AI: {what} failed to open — {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Close the overlay if the run is cancelled while it is open.
    /// </summary>
    /// <remarks>
    /// The callback arrives on whichever thread cancelled, so it posts back to the UI thread. The
    /// overlay's nested message loop is still pumping, which is what lets the post be delivered
    /// instead of deadlocking against the window it is trying to close.
    /// </remarks>
    private static CancellationTokenRegistration Register(
        OverlayWindow window, CancellationToken cancellationToken) =>
        cancellationToken.Register(() => UiThread.Shared.Post(window.Dismiss));
}
