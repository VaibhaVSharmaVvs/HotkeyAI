using HotkeyAI.Core.Authoring;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The prompt the dashboard hands to Claude Code.
/// </summary>
/// <remarks>
/// Worth testing because it is the whole V1 planner. If the description goes missing, or the
/// rules the policy layer enforces are absent, the user gets a plan the validator rejects and
/// has no idea the prompt was at fault rather than the model.
/// </remarks>
public sealed class AuthoringPromptTests
{
    [Fact]
    public void TheDescriptionIsCarriedThrough() =>
        Assert.Contains(
            "close everything except my editor",
            AuthoringPrompt.For("close everything except my editor"),
            StringComparison.Ordinal);

    [Fact]
    public void AnEmptyDescriptionLeavesAVisiblePlaceholder()
    {
        // Rather than producing a prompt that silently asks for nothing in particular.
        var prompt = AuthoringPrompt.For("   ");

        Assert.Contains("(describe the automation here)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AGivenHotkeyIsRequestedExplicitly() =>
        Assert.Contains("Bind it to CTRL+ALT+J", AuthoringPrompt.For("do a thing", "  CTRL+ALT+J "),
            StringComparison.Ordinal);

    [Fact]
    public void WithoutAHotkeyItAsksForAFreeOne() =>
        Assert.Contains("CTRL+ALT+<letter>", AuthoringPrompt.For("do a thing"),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("hotkeyai-dsl-v1.schema.json")]   // the contract itself
    [InlineData("execution hierarchy")]           // native API before synthetic input
    [InlineData("Prefer `app` over `path`")]      // survives app updates
    [InlineData("expect")]                        // postconditions, or it reports unverified
    [InlineData("Three action levels")]           // the nesting limit the schema encodes
    [InlineData("argv is a list of separate arguments")]
    [InlineData("Never put secrets")]
    [InlineData("validate")]                      // check before answering
    public void TheRulesThePolicyLayerEnforcesAreStated(string rule) =>
        Assert.Contains(rule, AuthoringPrompt.For("anything"), StringComparison.Ordinal);

    [Fact]
    public void ItAsksForJsonAndNothingElse() =>
        // Otherwise the reply arrives wrapped in prose and cannot be pasted straight back.
        Assert.Contains("nothing else", AuthoringPrompt.For("anything"), StringComparison.Ordinal);

    [Fact]
    public void NullIsRejectedRatherThanRenderedAsEmpty() =>
        Assert.Throws<ArgumentNullException>(() => AuthoringPrompt.For(null!));
}
