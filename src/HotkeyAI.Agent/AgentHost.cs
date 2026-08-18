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
    /// <remarks>
    /// From Core, so the chord the agent registers and the chord the validator refuses as a trigger
    /// are the same list rather than two that happen to match today.
    /// </remarks>
    private static IReadOnlyList<KeyName> PanicChord => HotkeyChord.Panic;

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

        // A tray app that dies leaves nothing behind but an event-log entry naming an exception
        // type, which is barely a clue. Whatever kills this process should say so in the log the
        // tray menu opens, next to the automation that was running when it happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AgentLog.Line($"FATAL: {e.ExceptionObject}");
        };

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
        UiThread.Report = AgentLog.Line;

        // Root first, and with its DACL, before anything creates a subfolder under it and inherits
        // whatever %LOCALAPPDATA% happens to grant. PLAN.md control 4 specifies a per-user ACL, and
        // nothing used to set or check one.
        StoreAcl.EnsureRoot();
        Directory.CreateDirectory(AgentPaths.Automations);

        ReportStoreAcl();

        // Logs used to hold window titles and file paths for as long as the machine lasted, and
        // two weeks covers every reason anyone opens one.
        if (LogRetention.Prune() is > 0 and var removed)
        {
            AgentLog.Line(
                $"[logs] removed {removed} log file(s) past the "
                + $"{LogRetention.Window.TotalDays:F0}-day retention.");
        }

        // Both this and the CLI open the store through one factory. Assembling it in two places
        // is how the CLI ended up blind to two of the four storages.
        var policy = AgentStore.Policy;
        var store = AgentStore.Open();
        var versions = AgentStore.Versions();
        var history = new RegistrationHistory(AgentPaths.HotkeyHistory);
        var loaded = store.Load(AgentPaths.Automations);
        Capture(versions, loaded);

        AgentLog.Line($"Hotkey AI — automations in {AgentPaths.Automations}");
        AgentLog.Line();


        using var host = new HotkeyHost();

        // The panic key goes first, before any automation gets a chance at a chord.
        // RegisterHotKey is first-come-first-served, so registering automations first meant one
        // of them could take Ctrl+Alt+Shift+Esc and the abort key would simply fail to bind.
        RegisterPanic(host);

        var runnable = new Dictionary<string, Automation>(StringComparer.Ordinal);
        var registrations = Register(host, loaded, runnable, history);

        Report(loaded, registrations, history);
        history.Save();

        var desktop = new WindowsDesktop(new WpfPrompts());
        var executor = new PlanExecutor(desktop, new PathGuard(policy.AllowedRoots, new WindowsRealPath()));
        // RunContinuationsAsynchronously matters here. Quit is signalled from the tray menu's
        // click handler, which runs on the UI thread; without this the whole shutdown sequence —
        // unregistering hotkeys, joining the pump, disposing the tray — would run inline on that
        // thread, with the menu still on the stack, tearing down the UI from inside itself.
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The last run of each automation, kept so the dashboard can build a repair prompt from
        // real evidence rather than from what the user remembers. In memory only: a run from
        // before the agent started is in the log file, and parsing a transcript back out of a
        // text log to feed a repair prompt would be building on a guess.
        var lastRuns = new System.Collections.Concurrent.ConcurrentDictionary<string, RunRecord>(
            StringComparer.OrdinalIgnoreCase);

        // Every execution goes through here, whether a key started it or the dashboard did.
        var runner = new AutomationRunner(executor, lastRuns);

        host.Pressed += name =>
        {
            if (string.Equals(name, "__panic", StringComparison.Ordinal))
            {
                runner.Panic();
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

            _ = Task.Run(() => runner.RunAsync(name, plan, "triggered"));
        };

        var live = registrations.Values.Count(r => r.Registered);

        // What the folder looked like at the last rebind. Compared on every watcher event so the
        // agent does not react to its own writes: rebinding a hotkey and saving a pasted plan both
        // rewrite files here, and each would otherwise bounce straight back as an external change.
        var fingerprint = Fingerprint(store);

        void Rebind()
        {
            Reload(store, host, runnable, history, versions);
            fingerprint = Fingerprint(store);
        }

        // Releases every chord, and puts them all back when the caller is done with it.
        IDisposable Suspend()
        {
            host.UnregisterAll();
            return new Restore(Rebind);
        }

        var dashboard = new DashboardHost(
            store, policy, Rebind, host.Probe, Suspend, lastRuns, versions, runner);

        using var tray = await TrayIcon.ShowAsync(
            Tooltip(loaded.Count, live),
            () => Menu(store, runnable, dashboard, Rebind, runner, quit),
            () => DashboardWindow.Open(dashboard),
            AgentLog.Line).ConfigureAwait(false);

        using var watcher = new AutomationWatcher(AgentPaths.Automations, () =>
        {
            var current = Fingerprint(store);

            if (string.Equals(current, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            AgentLog.Line();
            AgentLog.Line("The automations folder changed.");
            Rebind();

            // Said out loud, because the whole point is to shorten the gap between saving a plan
            // and being asked about it. Anything new stays inert until it is approved, so this
            // notification is the only thing standing between a dropped file and being forgotten.
            var waiting = store.Load(AgentPaths.Automations)
                .Count(a => a.Status != ApprovalStatus.Approved && a.Validation.IsValid);

            if (waiting > 0)
            {
                tray.Notify(
                    "Hotkey AI",
                    waiting == 1
                        ? "An automation is waiting for you to review it."
                        : $"{waiting} automations are waiting for you to review them.");
            }
        });

        AgentLog.Line();
        AgentLog.Line($"Watching {AgentPaths.Automations} for changes.");
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
        AutomationRunner runner,
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

            // A mouse-reachable abort. The panic key is the fast path, but it can fail to
            // register — another application may already hold the chord — and until now that left
            // no way at all to stop a running automation.
            // Shown only while something is running, so the menu does not offer an action that
            // would do nothing.
            .. runner.IsBusy
                ? new[] { new TrayCommand("Stop running automation", runner.Panic, Glyph: "") }
                : [],
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
        RegistrationHistory history,
        IVersionStore versions)
    {
        AgentLog.Line();
        AgentLog.Line("Reloading automations.");

        host.UnregisterAll();

        lock (runnable)
        {
            runnable.Clear();
        }

        var loaded = store.Load(AgentPaths.Automations);
        Capture(versions, loaded);

        var registrations = Register(host, loaded, runnable, history);

        Report(loaded, registrations, history);
        history.Save();

        // The panic key went with UnregisterAll, so it has to come back — and the result is
        // reported, not discarded. Startup said so loudly while every folder change, dashboard
        // rebind and suspend-restore came through here in silence, so the abort key could go
        // missing at any point after launch with nothing anywhere to say it had.
        RegisterPanic(host);
    }

    /// <summary>
    /// Bind the panic key and say what happened.
    /// </summary>
    /// <remarks>
    /// Always through here, so the outcome is reported on every path rather than only at startup.
    /// An agent running with no keyboard abort is a materially different product from one that has
    /// it, and the user is entitled to know which they have.
    /// </remarks>
    /// <summary>
    /// Say, in the log, whether the store's per-user ACL is actually in force.
    /// </summary>
    /// <remarks>
    /// The "assert" half of PLAN.md control 4. Logged rather than enforced: rewriting the ACL of a
    /// store that already exists means overruling whatever the user or their IT department
    /// configured, and getting that wrong locks someone out of their own
    /// automations. Reported every start, so a control that stops holding is visible.
    /// </remarks>
    private static void ReportStoreAcl()
    {
        switch (StoreAcl.Audit())
        {
            case null:
                AgentLog.Line("[store] could not read the folder permissions to check them.");
                break;

            case { Count: 0 }:
                AgentLog.Line("[store] permissions are per-user, as control 4 requires.");
                break;

            case { } unexpected:
                AgentLog.Line(
                    $"[store] {AgentPaths.Root} grants access to "
                    + string.Join(", ", unexpected)
                    + ". Automations here run on a keypress, so anyone who can write to this folder "
                    + "can change what they do.");
                break;
        }
    }

    private static void RegisterPanic(HotkeyHost host)
    {
        var result = host.Register("__panic", PanicChord);

        AgentLog.Line(result.Registered
            ? "Panic key   Ctrl+Alt+Shift+Esc — stops a running automation."
            : $"Panic key   UNAVAILABLE ({result.Describe()}). Automations will run with no way "
              + "to stop them from the keyboard; use Stop in the tray menu instead.");
    }

    private static void ToggleAutostart(bool currentlyEnabled)
    {
        var error = currentlyEnabled ? Autostart.Disable() : Autostart.Enable();

        AgentLog.Line(error is null
            ? currentlyEnabled ? "Autostart removed." : "Autostart installed."
            : $"Autostart change failed: {error}");
    }

    /// <summary>
    /// Snapshot every plan that has changed since its last snapshot.
    /// </summary>
    /// <remarks>
    /// Done on load rather than on save, so a plan edited in a text editor is versioned exactly
    /// like one changed through the dashboard. The store itself skips content it already holds,
    /// so calling this on every reload is cheap and produces no duplicates.
    /// </remarks>
    private static void Capture(IVersionStore versions, IReadOnlyList<StoredAutomation> loaded)
    {
        foreach (var automation in loaded)
        {
            try
            {
                versions.Capture(automation.FileName, File.ReadAllText(automation.Path));
            }
            catch (IOException)
            {
                // A file that cannot be read right now will be captured on the next reload.
            }
        }
    }

    /// <summary>
    /// A cheap description of the folder's contents, for spotting real changes.
    /// </summary>
    /// <remarks>
    /// Names and content hashes rather than timestamps. A file rewritten with identical content —
    /// which is what saving in an editor without changing anything produces — is not a change
    /// worth unregistering every hotkey for.
    /// </remarks>
    private static string Fingerprint(AutomationStore store) =>
        string.Join(
            "|",
            store.Load(AgentPaths.Automations).Select(a => $"{a.FileName}:{a.ContentHash}"));

    private static string Tooltip(int total, int live) =>
        live == total
            ? $"Hotkey AI — {live} automations live"
            : $"Hotkey AI — {live} of {total} live, {total - live} not running";

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


    /// <summary>Runs an action when disposed. For scoping a suspension to a using block.</summary>
    private sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
