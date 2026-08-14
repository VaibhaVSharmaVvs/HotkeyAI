using System.Diagnostics;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Execution;

/// <summary>
/// Runs a validated plan and reports what actually happened.
/// </summary>
/// <remarks>
/// <para>
/// Assumes the plan already passed both validation layers. Its job is not to re-check shape but
/// to execute safely and report honestly — which means never claiming an action worked when
/// only "it ran without throwing" is known.
/// </para>
/// <para>
/// Every OS effect goes through <see cref="IDesktop"/>, so this whole type is testable without
/// a desktop session. The safety controls live here rather than in the Win32 layer for exactly
/// that reason.
/// </para>
/// </remarks>
public sealed partial class PlanExecutor(
    IDesktop desktop,
    PathGuard pathGuard,
    ExecutionLimits? limits = null,
    TimeProvider? timeProvider = null)
{
    private readonly ExecutionLimits limits = limits ?? ExecutionLimits.Default;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>Execute a plan.</summary>
    /// <param name="automation">A plan that has passed schema and policy validation.</param>
    /// <param name="cancellationToken">
    /// Cancelled by the panic key. Cancellation is an abort, not a failure to retry.
    /// </param>
    public async Task<ExecutionResult> RunAsync(
        Automation automation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automation);

        var run = new RunState(
            new Variables(automation.Variables),
            [],
            Stopwatch.StartNew());

        try
        {
            await ExecuteAsync(automation.Actions, run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The panic key. Releasing modifiers matters more than the log line: an automation
            // stopped between key-down and key-up leaves Ctrl or Alt stuck system-wide, which
            // looks exactly like a hung machine.
            await SafelyReleaseModifiersAsync().ConfigureAwait(false);
            run.Stop("Stopped by the panic key.", null);
            Log(run, null, "abort", StepOutcome.Aborted, Verification.None,
                "Run cancelled; held modifier keys released.");
        }
#pragma warning disable CA1031 // A plan must never take the agent down with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await SafelyReleaseModifiersAsync().ConfigureAwait(false);
            run.Stop($"The engine hit an unexpected error: {ex.Message}", null);
        }

        return new ExecutionResult(
            run.StoppedBecause is null,
            run.Entries,
            run.StoppedBecause,
            run.FailedActionId);
    }

    // ---------------------------------------------------------------------------------

    private async Task ExecuteAsync(
        IReadOnlyList<HotkeyAction> actions, RunState run, CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            if (run.StoppedBecause is not null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!CheckLimits(run, action))
            {
                return;
            }

            await ExecuteOneAsync(action, run, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Safety control 1: step and wall-clock caps.</summary>
    private bool CheckLimits(RunState run, HotkeyAction action)
    {
        if (run.Steps >= limits.MaxSteps)
        {
            run.Stop(
                $"Step cap reached ({limits.MaxSteps} actions). The plan was stopped before it "
                + "could run away.",
                action.Id);
            return false;
        }

        if (run.Elapsed > limits.MaxDuration)
        {
            run.Stop(
                $"Time cap reached ({limits.MaxDuration.TotalSeconds:F0}s). The plan was stopped "
                + "before it could hold the desktop.",
                action.Id);
            return false;
        }

        return true;
    }

    private async Task ExecuteOneAsync(
        HotkeyAction action, RunState run, CancellationToken cancellationToken)
    {
        run.Steps++;

        var type = Discriminator(action);
        var timeout = action is VerifiableAction { TimeoutMs: { } ms }
            ? TimeSpan.FromMilliseconds(ms)
            : limits.DefaultActionTimeout;

        // Two sources: the per-action timeout, and the outer token the panic key cancels.
        // Linked so either stops the action, but the catch below can still tell them apart —
        // a timeout fails one step, the panic key aborts the run.
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var perAction = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);

        StepOutcome outcome;
        string detail;

        try
        {
            (outcome, detail) = await DispatchAsync(action, run, perAction.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // The panic key, not this action's timeout.
        }
        catch (OperationCanceledException)
        {
            outcome = StepOutcome.Failed;
            detail = $"Timed out after {timeout.TotalMilliseconds:F0} ms.";
        }
#pragma warning disable CA1031 // One bad action must not abort the whole agent.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = StepOutcome.Failed;
            detail = ex.Message;
        }

        var verification = Verification.None;

        if (outcome == StepOutcome.Succeeded && action is VerifiableAction { Expect: { } expect })
        {
            var (passed, why) = await VerifyAsync(expect, run, cancellationToken)
                .ConfigureAwait(false);

            verification = passed ? Verification.Passed : Verification.Failed;
            if (!passed)
            {
                outcome = StepOutcome.Failed;
                detail = why;
            }
        }

        Log(run, action.Id, type, outcome, verification, detail);

        if (outcome is StepOutcome.Failed
            && action is not VerifiableAction { OnError: OnErrorBehaviour.Continue })
        {
            run.Stop(detail, action.Id);
        }
        else if (outcome is StepOutcome.Aborted)
        {
            run.Stop(detail, action.Id);
        }
    }

    // ------------------------------- postconditions -------------------------------

    /// <summary>
    /// Poll a postcondition until it holds or its window expires.
    /// </summary>
    /// <remarks>
    /// Only these five checks are decidable, which is why the DSL offers no others. An action
    /// with no postcondition is logged as unverified rather than assumed to have worked — the
    /// engine genuinely cannot tell, and saying otherwise would make the log a lie.
    /// </remarks>
    private async Task<(bool Passed, string Detail)> VerifyAsync(
        Postcondition expect, RunState run, CancellationToken cancellationToken)
    {
        var window = expect.WithinMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : limits.DefaultVerificationTimeout;

        var deadline = clock.GetUtcNow() + window;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await HoldsAsync(expect, run, cancellationToken).ConfigureAwait(false))
            {
                return (true, "");
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return (false,
                    $"Postcondition not met within {window.TotalMilliseconds:F0} ms: "
                    + Core.PlanRenderer.Describe(expect));
            }

            await Task.Delay(limits.PollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> HoldsAsync(
        Postcondition expect, RunState run, CancellationToken cancellationToken) => expect switch
    {
        ProcessRunningExpectation e =>
            await desktop.Processes.IsRunningAsync(e.ProcessName, cancellationToken)
                .ConfigureAwait(false),

        WindowExistsExpectation e =>
            await FindWindowAsync(e.Selector, run, cancellationToken)
                .ConfigureAwait(false) is not null,

        PathExistsExpectation e =>
            await ExistsWithinRootsAsync(run.Variables.Interpolate(e.Path), cancellationToken)
                .ConfigureAwait(false),

        ForegroundProcessIsExpectation e =>
            string.Equals(
                await desktop.Windows.ForegroundProcessAsync(cancellationToken)
                    .ConfigureAwait(false),
                e.ProcessName,
                StringComparison.OrdinalIgnoreCase),

        ClipboardMatchesExpectation e => Matches(
            await desktop.Clipboard.ReadAsync(cancellationToken).ConfigureAwait(false),
            run.Variables.Interpolate(e.Exactly),
            run.Variables.Interpolate(e.Contains),
            e.Exactly is not null),

        _ => false,
    };

    private static bool Matches(string actual, string exact, string contains, bool exactly) =>
        exactly
            ? string.Equals(actual, exact, StringComparison.Ordinal)
            : actual.Contains(contains, StringComparison.Ordinal);

    private async Task<bool> ExistsWithinRootsAsync(string path, CancellationToken cancellationToken)
    {
        // A postcondition must not become a way to probe outside the allowed roots.
        return pathGuard.IsAllowed(path, out _)
               && await desktop.Files.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------- plumbing -------------------------------

    private async ValueTask<WindowRef?> FindWindowAsync(
        WindowSelector selector, RunState run, CancellationToken cancellationToken)
    {
        // Selectors can interpolate, so resolve before matching.
        var resolved = selector with
        {
            TitleContains = selector.TitleContains is null
                ? null
                : run.Variables.Interpolate(selector.TitleContains),
        };

        return await desktop.Windows.FindAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task SafelyReleaseModifiersAsync()
    {
        try
        {
            await desktop.Input.ReleaseModifiersAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: nothing useful to do if even this fails.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Swallowed deliberately — this runs on the abort path, and throwing here would
            // replace a clean abort with an unhandled exception.
        }
    }

    private static void Log(
        RunState run,
        string? actionId,
        string type,
        StepOutcome outcome,
        Verification verification,
        string detail) =>
        run.Entries.Add(new LogEntry(
            DateTimeOffset.Now, actionId, type, outcome, verification, detail));

    private static string Discriminator(HotkeyAction action) =>
        action.GetType()
            .GetCustomAttributes(typeof(Core.Json.DslTypeAttribute), false) is
            [Core.Json.DslTypeAttribute attribute]
            ? attribute.Discriminator
            : action.GetType().Name;

    private sealed record RunState(
        Variables Variables, List<LogEntry> Entries, Stopwatch Timer)
    {
        public int Steps { get; set; }

        public string? StoppedBecause { get; private set; }

        public string? FailedActionId { get; private set; }

        public TimeSpan Elapsed => Timer.Elapsed;

        public void Stop(string reason, string? actionId)
        {
            StoppedBecause ??= reason;
            FailedActionId ??= actionId;
        }
    }
}
