using System.Text.Json;
using HotkeyAI.Core;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;

namespace HotkeyAI.Core.Tests;

public sealed class PlanRendererTests
{
    private static Automation Load(string fileName) =>
        JsonSerializer.Deserialize<Automation>(
            File.ReadAllText(Path.Combine(RepoPaths.Examples, fileName)), DslJson.Options)!;

    [Theory]
    [MemberData(nameof(ExampleTests.Examples), MemberType = typeof(ExampleTests))]
    public void EveryExampleRenders(string fileName)
    {
        // This is how the renderer gets full coverage of all 25 action types without
        // hand-constructing each one: validate_examples.py gates the example corpus at 100%
        // action-type coverage, so rendering every example necessarily exercises every action.
        // DescribeAction throws on an unhandled type rather than emitting a placeholder, so a
        // primitive added without renderer support fails here loudly.
        var rendered = PlanRenderer.Explain(Load(fileName));

        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.DoesNotContain("Unhandled", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiedActionsAreCalledOut()
    {
        // spotify-play-pause is a single send_appcommand: playback state is not observable, so
        // it genuinely cannot be verified. The preview must say so rather than implying the
        // engine confirmed anything.
        var rendered = PlanRenderer.Explain(Load("spotify-play-pause.json"));

        Assert.Contains("(unverified)", rendered, StringComparison.Ordinal);
        Assert.Contains("cannot confirm they had any effect", rendered, StringComparison.Ordinal);
        Assert.Contains("0 of 1 actions", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedActionsShowWhatWillBeChecked()
    {
        var rendered = PlanRenderer.Explain(Load("project-launcher.json"));

        Assert.Contains("(verified)", rendered, StringComparison.Ordinal);
        Assert.Contains("process \"Code\"", rendered, StringComparison.Ordinal);
        Assert.Contains("within 20000 ms", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerRendersAsAReadableChord()
    {
        Assert.Equal(
            "Ctrl + Alt + P",
            PlanRenderer.DescribeTrigger(Load("project-launcher.json").Trigger));
    }

    [Fact]
    public void NestedActionsAreNumberedHierarchically()
    {
        // open-all-repos is foreach -> if -> leaf, the deepest legal nesting.
        var rendered = PlanRenderer.Explain(Load("open-all-repos.json"));

        Assert.Contains("3.1.", rendered, StringComparison.Ordinal);
        Assert.Contains("3.2.", rendered, StringComparison.Ordinal);
        Assert.Contains("3.2.1.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void NegatedPredicatesReadCorrectly()
    {
        var rendered = PlanRenderer.Explain(Load("work-environment.json"));

        Assert.Contains("is not running", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinueOnErrorIsSurfaced()
    {
        // Silently continuing past a failure is exactly the kind of thing a user should see
        // in the preview before approving a plan.
        var rendered = PlanRenderer.Explain(Load("close-distractions.json"));

        Assert.Contains("on failure: continue", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownActionTypeThrowsRatherThanRenderingAPlaceholder()
    {
        var rogue = new UnrenderableAction();

        var ex = Assert.Throws<NotSupportedException>(() => PlanRenderer.DescribeAction(rogue));
        Assert.Contains("PlanRenderer", ex.Message, StringComparison.Ordinal);
    }

    private sealed record UnrenderableAction : HotkeyAction;
}
