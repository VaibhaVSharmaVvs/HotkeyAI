using System.IO;
using System.Text.Json;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;
using HotkeyAI.Ui;
using HotkeyAI.Windows;

namespace HotkeyAI.Cli;

/// <summary>
/// Command-line entry point.
/// </summary>
/// <remarks>
/// <c>validate</c> is the most important verb in V1. Because there is no in-app planner yet,
/// automations are authored externally against the schema — and a machine-readable validator
/// with stable exit codes is what turns that into a self-correcting loop: generate, validate,
/// read the errors, fix, repeat, with no human in the middle of the correction cycle.
/// </remarks>
public static class Cli
{
    /// <summary>Process exit codes. Stable, because scripts and agents branch on them.</summary>
    public static class ExitCode
    {
        /// <summary>Valid, or the command succeeded.</summary>
        public const int Ok = 0;

        /// <summary>The plan is invalid. Errors were reported.</summary>
        public const int Invalid = 1;

        /// <summary>Bad usage, missing file, or unreadable input.</summary>
        public const int Usage = 2;
    }

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        var verb = args[0];
        var rest = args[1..];

        return verb switch
        {
            "validate" => Validate(rest),
            "explain" => Explain(rest),
            "schema" => PrintSchema(),
            "apps" => ListApps(),
            "run" => RunPlanAsync(rest).GetAwaiter().GetResult(),
            "list" => AgentCommands.List(),
            "approve" => AgentCommands.Approve(),
            "autostart" => AgentCommands.AutostartCommand(rest),
            "import" or "logs" => NotYetImplemented(verb),
            _ => Unknown(verb),
        };
    }

    // ----------------------------------------------------------------------------------

    private static int Validate(string[] args)
    {
        var asJson = args.Contains("--json", StringComparer.Ordinal);
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (path is null)
        {
            Error("validate needs a file: hotkeyai validate <file> [--json]");
            return ExitCode.Usage;
        }

        if (!TryRead(path, out var json))
        {
            return ExitCode.Usage;
        }

        var result = PlanValidator.Validate(json, PolicyForThisMachine());

        if (asJson)
        {
            var payload = new
            {
                file = Path.GetFileName(path),
                valid = result.IsValid,
                errors = result.Errors.Select(e => new
                {
                    layer = e.Layer.ToString().ToLowerInvariant(),
                    path = e.Path,
                    message = e.Message,
                }),
            };

            Console.WriteLine(JsonSerializer.Serialize(payload, DslJson.Indented));
            return result.IsValid ? ExitCode.Ok : ExitCode.Invalid;
        }

        if (result.IsValid)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: valid");
            return ExitCode.Ok;
        }

        Console.Error.WriteLine($"{Path.GetFileName(path)}: {result.Errors.Count} problem(s)");
        Console.Error.WriteLine();

        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"  {error}");
        }

        return ExitCode.Invalid;
    }

    private static int Explain(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (path is null)
        {
            Error("explain needs a file: hotkeyai explain <file>");
            return ExitCode.Usage;
        }

        if (!TryRead(path, out var json))
        {
            return ExitCode.Usage;
        }

        // Explaining an invalid plan would print something misleading, so refuse.
        var validation = SchemaValidator.Validate(json);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine(
                $"{Path.GetFileName(path)} is not a valid plan, so it cannot be explained. "
                + "Run `hotkeyai validate` for details.");
            return ExitCode.Invalid;
        }

        Automation? automation;
        try
        {
            automation = JsonSerializer.Deserialize<Automation>(json, DslJson.Options);
        }
        catch (JsonException ex)
        {
            Error($"Could not read {path}: {ex.Message}");
            return ExitCode.Invalid;
        }

        if (automation is null)
        {
            Error($"{path} deserialized to nothing.");
            return ExitCode.Invalid;
        }

        Console.WriteLine(PlanRenderer.Explain(automation));
        return ExitCode.Ok;
    }

    /// <summary>Execute a plan against the real desktop.</summary>
    /// <remarks>
    /// Validates through both layers first and refuses to run an invalid plan. That is not
    /// belt-and-braces: the executor assumes a validated plan and checks meaning rather than
    /// shape, so running an unvalidated one would skip every guarantee the two layers provide.
    /// </remarks>
    private static async Task<int> RunPlanAsync(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (path is null)
        {
            Error("run needs a file: hotkeyai run <file> [--dry-run] [--ui]");
            return ExitCode.Usage;
        }

        if (!TryRead(path, out var json))
        {
            return ExitCode.Usage;
        }

        var policy = PolicyForThisMachine();
        var validation = PlanValidator.Validate(json, policy);

        if (!validation.IsValid)
        {
            Console.Error.WriteLine($"{Path.GetFileName(path)} is not valid, so it will not run:");
            foreach (var error in validation.Errors)
            {
                Console.Error.WriteLine($"  {error}");
            }

            return ExitCode.Invalid;
        }

        var automation = JsonSerializer.Deserialize<Automation>(json, DslJson.Options)!;

        Console.WriteLine(PlanRenderer.Explain(automation));

        if (args.Contains("--dry-run", StringComparer.Ordinal))
        {
            Console.WriteLine("--dry-run: nothing was executed.");
            return ExitCode.Ok;
        }

        Console.WriteLine("Running. Press Ctrl+C to abort.");
        Console.WriteLine(new string('-', 60));

        // Ctrl+C stands in for the panic key until the agent owns a global one. Cancelling
        // rather than killing matters: it lets the engine release any held modifier keys, which
        // is the difference between a stopped automation and an apparently frozen desktop.
        using var panic = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            panic.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            // --ui runs the plan against the same overlays the agent uses, instead of the
            // console prompts. It exists so the picker can be exercised from a terminal: the
            // agent's only route to it is a live hotkey press, which cannot be scripted.
            var prompts = args.Contains("--ui", StringComparer.Ordinal)
                ? new WpfPrompts()
                : (IPrompts)new ConsolePrompts();

            var desktop = new WindowsDesktop(prompts);
            var executor = new PlanExecutor(desktop, new PathGuard(policy.AllowedRoots, new WindowsRealPath()));
            // Ctrl+C here, not the panic key — this is a console run, and saying "panic key" would
            // describe a keypress that is not even registered.
            var result = await executor
                .RunAsync(automation, null, () => "Stopped by Ctrl+C.", panic.Token)
                .ConfigureAwait(false);

            Console.WriteLine(new string('-', 60));
            Console.Write(result.ToTranscript());

            return result.Succeeded ? ExitCode.Ok : ExitCode.Invalid;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Report which logical app names resolve on this machine.</summary>
    /// <remarks>
    /// The schema advertises these names to whoever authors a plan. If one does not resolve
    /// here, a plan using it will fail at run time for a reason the author cannot see, so it is
    /// worth being able to ask.
    /// </remarks>
    private static int ListApps()
    {
        var resolved = new WindowsDesktop().Resolver.ResolveAll();

        foreach (var (name, path) in resolved.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(path is null
                ? $"  {name,-12} not installed"
                : $"  {name,-12} {path}");
        }

        var missing = resolved.Count(a => a.Value is null);
        Console.WriteLine();
        Console.WriteLine($"{resolved.Count - missing} of {resolved.Count} resolve on this machine.");

        return ExitCode.Ok;
    }

    private static int PrintSchema()
    {
        // Handy for piping the contract to an authoring tool.
        Console.WriteLine(DslSchema.Text);
        return ExitCode.Ok;
    }

    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Policy bounds for the machine running the CLI.
    /// </summary>
    /// <remarks>
    /// Allowed roots default to the user's own profile. Once the agent owns settings this
    /// should come from there rather than being inferred here, so that what the CLI accepts
    /// and what the engine will actually run are the same thing.
    /// </remarks>
    private static PolicyOptions PolicyForThisMachine()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return PolicyOptions.Default with
        {
            AllowedRoots = string.IsNullOrEmpty(home) ? [] : [home],
        };
    }

    private static bool TryRead(string path, out string json)
    {
        json = "";

        if (!File.Exists(path))
        {
            Error($"No such file: {path}");
            return false;
        }

        try
        {
            json = File.ReadAllText(path);
            return true;
        }
        catch (IOException ex)
        {
            Error($"Could not read {path}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Error($"Could not read {path}: {ex.Message}");
            return false;
        }
    }

    private static int NotYetImplemented(string verb)
    {
        Error(
            $"`{verb}` needs the agent, which does not exist yet. "
            + "Available now: validate, explain, schema.");

        return ExitCode.Usage;
    }

    private static int Unknown(string verb)
    {
        Error($"Unknown command `{verb}`.");
        PrintUsage(Console.Error);
        return ExitCode.Usage;
    }

    private static void Error(string message) => Console.Error.WriteLine(message);

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help" or "-?" or "/?";

    private static void PrintUsage(TextWriter? writer = null)
    {
        (writer ?? Console.Out).WriteLine(
            """
            hotkeyai — author and inspect Hotkey AI automations

            Usage:
              hotkeyai validate <file> [--json]   Check a plan against the DSL schema
              hotkeyai explain  <file>            Print the plan in readable form
              hotkeyai run      <file> [--dry-run] [--ui]
                                           Execute a plan on this machine
              hotkeyai schema                     Print the DSL schema to stdout
              hotkeyai apps                       Show which logical app names resolve here

            The resident agent:
              hotkeyai list                       Show every automation and whether it can run
              hotkeyai approve                    Review and approve pending automations
              hotkeyai autostart [on|off|status]  Start Hotkey AI at login

            Exit codes:
              0  valid / success
              1  the plan is invalid
              2  bad usage, or the file could not be read

            Authoring a plan? Read schema/hotkeyai-dsl-v1.schema.json and
            docs/capabilities.md, then validate before trusting it. --json gives
            machine-readable errors suitable for an automated fix loop.

            Not yet available (these need the agent): import, logs
            """);
    }
}
