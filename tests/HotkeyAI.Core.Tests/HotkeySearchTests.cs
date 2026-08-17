using HotkeyAI.Core.Matching;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The dashboard's search box.
/// </summary>
/// <remarks>
/// Worth testing properly for the same reason the picker's ranking is: a filter that drops the
/// row you wanted looks exactly like a list that never had it. There is no error, nothing on
/// screen is wrong, and the user concludes the automation is gone.
/// </remarks>
public sealed class HotkeySearchTests
{
    private const string Name = "Close Distractions";
    private const string Chord = "Ctrl + Alt + X";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankQueryKeepsEverything(string query) =>
        Assert.True(HotkeySearch.Matches(Name, Chord, query));

    [Theory]
    [InlineData("close")]
    [InlineData("Distractions")]
    [InlineData("CLOSE")]
    [InlineData("distr")]
    public void TheNameMatchesHoweverItIsCased(string query) =>
        Assert.True(HotkeySearch.Matches(Name, Chord, query));

    [Fact]
    public void AnAcronymFindsIt()
    {
        // The same behaviour the picker has, so what someone learns in one place works in the
        // other.
        Assert.True(HotkeySearch.Matches(Name, Chord, "cd"));
    }

    [Theory]
    [InlineData("ctrl+alt+x")]
    [InlineData("ctrl + alt + x")]
    [InlineData("ctrl alt x")]
    [InlineData("CtrlAltX")]
    [InlineData("CTRL+ALT+X")]
    public void EveryWayOfWritingTheChordFindsIt(string query)
    {
        // The point of the whole chord branch. The list renders "Ctrl + Alt + X" and nobody
        // types it that way.
        Assert.True(HotkeySearch.Matches(Name, Chord, query));
    }

    [Theory]
    [InlineData("altx")]
    [InlineData("alt x")]
    [InlineData("ctrlalt")]
    public void PartOfTheChordIsEnough(string query) =>
        Assert.True(HotkeySearch.Matches(Name, Chord, query));

    [Fact]
    public void SomethingInNeitherMatchesNothing() =>
        Assert.False(HotkeySearch.Matches(Name, Chord, "wallpaper"));

    [Fact]
    public void AQueryLongerThanBothIsRejectedRatherThanPartiallyMatched() =>
        Assert.False(HotkeySearch.Matches(Name, Chord, "close distractions and everything else"));

    [Fact]
    public void PunctuationAloneDoesNotMatchEverything()
    {
        // "+++" reduces to nothing once separators are stripped, and an empty needle is
        // contained in every string. Left unguarded that would silently match every row while
        // looking like a query that found them.
        Assert.False(HotkeySearch.Matches(Name, Chord, "+++"));
    }

    [Fact]
    public void AChordOnlyQueryDoesNotNeedTheNameToAgree()
    {
        // The two handles are independent: searching by combination must work for an automation
        // whose name shares no letters with what was typed.
        Assert.True(HotkeySearch.Matches("Play / Pause", "Ctrl + Alt + M", "ctrlaltm"));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => HotkeySearch.Matches(null!, Chord, "x"));
        Assert.Throws<ArgumentNullException>(() => HotkeySearch.Matches(Name, null!, "x"));
        Assert.Throws<ArgumentNullException>(() => HotkeySearch.Matches(Name, Chord, null!));
    }
}
