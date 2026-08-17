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
    public Task<ExecutionResult> RunAsync(
        Automation automation, CancellationToken cancellationToken = default) =>
        RunAsync(automation, null, cancellationToken);

    /// <summary>Execute a plan, with something watching it happen.</summary>
    /// <param name="automation">A plan that has passed schema and policy validation.</param>
    /// <param name="observer">
    /// Watcher for test-run mode, told about each step as it starts and finishes. It cannot
    /// influence the run: anything it throws is swallowed.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelled by the panic key. Cancellation is an abort, not a failure to retry.
    /// </param>
    /// <remarks>
    /// A separate overload rather than a third optional parameter, because the analyzer requires
    /// the token last and moving it would silently rewrite the meaning of every existing
    /// two-argument call. The token is deliberately not optional here, so <c>RunAsync(plan)</c>
    /// stays unambiguous.
    /// </remarks>
    public async Task<ExecutionResult> RunAsync(
        Automation automation,
        IRunObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(automation);

        var run = new RunState(
            new Variables(automation.Variables),
            [],
            clock)
        {
            Observer = observer,
        };

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

        // Announced before it runs, not after. An action is logged when it finishes, so without
        // this a long wait or a hung launch shows nothing at all on a live view — which is the
        // one moment someone watching most needs to know what they are waiting for.
        run.Announce(type, action.Id);

        var asked = action is VerifiableAction { TimeoutMs: { } ms }
            ? TimeSpan.FromMilliseconds(ms)
            : limits.DefaultActionTimeout;

        // Whichever runs out first: the action's own timeout, or what is left of the run's budget.
        //
        // Security review 2026-08-17, finding M5. The wall-clock cap was only evaluated *between*
        // actions, so a single action could run for its own timeoutMs — bounded by policy at
        // 300 000 ms, two and a half times the documented 120 s cap. The panic key still worked,
        // because cancellation is cooperative and honoured throughout, but the engine's own escape
        // hatch did not: PLAN.md describes the cap as being there for when the user cannot get to
        // the keyboard, and it was not bounding the run at all.
        var remaining = limits.MaxDuration - run.Elapsed;
        var timeout = remaining < asked ? remaining : asked;

        if (timeout <= TimeSpan.Zero)
        {
            // The budget is already spent. CheckLimits catches this before most actions, but an
            // action reached with nothing left must not be given an immediate deadline and reported
            // as a timeout of its own — that would blame the step for the run's overrun.
            run.Stop(
                $"Time cap reached ({limits.MaxDuration.TotalSeconds:F0}s). The plan was stopped "
                + "before it could hold the desktop.",
                action.Id);

            Log(run, action.Id, type, StepOutcome.Aborted, Verification.None,
                "Not started: the run's time cap was already reached.");

            return;
        }

        // Two sources: the effective timeout above, and the outer token the panic key cancels.
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
            // Two different events wear the same exception, and they deserve different words. An
            // action that outran its own timeout is one failed step; an action cut short because
            // the run's budget expired is the whole run ending, and blaming the step for that would
            // send someone looking at the wrong thing.
            if (timeout < asked)
            {
                outcome = StepOutcome.Aborted;
                detail =
                    $"Time cap reached ({limits.MaxDuration.TotalSeconds:F0}s) while this action "
                    + "was running. The plan was stopped before it could hold the desktop.";

                // The tail of this method stops the run for an Aborted outcome, so no Stop here —
                // and the outcome matters beyond the message: Aborted is not subject to
                // onError: continue, which would otherwise let a plan step straight past the cap.
            }
            else
            {
                outcome = StepOutcome.Failed;
                detail = $"Timed out after {timeout.TotalMilliseconds:F0} ms.";
            }
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
        // Checked before polling rather than by waiting out the window. A postcondition whose
        // comparison value interpolated to nothing can never pass, so spending five seconds
        // discovering that — and then reporting a generic miss — tells the user neither what
        // happened nor why.
        if (Vacuous(expect, run) is { } why)
        {
            return (false, why);
        }

        var asked = expect.WithinMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : limits.DefaultVerificationTimeout;

        // Clipped to the run's remaining budget, for the same reason the action timeout above is:
        // polling is time spent inside a step, and the wall-clock cap is only consulted between
        // them. withinMs goes up to 60 000 ms, so a plan whose steps each verify slowly could sit
        // well past the cap without any single number looking unreasonable.
        var remaining = limits.MaxDuration - run.Elapsed;
        var window = remaining < asked ? remaining : asked;

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
                return (false, window < asked
                    ? $"Time cap reached ({limits.MaxDuration.TotalSeconds:F0}s) while waiting "
                      + "for: " + Core.PlanRenderer.Describe(expect)
                    : $"Postcondition not met within {window.TotalMilliseconds:F0} ms: "
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

    /// <summary>
    /// Why this postcondition could never hold, or null if it is a real question.
    /// </summary>
    /// <remarks>
    /// Security review 2026-08-17, finding M1. An unset variable interpolates to the empty string,
    /// and an empty comparison value turns a check into a tautology or an impossibility depending
    /// on which one it is. Either way the plan asked a question it cannot get a meaningful answer
    /// to, and saying so beats reporting a verdict that means nothing.
    /// <para>
    /// Named after the fault rather than the fix, because the underlying cause is almost always a
    /// variable the plan never wrote — which the policy validator now also catches inside
    /// expectations (finding M2), so a plan reaching this at run time is the rarer case of a
    /// variable that was declared and assigned but ended up empty.
    /// </para>
    /// </remarks>
    private static string? Vacuous(Postcondition expect, RunState run) => expect switch
    {
        ClipboardMatchesExpectation { Exactly: null } e
            when run.Variables.Interpolate(e.Contains).Length == 0 =>
            "The text to look for in the clipboard interpolated to nothing, so this check could "
            + "not pass or fail on its own terms. A variable it names was never given a value.",

        PathExistsExpectation e when run.Variables.Interpolate(e.Path).Length == 0 =>
            "The path to check interpolated to nothing. A variable it names was never given a "
            + "value.",

        _ => null,
    };

    /// <summary>
    /// Whether the clipboard satisfies a <c>clipboard_matches</c> expectation.
    /// </summary>
    /// <remarks>
    /// An empty needle fails rather than passes. Security review 2026-08-17, finding M1: an unset
    /// variable interpolates to the empty string, so <c>contains: "${ghost}"</c> became
    /// <c>Contains("")</c> — true of every string ever — and the step was reported as
    /// <c>(verified)</c> while verifying nothing. That is the one failure the engine's honesty
    /// story cannot absorb: the entire point of counting unverified actions is that "it ran" and
    /// "it worked" stay separate claims, and a vacuous check silently upgrades the weaker one.
    /// <para>
    /// Deliberately not treated as "no expectation given" either. The plan asked for a check, so
    /// the check has to have an answer, and the honest answer is no.
    /// </para>
    /// </remarks>
    private static bool Matches(string actual, string exact, string contains, bool exactly) =>
        exactly
            ? string.Equals(actual, exact, StringComparison.Ordinal)
            : contains.Length > 0 && actual.Contains(contains, StringComparison.Ordinal);

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
        string detail)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now, actionId, type, outcome, verification, detail);

        run.Entries.Add(entry);
        run.Publish(entry);
    }

    /// <summary>
    /// Maps each action record to the <c>type</c> the plan actually wrote.
    /// </summary>
    /// <remarks>
    /// Read from the base type, not the derived one: <c>[DslType]</c> is declared once on
    /// <see cref="HotkeyAction"/> listing every derived record, so asking a concrete record for
    /// its own attribute finds nothing and falls back to the CLR name. That is not cosmetic —
    /// the execution log is read by the user and pasted into repair prompts, and a log saying
    /// <c>ListDirectoriesAction</c> does not match anything in the plan the person is holding.
    /// </remarks>
    private static readonly Dictionary<Type, string> Discriminators =
        typeof(HotkeyAction)
            .GetCustomAttributes(typeof(Core.Json.DslTypeAttribute), inherit: false)
            .Cast<Core.Json.DslTypeAttribute>()
            .ToDictionary(a => a.DerivedType, a => a.Discriminator);

    private static string Discriminator(HotkeyAction action) =>
        Discriminators.TryGetValue(action.GetType(), out var name) ? name : action.GetType().Name;

    /// <summary>
    /// One run's mutable state, including the clock the wall-clock cap is measured against.
    /// </summary>
    /// <remarks>
    /// The clock is the executor's <see cref="TimeProvider"/>, not a <see cref="Stopwatch"/>, and the
    /// distinction is what makes the cap testable. It used to be a Stopwatch while every deadline in
    /// this file came from the TimeProvider, so a test could move the clock and the cap would not
    /// notice — which is why the 120 s cap had no test at all before security review 2026-08-17.
    /// </remarks>
    private sealed record RunState(
        Variables Variables, List<LogEntry> Entries, TimeProvider Clock)
    {
        private readonly long started = Clock.GetTimestamp();

        public int Steps { get; set; }

        public string? StoppedBecause { get; private set; }

        public string? FailedActionId { get; private set; }

        public IRunObserver? Observer { get; init; }

        public TimeSpan Elapsed => Clock.GetElapsedTime(started);

        public void Stop(string reason, string? actionId)
        {
            StoppedBecause ??= reason;
            FailedActionId ??= actionId;
        }

        public void Announce(string type, string? actionId) =>
            Safely(o => o.Starting(type, actionId));

        public void Publish(LogEntry entry) => Safely(o => o.Finished(entry));

        /// <summary>
        /// Tell the observer something, and never let it affect the run.
        /// </summary>
        /// <remarks>
        /// The observer is a UI in every real case, and a UI can throw for reasons that have
        /// nothing to do with the automation — a window closed mid-run, a dispatcher shut down.
        /// Letting that escape would mean closing the test-run window aborted the automation it
        /// was watching, which is a spectacular way to break something that was working.
        /// </remarks>
        private void Safely(Action<IRunObserver> tell)
        {
            if (Observer is null)
            {
                return;
            }

            try
            {
                tell(Observer);
            }
#pragma warning disable CA1031 // Watching must not be able to break running.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
