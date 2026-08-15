namespace HotkeyAI.Core.Authoring;

/// <summary>
/// Builds the prompt that asks for a broken automation to be fixed.
/// </summary>
/// <remarks>
/// V1's repair loop: the application assembles the evidence, the user pastes it into Claude Code,
/// and the corrected plan comes back through the same paste-and-approve path a new one does.
/// <para>
/// The assembling is the part worth having. Someone whose automation misbehaved knows what they
/// expected and nothing else — they do not have the plan's JSON to hand, they cannot remember
/// which step failed, and they certainly will not think to mention that three actions were
/// unverified. Each of those is exactly what makes the difference between a useful answer and a
/// guess, and the application already knows all of them.
/// </para>
/// <para>
/// The execution transcript matters more than it looks. It distinguishes "the plan is wrong" from
/// "the plan is right and the world was not as expected" — an automation that failed because an
/// application was not running needs no repair at all, and a repair prompt without the transcript
/// invites a rewrite of something that was never broken.
/// </para>
/// </remarks>
public static class RepairPrompt
{
    /// <summary>
    /// Compose the repair prompt.
    /// </summary>
    /// <param name="fileName">The automation's file, so the answer can name what to replace.</param>
    /// <param name="planJson">The plan exactly as it is on disk.</param>
    /// <param name="transcript">The execution log of the run being complained about, if any.</param>
    /// <param name="complaint">What the user says went wrong, in their words.</param>
    public static string For(
        string fileName, string planJson, string? transcript, string complaint)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(planJson);
        ArgumentNullException.ThrowIfNull(complaint);

        var said = complaint.Trim();

        if (said.Length == 0)
        {
            said = "(describe what it did, and what it should have done)";
        }

        // Said explicitly rather than left as an empty heading. "No transcript" is information —
        // it means the complaint is about a plan that has not run since the agent started, and
        // the reader should not assume the plan is at fault.
        var log = string.IsNullOrWhiteSpace(transcript)
            ? "This automation has not run since the agent started, so there is no transcript.\n"
              + "Judge the plan on its own terms, and say if you need it run first."
            : transcript.Trim();

        return $"""
            An automation in this repository is not doing what it should. Fix it.

            What went wrong, in the user's words:

              {said}

            The automation is {fileName}, and this is its current plan:

            ```json
            {planJson.Trim()}
            ```

            This is the execution log of the run being complained about. Each step reports whether
            it succeeded, whether its result could be verified, and why it stopped if it did:

            ```
            {log}
            ```

            Read the log before changing anything. An action reported as unverified ran without
            the engine being able to confirm it had any effect, and that is frequently the actual
            fault — silence is not success. A step that failed because an application was not
            running may mean the plan is fine and the world was not as it assumed, in which case
            say so instead of rewriting it.

            {AuthoringRules.Contract}

            Rules the validator enforces, which are easy to get wrong:

            {AuthoringRules.Rules}

            {AuthoringRules.Checking}

            Reply with the complete corrected JSON object and nothing else, so it can be pasted
            straight back into Hotkey AI. If the plan is not the problem, say that instead of
            producing a plan.
            """;
    }
}
