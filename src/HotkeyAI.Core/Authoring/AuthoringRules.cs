namespace HotkeyAI.Core.Authoring;

/// <summary>
/// The constraints a plan has to satisfy, written for whoever is authoring one.
/// </summary>
/// <remarks>
/// Shared by the prompt that asks for a new automation and the prompt that asks for a broken one
/// to be repaired. Two copies would drift, and the copy that omits a rule produces plans the
/// validator rejects for reasons the model was never told.
/// <para>
/// These are deliberately the rules the <i>schema cannot express</i> — numeric bounds, the
/// execution hierarchy, dataflow between variables. The schema states the shape and, in V2, is
/// handed to the API as the structured-output definition; this states everything the shape leaves
/// out. Restating the schema here would be duplication that goes stale.
/// </para>
/// </remarks>
public static class AuthoringRules
{
    /// <summary>Where the contract lives, and how to check a plan against it.</summary>
    public const string Contract = """
        The contract is schema/hotkeyai-dsl-v1.schema.json in this repository — draft 2020-12,
        and authoritative. Read it before writing, including the description on each action and
        property: they state the real constraints, including numeric bounds the schema itself
        cannot express. docs/capabilities.md is the readable summary of the same thing.
        """;

    /// <summary>The constraints the policy layer enforces after the schema has had its say.</summary>
    public const string Rules = """
        - Follow the execution hierarchy: native API, then app CLI arguments, then UI
          Automation, then synthetic input. Prefer launch_process with argv over focusing a
          window and sending keystrokes. Prefer send_appcommand over driving a media player.
          send_keys is a last resort and cannot reach elevated windows, where it fails silently.
        - Prefer `app` over `path` on launch_process, so the plan survives app updates and
          machine changes. A path built from a variable is refused outright.
        - Add an `expect` wherever it is meaningful. Only five postconditions are verifiable;
          do not invent verification that is not real. Actions without one are reported to the
          user as unverified.
        - Declare every variable before use, with the right type. list_directories produces a
          pathList, and picking from one yields a path, not text.
        - Three action levels only: if/foreach may contain one more if/foreach, and that inner
          one may contain leaf actions only.
        - argv is a list of separate arguments, never a command line. No quoting, no shell.
        - Never put secrets in a plan. It is plain JSON on disk.
        """;

    /// <summary>How to check the answer before giving it.</summary>
    public const string Checking = """
        Validate before you answer:

          dotnet run --project src/HotkeyAI.Cli -- validate <file>
          dotnet run --project src/HotkeyAI.Cli -- explain  <file>

        `explain` prints what the user will see, including which actions are unverified. An
        automation that validates but explains wrongly is still wrong.
        """;
}
