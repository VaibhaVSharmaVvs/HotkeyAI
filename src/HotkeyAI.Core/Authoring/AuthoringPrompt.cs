namespace HotkeyAI.Core.Authoring;

/// <summary>
/// Builds the prompt that turns a description into an automation.
/// </summary>
/// <remarks>
/// The V1 planner is a person with Claude Code, not code in this application. This produces the
/// prompt they paste, which is the entire bridge: the description, the contract to write against,
/// and the rules the validator will enforce anyway. Getting those rules into the prompt is what
/// makes the difference between a plan that validates first time and three rounds of fixing.
/// <para>
/// It lives in Core, and is a pure function of its input, for the same reason the fuzzy matcher
/// does: it is the part of this feature that can be tested, and V2 will hand almost exactly this
/// text to an API instead of to the clipboard. When it does, the schema goes alongside it as the
/// structured-output definition — which is why the rules below are the ones the schema cannot
/// express, rather than a restatement of it.
/// </para>
/// </remarks>
public static class AuthoringPrompt
{
    /// <summary>Compose the prompt for a described automation.</summary>
    /// <param name="description">What the user wants it to do, in their own words.</param>
    /// <param name="hotkey">The chord they intend to bind, if they picked one.</param>
    public static string For(string description, string? hotkey = null)
    {
        ArgumentNullException.ThrowIfNull(description);

        var wanted = description.Trim();

        if (wanted.Length == 0)
        {
            wanted = "(describe the automation here)";
        }

        var trigger = string.IsNullOrWhiteSpace(hotkey)
            ? "Choose a sensible unused chord; CTRL+ALT+<letter> is usually free."
            : $"Bind it to {hotkey.Trim()}.";

        return $"""
            Write a Hotkey AI automation that does this:

              {wanted}

            {trigger}

            The contract is schema/hotkeyai-dsl-v1.schema.json in this repository — draft 2020-12,
            and authoritative. Read it before writing, including the description on each action and
            property: they state the real constraints, including numeric bounds the schema itself
            cannot express. docs/capabilities.md is the readable summary of the same thing.

            Rules the validator enforces, which are easy to get wrong:

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

            Validate before you answer:

              dotnet run --project src/HotkeyAI.Cli -- validate <file>
              dotnet run --project src/HotkeyAI.Cli -- explain  <file>

            `explain` prints what the user will see, including which actions are unverified. An
            automation that validates but explains wrongly is still wrong.

            Reply with the finished JSON object and nothing else, so it can be pasted straight back
            into Hotkey AI.
            """;
    }
}
