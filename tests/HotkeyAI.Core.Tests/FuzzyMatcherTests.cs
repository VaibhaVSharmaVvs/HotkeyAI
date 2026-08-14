using HotkeyAI.Core.Matching;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The picker's ranking.
/// </summary>
/// <remarks>
/// These exist because ranking is the only part of an overlay that can be got quietly wrong. A
/// picker that shows the right items in the wrong order still looks fine in a screenshot, and the
/// cost lands on someone who typed three letters, pressed Enter without reading, and opened the
/// wrong project. Clicking through the UI does not prove any of this; these do.
/// </remarks>
public sealed class FuzzyMatcherTests
{
    private static int Score(string candidate, string query) =>
        FuzzyMatcher.Match(candidate, query)?.Score
        ?? throw new InvalidOperationException($"'{query}' should match '{candidate}'");

    // ------------------------------- matching at all -------------------------------

    [Theory]
    [InlineData("HotkeyAI", "hk")]
    [InlineData("HotkeyAI", "hotkeyai")]
    [InlineData("HotkeyAI", "")]
    [InlineData("C:\\src\\my-project", "myproj")]
    [InlineData("open-solution", "os")]
    public void SubsequencesMatch(string candidate, string query) =>
        Assert.NotNull(FuzzyMatcher.Match(candidate, query));

    [Theory]
    [InlineData("HotkeyAI", "xyz")]
    [InlineData("HotkeyAI", "iah")]      // right letters, wrong order
    [InlineData("abc", "abcd")]          // query longer than candidate
    public void NonSubsequencesDoNot(string candidate, string query) =>
        Assert.Null(FuzzyMatcher.Match(candidate, query));

    [Fact]
    public void AnEmptyQueryMatchesEverythingWithNoHighlight()
    {
        // This is what shows the full list before the user types. If it returned null the picker
        // would open empty, which reads as "nothing to choose from".
        var match = FuzzyMatcher.Match("anything at all", "");

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.Score);
        Assert.Empty(match.Value.Positions);
    }

    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.NotNull(FuzzyMatcher.Match("HotkeyAI", "HOTKEY"));

    // ------------------------------- ranking -------------------------------

    [Fact]
    public void InitialsBeatLettersBuriedMidWord() =>
        Assert.True(
            Score("open-solution", "os") > Score("closet", "os"),
            "'os' as two word-initials must rank above the same letters inside one word.");

    [Fact]
    public void ConsecutiveLettersBeatScatteredOnes() =>
        Assert.True(
            Score("project", "pro") > Score("parrot", "pro"),
            "A contiguous run is a better match than the same letters spread out.");

    [Fact]
    public void SeparatedInitialsAreStillAStrongMatch() =>
        // The counterpart to the test above, and the reason it does not use "p-r-o-x-y" as its
        // scattered example: letters sitting at word boundaries are an acronym, not scatter, and
        // ranking them poorly would break the main use case of picking from hyphenated names.
        Assert.True(
            Score("p-r-o-x-y", "pro") > Score("parrot", "pro"),
            "Letters at word starts are an acronym match and must outrank genuine scatter.");

    [Fact]
    public void AMatchAtTheStartBeatsOneInTheMiddle() =>
        Assert.True(
            Score("hotkey-notes", "hot") > Score("my-hot-take", "hot"),
            "Leading matches should win; a query is usually the start of what you mean.");

    [Fact]
    public void CamelCaseBoundariesCount() =>
        Assert.True(
            Score("openSolution", "oS") > Score("oxbows", "oS"),
            "An interior capital starts a word, so 'oS' should find openSolution.");

    [Fact]
    public void ExactCaseBreaksTiesTowardTheObviousAnswer() =>
        Assert.True(
            Score("Hotkey", "H") > Score("hotkey", "H"),
            "With everything else equal, the candidate that matches case should come first.");

    // ------------------------------- positions -------------------------------

    [Fact]
    public void PositionsPointAtTheMatchedCharacters()
    {
        var match = FuzzyMatcher.Match("open-solution", "os");

        Assert.NotNull(match);
        Assert.Equal([0, 5], match.Value.Positions);
    }

    [Fact]
    public void PositionsAreAscendingAndOneForEachQueryCharacter()
    {
        var match = FuzzyMatcher.Match(@"C:\Users\me\src\hotkey-ai\README.md", "hotai");

        Assert.NotNull(match);
        var positions = match.Value.Positions;

        Assert.Equal(5, positions.Count);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Distinct(positions);
    }

    [Fact]
    public void PositionsSelectTheHighestScoringOccurrenceNotTheFirst()
    {
        // "ha" appears at index 0, but the run at the word boundary scores better. Backtracking
        // has to follow the score, or the highlight will not agree with the ranking.
        var match = FuzzyMatcher.Match("haystack-hat", "hat");

        Assert.NotNull(match);
        Assert.Equal([9, 10, 11], match.Value.Positions);
    }

    // ------------------------------- Rank over a list -------------------------------

    [Fact]
    public void RankDropsNonMatchesAndOrdersByScore()
    {
        string[] items = ["closet", "open-solution", "unrelated", "os-notes"];

        var ranked = FuzzyMatcher.Rank(items, "os");

        Assert.DoesNotContain(ranked, r => items[r.Index] == "unrelated");
        Assert.Equal("os-notes", items[ranked[0].Index]);
    }

    [Fact]
    public void RankKeepsTheCallersOrderForEqualScores()
    {
        // Two identical strings can only be separated by their original position. Anything else
        // means the list reshuffles between keystrokes, and someone selects the wrong row.
        string[] items = ["same", "same", "same"];

        var ranked = FuzzyMatcher.Rank(items, "sa");

        Assert.Equal([0, 1, 2], ranked.Select(r => r.Index));
    }

    [Fact]
    public void RankPrefersTheShorterCandidateWhenScoresTie()
    {
        string[] items = ["project-management-system", "project"];

        var ranked = FuzzyMatcher.Rank(items, "project");

        Assert.Equal("project", items[ranked[0].Index]);
    }

    [Fact]
    public void AnEmptyQueryKeepsEveryItemInTheOriginalOrder()
    {
        string[] items = ["gamma", "alpha", "beta"];

        var ranked = FuzzyMatcher.Rank(items, "");

        Assert.Equal([0, 1, 2], ranked.Select(r => r.Index));
    }

    // ------------------------------- robustness -------------------------------

    [Fact]
    public void AVeryLongCandidateIsScoredWithoutStalling()
    {
        // The overlay re-ranks on every keystroke; one huge item must not make it feel slow.
        var candidate = new string('a', 50_000) + "needle";

        Assert.Null(FuzzyMatcher.Match(candidate, "needle"));
    }

    [Fact]
    public void RankHandlesAnEmptyList() =>
        Assert.Empty(FuzzyMatcher.Rank([], "anything"));

    [Fact]
    public void NullArgumentsAreRejectedRatherThanMatched()
    {
        Assert.Throws<ArgumentNullException>(() => FuzzyMatcher.Match(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => FuzzyMatcher.Match("a", null!));
    }
}
