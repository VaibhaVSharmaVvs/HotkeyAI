using System.Text.Json;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;

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
            "import" or "run" or "logs" => NotYetImplemented(verb),
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

        var result = SchemaValidator.Validate(json);

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

            // Valid against the schema is not the same as ready to run. Say so, rather than
            // letting "valid" imply more than it means.
            Console.WriteLine(
                "Note: this is structural validation only. The policy layer (numeric bounds, "
                + "allowed roots, variable dataflow) is not yet implemented.");

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

    private static int PrintSchema()
    {
        // Handy for piping the contract to an authoring tool.
        Console.WriteLine(DslSchema.Text);
        return ExitCode.Ok;
    }

    // ----------------------------------------------------------------------------------

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
              hotkeyai schema                     Print the DSL schema to stdout

            Exit codes:
              0  valid / success
              1  the plan is invalid
              2  bad usage, or the file could not be read

            Authoring a plan? Read schema/hotkeyai-dsl-v1.schema.json and
            docs/capabilities.md, then validate before trusting it. --json gives
            machine-readable errors suitable for an automated fix loop.

            Not yet available (these need the agent): import, run, logs
            """);
    }
}
