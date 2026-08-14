using System.Diagnostics;
using System.IO;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Store;
using HotkeyAI.Ui;
using HotkeyAI.Windows;

namespace HotkeyAI.Agent;

/// <summary>
/// The resident process: owns the hotkeys, the store, the executor and the tray icon.
/// </summary>
/// <remarks>
/// Windowed rather than console-hosted. A console window is a thing users close, and closing it
/// kills every hotkey with nothing left on screen to say so — the failure would look exactly like
/// the automations having quietly stopped working. A tray icon is the honest version of "this is
/// running": visible, and quit on purpose rather than by accident.
/// <para>
/// Nothing console-shaped lives here. Listing automations, approving them and turning autostart
/// on are CLI verbs, because a GUI-subsystem process cannot do them properly: the shell does not
/// wait for it, so output lands after the next prompt, and an interactive approval would be
/// reading the same stdin the shell is.
/// </para>
/// </remarks>
public static class AgentHost
{
    /// <summary>
    /// The panic key. Registered before anything else, and never bound to an automation.
    /// </summary>
    /// <remarks>
    /// Safety control 1. If this cannot be registered the agent still starts, but it says so
    /// loudly — running automations with no way to stop them is a materially worse product, and
    /// the user deserves to know which one they have.
    /// </remarks>
    private static readonly KeyName[] PanicChord =
        [KeyName.Ctrl, KeyName.Alt, KeyName.Shift, KeyName.Esc];

    /// <summary>
    /// Guards against a second agent running.
    /// </summary>
    /// <remarks>
    /// Found by running the agent twice during development, and worth a named mutex rather than
    /// a comment. Hotkey registration is per-process and first-come-first-served, so a second
    /// instance registers nothing at all — and then honestly reports every automation as
    /// "unavailable, another application holds this combination". Which is true, and which is
    /// itself. A user who double-clicks twice would be told their automations are broken by the
    /// very process breaking them, with no hint that the first copy is fine.
    /// </remarks>
    private const string SingleInstanceMutex = @"Local\HotkeyAI.Agent.SingleInstance";

    /// <summary>
    /// Entry point. Synchronous, and deliberately so.
    /// </summary>
    /// <remarks>
    /// A <see cref="Mutex"/> has thread affinity: only the thread that acquired it may release it,
    /// and doing otherwise throws. An <c>async</c> version of this method acquired the mutex on
    /// the main thread and released it on whichever thread happened to resume the continuation
    /// after the quit signal — so choosing Quit terminated the agent by crashing it. It looked
    /// like it worked, because the process did go away; the giveaway was that it took twelve
    /// seconds, which was Windows Error Reporting. Blocking the main thread here keeps acquisition
    /// and release on one thread, which is the only way this is correct.
    /// </remarks>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var singleInstance = new Mutex(initiallyOwned: false, SingleInstanceMutex);
        var owned = false;

        try
        {
            owned = singleInstance.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous agent was killed rather than closed. The mutex is ours now.
            owned = true;
        }

        if (!owned)
        {
            AgentLog.Line(
                "Hotkey AI is already running. That copy owns the hotkeys; this one would "
                + "register none of them and report every automation as unavailable. "
                + "Run `hotkeyai list` to inspect state, or quit the running agent first.");

            return 3;
        }

        try
        {
            return StartAsync().GetAwaiter().GetResult();
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }

    private static async Task<int> StartAsync()
    {
        Directory.CreateDirectory(AgentPaths.Automations);

        var policy = PolicyOptions.Default with
        {
            AllowedRoots = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
        };

        var store = new AutomationStore(
            new DpapiApprovalStorage(AgentPaths.Approvals),
            policy,
            new JsonDisabledStorage(AgentPaths.Disabled));
        var history = new RegistrationHistory(AgentPaths.HotkeyHistory);
        var loaded = store.Load(AgentPaths.Automations);

        AgentLog.Line($"Hotkey AI — automations in {AgentPaths.Automations}");
        AgentLog.Line();


        using var host = new HotkeyHost();
        using var panic = new CancellationTokenSource();

        var runnable = new Dictionary<string, Automation>(StringComparer.Ordinal);
        var registrations = Register(host, loaded, runnable, history);

        Report(loaded, registrations, history);
        history.Save();

        var panicResult = host.Register("__panic", PanicChord);
        AgentLog.Line(panicResult.Registered
            ? "Panic key   Ctrl+Alt+Shift+Esc — stops a running automation."
            : $"Panic key   UNAVAILABLE ({panicResult.Describe()}). Automations will run with no "
              + "way to stop them from the keyboard; quit from the tray instead.");

        var desktop = new WindowsDesktop(new WpfPrompts());
        var executor = new PlanExecutor(desktop, new PathGuard(policy.AllowedRoots));
        // RunContinuationsAsynchronously matters here. Quit is signalled from the tray menu's
        // click handler, which runs on the UI thread; without this the whole shutdown sequence —
        // unregistering hotkeys, joining the pump, disposing the tray — would run inline on that
        // thread, with the menu still on the stack, tearing down the UI from inside itself.
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;

        host.Pressed += name =>
        {
            if (string.Equals(name, "__panic", StringComparison.Ordinal))
            {
                AgentLog.Line("[panic] stopping the running automation.");
                panic.Cancel();
                return;
            }

            Automation? plan;

            lock (runnable)
            {
                if (!runnable.TryGetValue(name, out plan))
                {
                    return;
                }
            }

            // One at a time. Two automations racing for the foreground window would produce
            // results neither plan describes, and the logs would interleave into nonsense.
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                AgentLog.Line($"[{name}] ignored — another automation is still running.");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteAsync(executor, name, plan, panic).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref running, 0);
                }
            });
        };

        var live = registrations.Values.Count(r => r.Registered);

        void Rebind() => Reload(store, host, runnable, history);

        var dashboard = new DashboardHost(store, policy, Rebind);

        using var tray = await TrayIcon.ShowAsync(
            Tooltip(loaded.Count, live),
            () => Menu(store, runnable, dashboard, Rebind, quit),
            () => DashboardWindow.Open(dashboard),
            AgentLog.Line).ConfigureAwait(false);

        AgentLog.Line();
        AgentLog.Line($"Running in the tray. Log: {AgentLog.Path}");

        // The one moment this process is allowed to interrupt: it has just started, has no window,
        // and the user needs to know it is alive and how much of it is working.
        tray.Notify(
            "Hotkey AI is running",
            live == loaded.Count
                ? $"{live} automations are live."
                : $"{live} of {loaded.Count} automations are live. Open the log for details.",
            isError: live < loaded.Count);

        await quit.Task.ConfigureAwait(false);
        AgentLog.Line("Stopping.");
        return 0;
    }

    // ---------------------------------------------------------------------------------

    private static IReadOnlyList<TrayCommand> Menu(
        AutomationStore store,
        Dictionary<string, Automation> runnable,
        DashboardHost dashboard,
        Action rebind,
        TaskCompletionSource quit)
    {
        // Counted at open time rather than captured at startup, because Reload changes it. A tray
        // menu that reports yesterday's state is worse than one that reports none.
        var loaded = store.Load(AgentPaths.Automations);
        int live;

        lock (runnable)
        {
            live = runnable.Count;
        }

        var autostart = Autostart.IsEnabled();

        return
        [
            new TrayCommand($"{live} of {loaded.Count} automations live"),
            TrayCommand.Separator,
            new TrayCommand("Dashboard", () => DashboardWindow.Open(dashboard), Glyph: ""),
            new TrayCommand(
                "Automations folder", () => Shell.Open(AgentPaths.Automations), Glyph: ""),
            new TrayCommand("View log", () => Shell.Open(AgentLog.Path), Glyph: ""),
            new TrayCommand("Reload automations", rebind, Glyph: ""),
            TrayCommand.Separator,
            new TrayCommand(
                "Start at login",
                () => ToggleAutostart(autostart),
                Checked: autostart,
                Glyph: ""),
            TrayCommand.Separator,
            new TrayCommand("Quit Hotkey AI", () => quit.TrySetResult(), Glyph: ""),
        ];
    }

    /// <summary>
    /// Re-read the automations folder and rebind every hotkey.
    /// </summary>
    /// <remarks>
    /// Everything is unregistered and registered again rather than diffed. Approval state, plan
    /// contents and chords can all have changed since the last load, and a partial update that got
    /// one of them wrong would leave a chord bound to a plan the user has since edited — which is
    /// exactly what the trust-on-first-use gate exists to prevent.
    /// </remarks>
    private static void Reload(
        AutomationStore store,
        HotkeyHost host,
        Dictionary<string, Automation> runnable,
        RegistrationHistory history)
    {
        AgentLog.Line();
        AgentLog.Line("Reloading automations.");

        host.UnregisterAll();

        lock (runnable)
        {
            runnable.Clear();
        }

        var loaded = store.Load(AgentPaths.Automations);
        var registrations = Register(host, loaded, runnable, history);

        Report(loaded, registrations, history);
        history.Save();

        // The panic key went with UnregisterAll, so it has to come back.
        host.Register("__panic", PanicChord);
    }

    private static void ToggleAutostart(bool currentlyEnabled)
    {
        var error = currentlyEnabled ? Autostart.Disable() : Autostart.Enable();

        AgentLog.Line(error is null
            ? currentlyEnabled ? "Autostart removed." : "Autostart installed."
            : $"Autostart change failed: {error}");
    }

    private static string Tooltip(int total, int live) =>
        live == total
            ? $"Hotkey AI — {live} automations live"
            : $"Hotkey AI — {live} of {total} live, {total - live} not running";

    private static async Task ExecuteAsync(
        PlanExecutor executor,
        string name,
        Automation plan,
        CancellationTokenSource panic)
    {
        AgentLog.Line();
        AgentLog.Line($"[{name}] triggered");

        // A fresh token per run: the panic key must stop the automation that is running, not
        // permanently disable every future one.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(panic.Token);

        try
        {
            var result = await executor.RunAsync(plan, run.Token).ConfigureAwait(false);
            AgentLog.Raw(result.ToTranscript());
        }
#pragma warning disable CA1031 // A failing automation must never take the agent down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AgentLog.Line($"[{name}] the engine failed unexpectedly: {ex.Message}");
        }

        if (panic.IsCancellationRequested)
        {
            AgentLog.Line("[panic] cleared; hotkeys are live again.");
        }
    }

    private static Dictionary<string, RegistrationResult> Register(
        HotkeyHost host,
        IReadOnlyList<StoredAutomation> loaded,
        Dictionary<string, Automation> runnable,
        RegistrationHistory history)
    {
        var registrations = new Dictionary<string, RegistrationResult>(StringComparer.Ordinal);

        foreach (var automation in loaded)
        {
            // Only approved, valid plans get a hotkey. An inert automation must not hold a
            // combination hostage — the user would see the key do nothing and have no idea why.
            if (!automation.IsRunnable)
            {
                continue;
            }

            var result = host.Register(automation.FileName, automation.Plan!.Trigger.Keys);
            registrations[automation.FileName] = result;

            if (result.Registered)
            {
                lock (runnable)
                {
                    runnable[automation.FileName] = automation.Plan;
                }

                history.RecordSuccess(
                    automation.FileName, PlanRenderer.DescribeTrigger(automation.Plan.Trigger));
            }
        }

        return registrations;
    }

    /// <summary>
    /// Print the state of every automation, including the ones that are not running.
    /// </summary>
    /// <remarks>
    /// The Phase 0 spike's first requirement. An automation whose hotkey did not register is not
    /// enabled, and an agent that looks healthy while three of its hotkeys are dead is worse
    /// than one that refuses to start — the user presses the key, nothing happens, and there is
    /// nothing anywhere to explain it.
    /// </remarks>
    private static void Report(
        IReadOnlyList<StoredAutomation> loaded,
        Dictionary<string, RegistrationResult> registrations,
        RegistrationHistory history)
    {
        foreach (var automation in loaded)
        {
            var chord = automation.Plan is { } plan
                ? PlanRenderer.DescribeTrigger(plan.Trigger)
                : "—";

            string state;

            if (automation.Blocker is { } blocker)
            {
                state = blocker;
            }
            else if (registrations.TryGetValue(automation.FileName, out var registration))
            {
                state = registration.Describe();

                // The API cannot name the holder, but history can say whether this is new.
                if (!registration.Registered
                    && history.Explain(automation.FileName, chord) is { } sinceWhen)
                {
                    state += $" — {sinceWhen}";
                }
            }
            else
            {
                state = "ready";
            }

            var marker = automation.IsRunnable
                && (!registrations.TryGetValue(automation.FileName, out var r) || r.Registered)
                ? "  ok  "
                : "  --  ";

            AgentLog.Line($"{marker}{automation.FileName,-30} {chord,-22} {state}");

            if (automation.Blocker is not null && !automation.Validation.IsValid)
            {
                foreach (var error in automation.Validation.Errors.Take(3))
                {
                    AgentLog.Line($"        {error}");
                }
            }
        }
    }

}
