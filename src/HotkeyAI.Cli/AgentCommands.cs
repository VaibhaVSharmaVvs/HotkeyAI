using HotkeyAI.Core;
using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Store;
using HotkeyAI.Windows;

namespace HotkeyAI.Cli;

/// <summary>
/// The console-shaped half of the agent: inspecting state, approving plans, autostart.
/// </summary>
/// <remarks>
/// These live in the CLI rather than in the agent, and the reason is not tidiness. The agent is a
/// windowed process so that it can sit in the tray without a console window somebody will close;
/// a windowed process cannot do console work properly. The shell does not wait for it, so its
/// output arrives after the next prompt, and an interactive approval would be reading the same
/// stdin the shell is reading. Both were observed before this moved.
/// <para>
/// The agent still owns this data at runtime. These commands read and write the same files, which
/// is why the paths live in <see cref="AgentPaths"/> rather than being spelled twice.
/// </para>
/// </remarks>
internal static class AgentCommands
{
    /// <summary>Show every automation, its chord and whether it can run.</summary>
    /// <remarks>
    /// Deliberately reports approval and validity, not live registration: this is a separate
    /// process and cannot know which chords the running agent actually holds. The tray menu and
    /// the log are where that lives, and claiming otherwise here would be a guess.
    /// </remarks>
    public static int List()
    {
        var loaded = Load();

        if (loaded.Count == 0)
        {
            Console.WriteLine($"No automations in {AgentPaths.Automations}");
            return Cli.ExitCode.Ok;
        }

        Console.WriteLine($"Automations in {AgentPaths.Automations}");
        Console.WriteLine();

        foreach (var automation in loaded)
        {
            var chord = automation.Plan is { } plan
                ? PlanRenderer.DescribeTrigger(plan.Trigger)
                : "—";

            var state = automation.Blocker ?? "approved";
            var marker = automation.IsRunnable ? "  ok  " : "  --  ";

            // The user's verdict, which is a different claim from anything above: those say
            // whether it may run, this says whether it does what they meant.
            var verdict = automation.Health switch
            {
                AutomationHealth.Works => "works",
                AutomationHealth.NotWorking => "NOT WORKING",
                _ => "not tested",
            };

            Console.WriteLine($"{marker}{automation.FileName,-30} {chord,-22} {verdict,-12} {state}");

            if (automation.HealthNote is { Length: > 0 } note)
            {
                Console.WriteLine($"        \"{note}\"");
            }

            if (automation.Blocker is not null && !automation.Validation.IsValid)
            {
                foreach (var error in automation.Validation.Errors.Take(3))
                {
                    Console.WriteLine($"        {error}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "Registration state is per-run: see the tray menu, or the log in "
            + AgentPaths.Logs);

        ReportStoreAcl();

        return Cli.ExitCode.Ok;
    }

    /// <summary>
    /// Say whether the store's per-user ACL is in force, on the surface people already read.
    /// </summary>
    /// <remarks>
    /// PLAN.md control 4's "assert" half — security review 2026-08-17, finding L7. Printed here
    /// because `list` is the command someone runs when asking what the agent is doing with their
    /// machine, and "who can change these automations" is part of that answer. Silent in the ordinary
    /// case: a line on every invocation saying nothing is wrong is a line people stop reading.
    /// </remarks>
    private static void ReportStoreAcl()
    {
        switch (StoreAcl.Audit())
        {
            case null:
                Console.WriteLine("Could not read the store's folder permissions to check them.");
                break;

            case { Count: 0 }:
                break;

            case { } unexpected:
                Console.WriteLine();
                Console.WriteLine(
                    $"WARNING: {AgentPaths.Root} grants access to "
                    + string.Join(", ", unexpected)
                    + ".");
                Console.WriteLine(
                    "Automations there run on a keypress, so anyone who can write to that folder "
                    + "can change what they do.");
                break;
        }
    }

    /// <summary>
    /// Review and approve pending automations, one at a time.
    /// </summary>
    /// <remarks>
    /// The plan is printed in full before the prompt, every time. Approval means "I have read
    /// this and accept what it does"; a yes/no on a filename would be a rubber stamp and would
    /// make safety control 4 theatre rather than a control.
    /// </remarks>
    public static int Approve()
    {
        var store = Store();
        var pending = store.Load(AgentPaths.Automations)
            .Where(a => a.Status != ApprovalStatus.Approved)
            .ToList();

        if (pending.Count == 0)
        {
            Console.WriteLine("Everything is already approved.");
            return Cli.ExitCode.Ok;
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

        Console.WriteLine();
        Console.WriteLine("Reload from the tray menu, or restart the agent, to pick these up.");
        return Cli.ExitCode.Ok;
    }

    /// <summary>Turn start-at-login on or off, or report it.</summary>
    public static int AutostartCommand(string[] args)
    {
        var setting = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        switch (setting)
        {
            case null or "status":
                Console.WriteLine(Autostart.IsEnabled()
                    ? "Autostart is ON — Hotkey AI starts when you sign in."
                    : "Autostart is OFF.");
                return Cli.ExitCode.Ok;

            case "on":
                return Apply(Autostart.Enable(), "Autostart is ON.");

            case "off":
                return Apply(Autostart.Disable(), "Autostart is OFF.");

            default:
                Console.Error.WriteLine("Usage: hotkeyai autostart [on|off|status]");
                return Cli.ExitCode.Usage;
        }

        static int Apply(string? error, string success)
        {
            if (error is not null)
            {
                Console.Error.WriteLine(error);
                return Cli.ExitCode.Invalid;
            }

            Console.WriteLine(success);
            return Cli.ExitCode.Ok;
        }
    }

    private static IReadOnlyList<StoredAutomation> Load() => Store().Load(AgentPaths.Automations);

    private static AutomationStore Store() => AgentStore.Open();
}
