using HotkeyAI.Core.Diff;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The diff behind the review view.
/// </summary>
/// <remarks>
/// Worth testing properly because a diff that is merely plausible is worse than none: a reviewer
/// reads it instead of the plan, so a dropped step shown as unchanged is a dropped step that ships.
/// </remarks>
public sealed class LineDiffTests
{
    private static string Text(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void IdenticalTextHasNoChanges()
    {
        var diff = LineDiff.Between(Text("a", "b", "c"), Text("a", "b", "c"));

        Assert.All(diff, line => Assert.Equal(DiffKind.Same, line.Kind));
        Assert.Equal((0, 0), LineDiff.Summarise(diff));
    }

    [Fact]
    public void AnAddedLineIsReportedOnce()
    {
        var diff = LineDiff.Between(Text("a", "c"), Text("a", "b", "c"));

        Assert.Equal((1, 0), LineDiff.Summarise(diff));
        Assert.Contains(diff, l => l.Kind == DiffKind.Added && l.Text == "b");
    }

    [Fact]
    public void ARemovedLineIsReportedOnce()
    {
        var diff = LineDiff.Between(Text("a", "b", "c"), Text("a", "c"));

        Assert.Equal((0, 1), LineDiff.Summarise(diff));
        Assert.Contains(diff, l => l.Kind == DiffKind.Removed && l.Text == "b");
    }

    [Fact]
    public void AChangedLineReadsAsOldThenNew()
    {
        // Order matters for readability: the reviewer should see what was there, then what
        // replaced it, in that order.
        var diff = LineDiff.Between(Text("a", "old", "c"), Text("a", "new", "c"));

        var changed = diff.Where(l => l.Kind != DiffKind.Same).ToList();
        Assert.Equal(2, changed.Count);
        Assert.Equal(DiffKind.Removed, changed[0].Kind);
        Assert.Equal("old", changed[0].Text);
        Assert.Equal(DiffKind.Added, changed[1].Kind);
        Assert.Equal("new", changed[1].Text);
    }

    [Fact]
    public void UnchangedLinesAreKeptSoTheChangeHasContext()
    {
        var diff = LineDiff.Between(Text("a", "b", "c"), Text("a", "x", "c"));

        Assert.Contains(diff, l => l.Kind == DiffKind.Same && l.Text == "a");
        Assert.Contains(diff, l => l.Kind == DiffKind.Same && l.Text == "c");
    }

    [Fact]
    public void EveryLineOfBothVersionsAppearsSomewhere()
    {
        // The property that makes the view trustworthy. A line that exists in either version and
        // is shown in neither is a change the reviewer cannot see.
        var before = Text("one", "two", "three", "four");
        var after = Text("one", "TWO", "three", "five");

        var diff = LineDiff.Between(before, after);

        foreach (var line in new[] { "one", "two", "three", "four", "TWO", "five" })
        {
            Assert.Contains(diff, l => l.Text == line);
        }
    }

    [Fact]
    public void ARewriteReadsAsARewriteRatherThanASmallEdit()
    {
        // The headline that tells a reviewer the model replaced the plan instead of fixing it.
        var diff = LineDiff.Between(Text("a", "b", "c"), Text("x", "y", "z"));
        var (added, removed) = LineDiff.Summarise(diff);

        Assert.Equal(3, added);
        Assert.Equal(3, removed);
    }

    [Fact]
    public void MovingALineIsAnAdditionAndARemovalNotASilentSame()
    {
        var diff = LineDiff.Between(Text("a", "b"), Text("b", "a"));
        var (added, removed) = LineDiff.Summarise(diff);

        Assert.Equal(1, added);
        Assert.Equal(1, removed);
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\nb\n", "a\nb")]
    public void LineEndingsAndTrailingNewlinesAreNotChanges(string before, string after)
    {
        // Editors and git rewrite these constantly. Reporting them as changes would bury the one
        // line that actually moved, which is the only thing the view exists to show.
        Assert.Equal((0, 0), LineDiff.Summarise(LineDiff.Between(before, after)));
    }

    [Fact]
    public void EmptyToContentIsAllAdditions()
    {
        var diff = LineDiff.Between("", Text("a", "b"));

        Assert.Equal((2, 1), LineDiff.Summarise(diff));
    }

    [Fact]
    public void SomethingFarTooLongDeclinesRatherThanHanging()
    {
        var huge = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i}"));

        var diff = LineDiff.Between(huge, huge + "\nmore");

        Assert.Contains(diff, l => l.Text.Contains("too long to diff", StringComparison.Ordinal));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => LineDiff.Between(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => LineDiff.Between("a", null!));
    }
}
