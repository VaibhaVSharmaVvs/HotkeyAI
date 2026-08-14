using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Execution;

/// <summary>Per-action behaviour. The safety machinery lives in the other half.</summary>
public sealed partial class PlanExecutor
{
    private async Task<(StepOutcome Outcome, string Detail)> DispatchAsync(
        HotkeyAction action, RunState run, CancellationToken token) => action switch
    {
        // ------------------------------- process -------------------------------
        LaunchProcessAction a => await LaunchAsync(a, run, token).ConfigureAwait(false),

        TerminateProcessAction a => await TerminateAsync(a, token).ConfigureAwait(false),

        WaitForProcessAction a =>
            await PollAsync(
                () => desktop.Processes.IsRunningAsync(a.ProcessName, token),
                $"process \"{a.ProcessName}\" did not start",
                a.TimeoutMs,
                token).ConfigureAwait(false),

        // ------------------------------- window -------------------------------
        FocusWindowAction a => await OnWindowAsync(
            a.Selector, run, (w, t) => desktop.Windows.FocusAsync(w, t), "Focused", token),

        MinimizeWindowAction a => await OnWindowAsync(
            a.Selector, run, (w, t) => desktop.Windows.MinimiseAsync(w, t), "Minimised", token),

        MaximizeWindowAction a => await OnWindowAsync(
            a.Selector, run, (w, t) => desktop.Windows.MaximiseAsync(w, t), "Maximised", token),

        CloseWindowAction a => await OnWindowAsync(
            a.Selector, run, (w, t) => desktop.Windows.CloseAsync(w, t), "Asked to close", token),

        MoveWindowAction a => await OnWindowAsync(
            a.Selector, run,
            (w, t) => desktop.Windows.MoveAsync(w, a.Position, a.Monitor, t),
            $"Moved to {a.Position}", token),

        WaitForWindowAction a =>
            await PollAsync(
                async () => await FindWindowAsync(a.Selector, run, token)
                    .ConfigureAwait(false) is not null,
                "the window did not appear",
                a.TimeoutMs,
                token).ConfigureAwait(false),

        // ------------------------------- input -------------------------------
        SendKeysAction a => await SendKeysAsync(a, token).ConfigureAwait(false),

        TypeTextAction a => await TypeTextAsync(a, run, token).ConfigureAwait(false),

        SendAppCommandAction a => await Do(
            () => desktop.Input.SendAppCommandAsync(a.Command, token),
            $"Sent {a.Command}.").ConfigureAwait(false),

        // ------------------------------- files -------------------------------
        ListDirectoriesAction a => await ListAsync(
            run.Variables.Interpolate(a.Path), a.Into, run,
            path => desktop.Files.ListDirectoriesAsync(path, a.Depth ?? 1, token), token)
            .ConfigureAwait(false),

        ListFilesAction a => await ListAsync(
            run.Variables.Interpolate(a.Path), a.Into, run,
            path => desktop.Files.ListFilesAsync(path, a.Pattern, a.Depth ?? 1, token), token)
            .ConfigureAwait(false),

        PathExistsAction a => await PathExistsAsync(a, run, token).ConfigureAwait(false),

        OpenPathAction a => await OpenAsync(a, run, token).ConfigureAwait(false),

        // ------------------------------- clipboard -------------------------------
        SetClipboardAction a => await Do(
            () => desktop.Clipboard.WriteAsync(run.Variables.Interpolate(a.Text), token),
            "Clipboard set.").ConfigureAwait(false),

        GetClipboardAction a => await ReadClipboardAsync(a, run, token).ConfigureAwait(false),

        // ------------------------------- prompts -------------------------------
        ShowPickerAction a => await PickAsync(a, run, token).ConfigureAwait(false),

        ShowInputAction a => await AskAsync(a, run, token).ConfigureAwait(false),

        NotifyAction a => await Do(
            () => desktop.Prompts.NotifyAsync(
                run.Variables.Interpolate(a.Message), a.Level ?? NotifyLevel.Info, token),
            "Notified.").ConfigureAwait(false),

        // ------------------------------- control -------------------------------
        WaitAction a => await WaitAsync(a, token).ConfigureAwait(false),

        AbortAction a => (StepOutcome.Aborted,
            run.Variables.Interpolate(a.Reason) is { Length: > 0 } reason
                ? reason
                : "The plan called abort."),

        IfAction a => await BranchAsync(a, run, token).ConfigureAwait(false),

        ForEachAction a => await LoopAsync(a, run, token).ConfigureAwait(false),

        _ => (StepOutcome.Failed,
            $"No handler for {action.GetType().Name}. A primitive was added to the DSL without "
            + "teaching the executor to run it."),
    };

    // ------------------------------- process -------------------------------

    private async Task<(StepOutcome, string)> LaunchAsync(
        LaunchProcessAction action, RunState run, CancellationToken token)
    {
        string executable;

        if (action.App is { } app)
        {
            var resolved = await desktop.Processes.ResolveAsync(app, token).ConfigureAwait(false);
            if (resolved is null)
            {
                return (StepOutcome.Failed,
                    $"\"{app}\" is not installed, or the engine could not find it. The plan "
                    + "names a logical application so this can be reported rather than guessed.");
            }

            executable = resolved;
        }
        else
        {
            // Safety control 2, run-time half: the literal path was checked at validation, but
            // it may interpolate, so the resolved value is what matters.
            executable = run.Variables.Interpolate(action.Path);
            if (!pathGuard.IsAllowed(executable, out var reason))
            {
                return (StepOutcome.Failed, $"Refused to launch: {reason}");
            }
        }

        var argv = action.Argv.Select(run.Variables.Interpolate).ToList();
        var workingDirectory = action.WorkingDirectory is null
            ? null
            : run.Variables.Interpolate(action.WorkingDirectory);

        if (workingDirectory is not null && !pathGuard.IsAllowed(workingDirectory, out var why))
        {
            return (StepOutcome.Failed, $"Refused: working directory {why}");
        }

        await desktop.Processes
            .LaunchAsync(executable, argv, workingDirectory, token)
            .ConfigureAwait(false);

        return (StepOutcome.Succeeded,
            argv.Count == 0 ? $"Launched {executable}." : $"Launched {executable} with {argv.Count} argument(s).");
    }

    private async Task<(StepOutcome, string)> TerminateAsync(
        TerminateProcessAction action, CancellationToken token)
    {
        // Safety control 5. Asked once per action, not once per run, because the user is
        // approving this specific kill rather than the idea of killing things.
        var approved = await desktop.Prompts.ConfirmAsync(
            $"Close {action.ProcessName}{(action.Force == true ? " without saving" : "")}?",
            token).ConfigureAwait(false);

        if (!approved)
        {
            return (StepOutcome.Failed, "The user declined to close the process.");
        }

        await desktop.Processes
            .TerminateAsync(action.ProcessName, action.Force == true, token)
            .ConfigureAwait(false);

        return (StepOutcome.Succeeded, $"Terminated {action.ProcessName}.");
    }

    // ------------------------------- window -------------------------------

    private async Task<(StepOutcome, string)> OnWindowAsync(
        WindowSelector selector,
        RunState run,
        Func<WindowRef, CancellationToken, ValueTask> operation,
        string verb,
        CancellationToken token)
    {
        var window = await FindWindowAsync(selector, run, token).ConfigureAwait(false);

        if (window is not { } target)
        {
            return (StepOutcome.Failed,
                $"No window matched {Core.PlanRenderer.Describe(selector)}.");
        }

        await operation(target, token).ConfigureAwait(false);
        return (StepOutcome.Succeeded, $"{verb} \"{target.Title}\".");
    }

    // ------------------------------- input -------------------------------

    private async Task<(StepOutcome, string)> SendKeysAsync(
        SendKeysAction action, CancellationToken token)
    {
        if (await BlockedAsync(token).ConfigureAwait(false) is { } blocked)
        {
            return blocked;
        }

        await desktop.Input
            .SendChordAsync(action.Keys, action.Repeat ?? 1, token)
            .ConfigureAwait(false);

        return (StepOutcome.Succeeded, $"Sent {string.Join("+", action.Keys)}.");
    }

    private async Task<(StepOutcome, string)> TypeTextAsync(
        TypeTextAction action, RunState run, CancellationToken token)
    {
        if (await BlockedAsync(token).ConfigureAwait(false) is { } blocked)
        {
            return blocked;
        }

        await desktop.Input
            .TypeTextAsync(run.Variables.Interpolate(action.Text), token)
            .ConfigureAwait(false);

        // Deliberately not logging the text. Safety control 6 — a plan may legitimately type
        // something the user would not want in a log they paste into a repair prompt.
        return (StepOutcome.Succeeded, "Typed text (contents not logged).");
    }

    /// <summary>
    /// Safety control 3: refuse synthetic input when the foreground window makes it unsafe.
    /// </summary>
    /// <remarks>
    /// The elevated case is the one that would otherwise be invisible: an unelevated process
    /// cannot send input to an elevated window, and Windows reports no error — the keystrokes
    /// simply go nowhere. Reporting it as a failure is the only way the user learns why an
    /// automation "worked" but nothing happened.
    /// </remarks>
    private async Task<(StepOutcome, string)?> BlockedAsync(CancellationToken token)
    {
        var hazard = await desktop.Input.CheckHazardAsync(token).ConfigureAwait(false);

        return hazard switch
        {
            InputHazard.None => null,

            InputHazard.ConsentPrompt => (StepOutcome.Failed,
                "Refused to send input: a Windows security prompt has focus."),

            InputHazard.CredentialPrompt => (StepOutcome.Failed,
                "Refused to send input: a password or credential field has focus."),

            InputHazard.ElevatedWindow => (StepOutcome.Failed,
                "The focused window runs elevated, so synthetic input cannot reach it. Windows "
                + "discards it silently, so this is reported as a failure rather than a "
                + "success that did nothing."),

            _ => (StepOutcome.Failed, $"Refused to send input: {hazard}."),
        };
    }

    // ------------------------------- files -------------------------------

    private async Task<(StepOutcome, string)> ListAsync(
        string path,
        string into,
        RunState run,
        Func<string, ValueTask<IReadOnlyList<string>>> list,
        CancellationToken token)
    {
        if (!pathGuard.IsAllowed(path, out var reason))
        {
            return (StepOutcome.Failed, $"Refused to read: {reason}");
        }

        var found = await list(path).ConfigureAwait(false);
        run.Variables.SetList(into, found);

        return (StepOutcome.Succeeded, $"Found {found.Count} item(s) in {path}.");
    }

    private async Task<(StepOutcome, string)> PathExistsAsync(
        PathExistsAction action, RunState run, CancellationToken token)
    {
        var path = run.Variables.Interpolate(action.Path);

        if (!pathGuard.IsAllowed(path, out var reason))
        {
            return (StepOutcome.Failed, $"Refused to check: {reason}");
        }

        var exists = await desktop.Files.ExistsAsync(path, token).ConfigureAwait(false);
        run.Variables.SetBoolean(action.Into, exists);

        return (StepOutcome.Succeeded, $"{path} {(exists ? "exists" : "does not exist")}.");
    }

    private async Task<(StepOutcome, string)> OpenAsync(
        OpenPathAction action, RunState run, CancellationToken token)
    {
        var path = run.Variables.Interpolate(action.Path);

        if (!pathGuard.IsAllowed(path, out var reason))
        {
            return (StepOutcome.Failed, $"Refused to open: {reason}");
        }

        await desktop.Files.OpenAsync(path, token).ConfigureAwait(false);
        return (StepOutcome.Succeeded, $"Opened {path}.");
    }

    // ------------------------------- clipboard & prompts -------------------------------

    private async Task<(StepOutcome, string)> ReadClipboardAsync(
        GetClipboardAction action, RunState run, CancellationToken token)
    {
        var text = await desktop.Clipboard.ReadAsync(token).ConfigureAwait(false);
        run.Variables.SetText(action.Into, text);

        // Contents redacted — safety control 6.
        return (StepOutcome.Succeeded, $"Read {text.Length} character(s) from the clipboard.");
    }

    private async Task<(StepOutcome, string)> PickAsync(
        ShowPickerAction action, RunState run, CancellationToken token)
    {
        var items = run.Variables.GetList(action.Source);

        if (items.Count == 0)
        {
            return (StepOutcome.Failed,
                $"${{{action.Source}}} is empty, so there is nothing to choose from.");
        }

        var chosen = await desktop.Prompts
            .PickAsync(items, run.Variables.Interpolate(action.Prompt), token)
            .ConfigureAwait(false);

        if (chosen is null)
        {
            return (StepOutcome.Failed, "The user cancelled the picker.");
        }

        run.Variables.SetPath(action.Into, chosen);
        return (StepOutcome.Succeeded, $"Selected \"{chosen}\".");
    }

    private async Task<(StepOutcome, string)> AskAsync(
        ShowInputAction action, RunState run, CancellationToken token)
    {
        var answer = await desktop.Prompts.AskAsync(
            run.Variables.Interpolate(action.Prompt),
            run.Variables.Interpolate(action.DefaultValue),
            token).ConfigureAwait(false);

        if (answer is null)
        {
            return (StepOutcome.Failed, "The user cancelled the prompt.");
        }

        run.Variables.SetText(action.Into, answer);
        return (StepOutcome.Succeeded, "Captured the user's input.");
    }

    // ------------------------------- control -------------------------------

    private async Task<(StepOutcome, string)> WaitAsync(
        WaitAction action, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(action.DurationMs), clock, token)
            .ConfigureAwait(false);

        return (StepOutcome.Succeeded, $"Waited {action.DurationMs} ms.");
    }

    private async Task<(StepOutcome, string)> BranchAsync(
        IfAction action, RunState run, CancellationToken token)
    {
        var holds = await EvaluateAsync(action.Condition, run, token).ConfigureAwait(false);
        var branch = holds ? action.Then : action.Else;

        await ExecuteAsync(branch, run, token).ConfigureAwait(false);

        return (StepOutcome.Succeeded,
            holds ? "Condition held; ran the 'then' branch." : "Condition did not hold.");
    }

    private async Task<(StepOutcome, string)> LoopAsync(
        ForEachAction action, RunState run, CancellationToken token)
    {
        var items = run.Variables.GetList(action.Source);
        var cap = Math.Min(items.Count, action.MaxIterations ?? 25);

        for (var i = 0; i < cap; i++)
        {
            if (run.StoppedBecause is not null)
            {
                break;
            }

            run.Variables.SetPath(action.ItemVariable, items[i]);
            await ExecuteAsync(action.Body, run, token).ConfigureAwait(false);
        }

        // Scoped to the loop: reading it afterwards is a mistake the policy layer already
        // rejects, and leaving it set would hide that at run time.
        run.Variables.Clear(action.ItemVariable);

        var skipped = items.Count - cap;
        return (StepOutcome.Succeeded,
            skipped > 0
                ? $"Ran {cap} iteration(s); {skipped} item(s) skipped by maxIterations."
                : $"Ran {cap} iteration(s).");
    }

    private async Task<bool> EvaluateAsync(
        Condition condition, RunState run, CancellationToken token) => condition switch
    {
        AllOfCondition c => await AllAsync(c.Conditions, run, token).ConfigureAwait(false),
        AnyOfCondition c => await AnyAsync(c.Conditions, run, token).ConfigureAwait(false),
        SimplePredicate p => await SimpleAsync(p, run, token).ConfigureAwait(false),
        _ => false,
    };

    private async Task<bool> AllAsync(
        IReadOnlyList<SimplePredicate> predicates, RunState run, CancellationToken token)
    {
        foreach (var predicate in predicates)
        {
            if (!await SimpleAsync(predicate, run, token).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> AnyAsync(
        IReadOnlyList<SimplePredicate> predicates, RunState run, CancellationToken token)
    {
        foreach (var predicate in predicates)
        {
            if (await SimpleAsync(predicate, run, token).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> SimpleAsync(
        SimplePredicate predicate, RunState run, CancellationToken token)
    {
        var holds = predicate switch
        {
            ProcessRunningPredicate p =>
                await desktop.Processes.IsRunningAsync(p.ProcessName, token).ConfigureAwait(false),

            WindowExistsPredicate p =>
                await FindWindowAsync(p.Selector, run, token).ConfigureAwait(false) is not null,

            PathExistsPredicate p =>
                await ExistsWithinRootsAsync(run.Variables.Interpolate(p.Path), token)
                    .ConfigureAwait(false),

            VariableEqualsPredicate p => string.Equals(
                run.Variables.GetText(p.Variable),
                run.Variables.Interpolate(p.Value),
                StringComparison.Ordinal),

            VariableEmptyPredicate p => IsEmpty(run, p.Variable),

            _ => false,
        };

        return predicate.Negate == true ? !holds : holds;
    }

    private static bool IsEmpty(RunState run, string name)
    {
        if (!run.Variables.IsSet(name))
        {
            return true;
        }

        return run.Variables.TypeOf(name) is VariableType.PathList or VariableType.TextList
            ? run.Variables.GetList(name).Count == 0
            : string.IsNullOrEmpty(run.Variables.GetText(name));
    }

    // ------------------------------- helpers -------------------------------

    private static async Task<(StepOutcome, string)> Do(
        Func<ValueTask> operation, string detail)
    {
        await operation().ConfigureAwait(false);
        return (StepOutcome.Succeeded, detail);
    }

    /// <summary>Poll a condition until it holds or the action's timeout expires.</summary>
    private async Task<(StepOutcome, string)> PollAsync(
        Func<ValueTask<bool>> condition,
        string failureDetail,
        int? timeoutMs,
        CancellationToken token)
    {
        var window = timeoutMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : limits.DefaultActionTimeout;

        var deadline = clock.GetUtcNow() + window;

        while (true)
        {
            if (await condition().ConfigureAwait(false))
            {
                return (StepOutcome.Succeeded, "Condition met.");
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return (StepOutcome.Failed,
                    $"Waited {window.TotalMilliseconds:F0} ms but {failureDetail}.");
            }

            await Task.Delay(limits.PollInterval, clock, token).ConfigureAwait(false);
        }
    }
}
