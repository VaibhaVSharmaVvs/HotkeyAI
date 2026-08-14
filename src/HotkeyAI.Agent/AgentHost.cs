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
/// The resident process: owns the hotkeys, the store, and the executor.
/// </summary>
/// <remarks>
/// Console-hosted for now. The tray icon replaces the console window later without changing any
/// of the wiring below, which is the point of keeping the pump, the store and the engine as
/// separate pieces.
/// </remarks>
public static class AgentHost
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotkeyAI");

    private static readonly string AutomationsDirectory = Path.Combine(Root, "automations");
    private static readonly string ApprovalsFile = Path.Combine(Root, "approvals.dat");

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

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // --list and --approve-all only read and write files, so they may run alongside the
        // resident agent; only the hotkey-owning path needs exclusivity.
        var needsHotkeys = !args.Contains("--list", StringComparer.Ordinal)
                           && !args.Contains("--approve-all", StringComparer.Ordinal);

        using var singleInstance = new Mutex(initiallyOwned: false, SingleInstanceMutex);
        var owned = false;

        if (needsHotkeys)
        {
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
                Console.Error.WriteLine(
                    "Hotkey AI is already running. That copy owns the hotkeys; this one would "
                    + "register none of them and report every automation as unavailable. "
                    + "Use --list to inspect state, or quit the running agent first.");

                return 3;
            }
        }

        try
        {
            return await StartAsync(args, needsHotkeys).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
            {
                singleInstance.ReleaseMutex();
            }
        }
    }

    private static async Task<int> StartAsync(string[] args, bool needsHotkeys)
    {
        Directory.CreateDirectory(AutomationsDirectory);

        var policy = PolicyOptions.Default with
        {
            AllowedRoots = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
        };

        var store = new AutomationStore(new DpapiApprovalStorage(ApprovalsFile), policy);
        var loaded = store.Load(AutomationsDirectory);

        Console.WriteLine($"Hotkey AI — automations in {AutomationsDirectory}");
        Console.WriteLine();

        if (loaded.Count == 0)
        {
            Console.WriteLine("No automations found. Drop a validated plan into that folder.");
            return 0;
        }

        if (args.Contains("--approve-all", StringComparer.Ordinal))
        {
            return ApproveAll(store, loaded);
        }

        if (args.Contains("--list", StringComparer.Ordinal))
        {
            Report(loaded, new Dictionary<string, RegistrationResult>(StringComparer.Ordinal));
            return 0;
        }

        using var host = new HotkeyHost();
        using var panic = new CancellationTokenSource();

        var registrations = Register(host, loaded, out var runnable);
        Report(loaded, registrations);

        var panicResult = host.Register("__panic", PanicChord);
        Console.WriteLine(panicResult.Registered
            ? "Panic key   Ctrl+Alt+Shift+Esc — stops a running automation."
            : $"Panic key   UNAVAILABLE ({panicResult.Describe()}). Automations will run with no "
              + "way to stop them from the keyboard; close this window instead.");

        if (runnable.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing is runnable, so no hotkeys are live.");
            return 0;
        }

        // The overlays, not the console prompts. The agent has no console once it runs from the
        // tray, so ConsolePrompts would leave show_picker and show_input reading a stdin that
        // nobody can type into — the automation would hang with nothing on screen to explain it.
        var desktop = new WindowsDesktop(new WpfPrompts());
        var executor = new PlanExecutor(desktop, new PathGuard(policy.AllowedRoots));
        var running = 0;

        host.Pressed += name =>
        {
            if (string.Equals(name, "__panic", StringComparison.Ordinal))
            {
                Console.WriteLine("\n[panic] stopping the running automation.");
                panic.Cancel();
                return;
            }

            if (!runnable.TryGetValue(name, out var plan))
            {
                return;
            }

            // One at a time. Two automations racing for the foreground window would produce
            // results neither plan describes, and the logs would interleave into nonsense.
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                Console.WriteLine($"\n[{name}] ignored — another automation is still running.");
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

        Console.WriteLine();
        Console.WriteLine("Listening. Press Ctrl+C to quit.");

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.TrySetResult();
        };

        await quit.Task.ConfigureAwait(false);
        Console.WriteLine("Stopping.");
        return 0;
    }

    // ---------------------------------------------------------------------------------

    private static async Task ExecuteAsync(
        PlanExecutor executor,
        string name,
        Automation plan,
        CancellationTokenSource panic)
    {
        Console.WriteLine();
        Console.WriteLine($"[{name}] {DateTimeOffset.Now:HH:mm:ss} triggered");

        // A fresh token per run: the panic key must stop the automation that is running, not
        // permanently disable every future one.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(panic.Token);

        try
        {
            var result = await executor.RunAsync(plan, run.Token).ConfigureAwait(false);
            Console.Write(result.ToTranscript());
        }
#pragma warning disable CA1031 // A failing automation must never take the agent down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine($"[{name}] the engine failed unexpectedly: {ex.Message}");
        }

        // Re-arm the panic source if it fired, so the next press of a hotkey still works.
        if (panic.IsCancellationRequested)
        {
            Console.WriteLine("[panic] cleared; hotkeys are live again.");
        }
    }

    private static Dictionary<string, RegistrationResult> Register(
        HotkeyHost host,
        IReadOnlyList<StoredAutomation> loaded,
        out Dictionary<string, Automation> runnable)
    {
        var registrations = new Dictionary<string, RegistrationResult>(StringComparer.Ordinal);
        runnable = new Dictionary<string, Automation>(StringComparer.Ordinal);

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
                runnable[automation.FileName] = automation.Plan;
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
        Dictionary<string, RegistrationResult> registrations)
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
            }
            else
            {
                state = "ready";
            }

            var marker = automation.IsRunnable
                && (!registrations.TryGetValue(automation.FileName, out var r) || r.Registered)
                ? "  ok  "
                : "  --  ";

            Console.WriteLine($"{marker}{automation.FileName,-30} {chord,-22} {state}");

            if (automation.Blocker is not null && !automation.Validation.IsValid)
            {
                foreach (var error in automation.Validation.Errors.Take(3))
                {
                    Console.WriteLine($"        {error}");
                }
            }
        }
    }

    /// <summary>
    /// Approve every valid automation, after showing what each one does.
    /// </summary>
    /// <remarks>
    /// The plan is printed before the prompt, every time. Approval means "I have read this and
    /// I accept what it does" — a yes/no on a filename would be a rubber stamp, and would make
    /// safety control 4 theatre rather than a control.
    /// </remarks>
    private static int ApproveAll(AutomationStore store, IReadOnlyList<StoredAutomation> loaded)
    {
        var pending = loaded.Where(a => a.Status != ApprovalStatus.Approved).ToList();

        if (pending.Count == 0)
        {
            Console.WriteLine("Everything is already approved.");
            return 0;
        }

        foreach (var automation in pending)
        {
            Console.WriteLine(new string('=', 70));

            if (!automation.Validation.IsValid || automation.Plan is null)
            {
                Console.WriteLine($"{automation.FileName}: invalid, so it cannot be approved.");
                foreach (var error in automation.Validation.Errors.Take(5))
                {
                    Console.WriteLine($"  {error}");
                }

                continue;
            }

            var verb = automation.Status == ApprovalStatus.New ? "NEW" : "CHANGED";
            Console.WriteLine($"{verb}: {automation.FileName}");
            Console.WriteLine();
            Console.WriteLine(PlanRenderer.Explain(automation.Plan));

            Console.Write("Approve this automation? [y/N] ");
            var answer = Console.ReadLine();

            if (answer?.Trim().StartsWith('y') == true)
            {
                store.Approve(automation);
                Console.WriteLine("Approved.");
            }
            else
            {
                Console.WriteLine("Left inert.");
            }
        }

        return 0;
    }
}
