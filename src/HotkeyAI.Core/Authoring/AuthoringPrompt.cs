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

            {AuthoringRules.Contract}

            Rules the validator enforces, which are easy to get wrong:

            {AuthoringRules.Rules}

            {AuthoringRules.Checking}

            Reply with the finished JSON object and nothing else, so it can be pasted straight back
            into Hotkey AI.
            """;
    }
}
