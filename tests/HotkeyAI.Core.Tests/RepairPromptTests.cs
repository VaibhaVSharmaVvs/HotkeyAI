using HotkeyAI.Core.Authoring;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The prompt the dashboard hands to Claude Code when an automation misbehaves.
/// </summary>
/// <remarks>
/// The point of the exporter is that a user knows what they expected and nothing else. If any of
/// the four pieces of evidence — the complaint, the plan, the transcript, the rules — goes
/// missing, the answer that comes back is a guess, and nothing about the output would look wrong.
/// </remarks>
public sealed class RepairPromptTests
{
    private const string Plan = """
        { "schemaVersion": 1, "name": "Broken", "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","B"] },
          "actions": [ { "type": "notify", "message": "hi" } ] }
        """;

    private const string Transcript = """
        [s1] launch_process: Succeeded (verified) — Launched Code.exe.
        [s2] focus_window: Failed — No window matched the window with process "Code".

        FAILED: No window matched the window with process "Code".
        """;

    [Fact]
    public void TheComplaintIsCarriedThrough() =>
        Assert.Contains(
            "it opens the wrong folder",
            RepairPrompt.For("a.json", Plan, Transcript, "it opens the wrong folder"),
            StringComparison.Ordinal);

    [Fact]
    public void ThePlanIsIncludedVerbatim() =>
        Assert.Contains(
            "\"name\": \"Broken\"",
            RepairPrompt.For("a.json", Plan, Transcript, "wrong"),
            StringComparison.Ordinal);

    [Fact]
    public void TheTranscriptIsIncluded() =>
        Assert.Contains(
            "No window matched",
            RepairPrompt.For("a.json", Plan, Transcript, "wrong"),
            StringComparison.Ordinal);

    [Fact]
    public void TheFileIsNamedSoTheAnswerKnowsWhatItIsReplacing() =>
        Assert.Contains(
            "open-solution.json",
            RepairPrompt.For("open-solution.json", Plan, Transcript, "wrong"),
            StringComparison.Ordinal);

    [Fact]
    public void AMissingTranscriptIsStatedRatherThanLeftBlank()
    {
        // "No transcript" is information: the complaint is about a plan that has not run, so the
        // reader should not assume the plan is at fault. An empty section invites that assumption.
        var prompt = RepairPrompt.For("a.json", Plan, transcript: null, "wrong");

        Assert.Contains("has not run", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("```\n\n```", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyComplaintLeavesAVisiblePlaceholder() =>
        Assert.Contains(
            "(describe what it did",
            RepairPrompt.For("a.json", Plan, Transcript, "   "),
            StringComparison.Ordinal);

    [Fact]
    public void ItSaysThatUnverifiedIsNotSuccess() =>
        // The single most useful thing the prompt can say, because it is the failure mode the
        // engine reports most often and the one a reader is most likely to skim past.
        Assert.Contains(
            "silence is not success",
            RepairPrompt.For("a.json", Plan, Transcript, "wrong"),
            StringComparison.Ordinal);

    [Fact]
    public void ItAllowsTheAnswerToBeThatThePlanIsFine() =>
        // Otherwise every complaint produces a rewrite, including the ones where the plan was
        // correct and the world was not as it assumed.
        Assert.Contains(
            "If the plan is not the problem",
            RepairPrompt.For("a.json", Plan, Transcript, "wrong"),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("hotkeyai-dsl-v1.schema.json")]
    [InlineData("execution hierarchy")]
    [InlineData("Three action levels")]
    [InlineData("validate")]
    public void ItCarriesTheSameRulesAsTheAuthoringPrompt(string rule)
    {
        // Shared through AuthoringRules rather than restated, so a rule added for authoring
        // cannot go missing from repair.
        Assert.Contains(rule, RepairPrompt.For("a.json", Plan, Transcript, "x"), StringComparison.Ordinal);
        Assert.Contains(rule, AuthoringPrompt.For("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => RepairPrompt.For(null!, Plan, null, "x"));
        Assert.Throws<ArgumentNullException>(() => RepairPrompt.For("a.json", null!, null, "x"));
        Assert.Throws<ArgumentNullException>(() => RepairPrompt.For("a.json", Plan, null, null!));
    }
}
