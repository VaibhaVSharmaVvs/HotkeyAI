using HotkeyAI.Core.Dsl;

namespace HotkeyAI.Core.Tests;

/// <summary>
/// The rule deciding whether a key combination can be bound.
/// </summary>
/// <remarks>
/// Shared by the policy validator and the dashboard's capture control, so these tests cover both.
/// The normalisation cases matter more than they look: capture reports keys in whatever order the
/// keyboard state enumerated them, and an unnormalised chord would rewrite a plan's JSON into an
/// equivalent-but-different file — silently revoking an approval the user never changed.
/// </remarks>
public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData(KeyName.Ctrl, KeyName.Alt, KeyName.P)]
    [InlineData(KeyName.Win, KeyName.F1, KeyName.Ctrl)]
    public void AModifierPlusOneKeyIsBindable(KeyName a, KeyName b, KeyName c) =>
        Assert.True(HotkeyChord.IsBindable([a, b, c]));

    [Fact]
    public void ABareKeyIsRefused()
    {
        // Load-bearing: RegisterHotKey binds a bare key happily and swallows it system-wide, and
        // the user would have no way to discover which application ate it.
        var problems = HotkeyChord.Problems([KeyName.P]);

        Assert.Contains(problems, p => p.Contains("at least one modifier", StringComparison.Ordinal));
        Assert.False(HotkeyChord.IsBindable([KeyName.P]));
    }

    [Fact]
    public void ModifiersAloneAreRefused() =>
        Assert.Contains(
            HotkeyChord.Problems([KeyName.Ctrl, KeyName.Alt]),
            p => p.Contains("exactly one non-modifier", StringComparison.Ordinal));

    [Fact]
    public void TwoNonModifiersAreRefused() =>
        Assert.Contains(
            HotkeyChord.Problems([KeyName.Ctrl, KeyName.P, KeyName.Q]),
            p => p.Contains("exactly one non-modifier", StringComparison.Ordinal));

    [Fact]
    public void ARepeatedKeyIsRefused() =>
        Assert.Contains(
            HotkeyChord.Problems([KeyName.Ctrl, KeyName.Ctrl, KeyName.P]),
            p => p.Contains("repeats a key", StringComparison.Ordinal));

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        // Not just the first. Someone fixing one and being shown the next has to try three times.
        var problems = HotkeyChord.Problems([KeyName.P, KeyName.Q]);

        Assert.Equal(2, problems.Count);
    }

    // ------------------------------- normalisation -------------------------------

    [Fact]
    public void NormaliseWritesModifiersFirstInTheConventionalOrder() =>
        Assert.Equal(
            [KeyName.Ctrl, KeyName.Alt, KeyName.Shift, KeyName.J],
            HotkeyChord.Normalise([KeyName.J, KeyName.Shift, KeyName.Alt, KeyName.Ctrl]));

    [Fact]
    public void NormaliseIsStableForAnAlreadyOrderedChord() =>
        Assert.Equal(
            [KeyName.Ctrl, KeyName.Alt, KeyName.P],
            HotkeyChord.Normalise([KeyName.Ctrl, KeyName.Alt, KeyName.P]));

    [Fact]
    public void TwoOrderingsOfTheSameChordNormaliseIdentically()
    {
        // This is what stops a re-bind to the chord an automation already has from rewriting the
        // file and revoking its approval for no reason.
        Assert.Equal(
            HotkeyChord.Normalise([KeyName.Alt, KeyName.Ctrl, KeyName.W]),
            HotkeyChord.Normalise([KeyName.W, KeyName.Ctrl, KeyName.Alt]));
    }

    [Fact]
    public void NormaliseKeepsWinInItsConventionalPlace() =>
        Assert.Equal(
            [KeyName.Ctrl, KeyName.Win, KeyName.Space],
            HotkeyChord.Normalise([KeyName.Space, KeyName.Win, KeyName.Ctrl]));

    [Fact]
    public void NullIsRejectedRatherThanTreatedAsEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => HotkeyChord.Problems(null!));
        Assert.Throws<ArgumentNullException>(() => HotkeyChord.Normalise(null!));
    }
}
