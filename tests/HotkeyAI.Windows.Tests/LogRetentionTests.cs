using HotkeyAI.Windows;

namespace HotkeyAI.Windows.Tests;

/// <summary>
/// Logs stop existing eventually, and only the ones this code understands are deleted.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding L2. Logs accumulated one file per day, kept for as long as the
/// machine lasted, each holding the window titles and file paths PLAN.md item 7 flags as
/// PII/confidential-adjacent. Nothing rotated, retained or pruned them.
/// <para>
/// The dangerous half of a retention rule is the deleting, so most of these are about what it must
/// leave alone.
/// </para>
/// </remarks>
public sealed class LogRetentionTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 17);

    private readonly string folder =
        Path.Combine(Path.GetTempPath(), "hotkeyai-logs-" + Guid.NewGuid().ToString("N")[..8]);

    public LogRetentionTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that will not delete is not a test failure.
        }
    }

    private string Write(string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "a log line");
        return path;
    }

    private string[] Remaining() =>
        [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    // ------------------------------- what goes -------------------------------

    [Fact]
    public void ALogOlderThanTheWindowGoes()
    {
        Write("agent-2026-07-01.log");

        Assert.Equal(1, LogRetention.Prune(folder, Today));
        Assert.Empty(Remaining());
    }

    [Fact]
    public void ARolledPartOfAnOldDayGoesToo()
    {
        // agent-2026-07-01.2.log is the same day's log continued, and keeping half a day's record
        // would be worse than keeping none.
        Write("agent-2026-07-01.log");
        Write("agent-2026-07-01.2.log");

        Assert.Equal(2, LogRetention.Prune(folder, Today));
        Assert.Empty(Remaining());
    }

    // ------------------------------- what stays -------------------------------

    [Fact]
    public void TodaysLogStays()
    {
        Write("agent-2026-08-17.log");

        Assert.Equal(0, LogRetention.Prune(folder, Today));
        Assert.Single(Remaining());
    }

    [Fact]
    public void ALogInsideTheWindowStays()
    {
        Write("agent-2026-08-05.log");   // twelve days old, inside fourteen

        Assert.Equal(0, LogRetention.Prune(folder, Today));
        Assert.Single(Remaining());
    }

    [Fact]
    public void TheBoundaryDayStays()
    {
        // Exactly fourteen days old. Inclusive, because "kept for two weeks" reads as including the
        // fourteenth day, and an off-by-one here silently shortens the retention someone chose.
        Write("agent-2026-08-03.log");

        Assert.Equal(0, LogRetention.Prune(folder, Today));
        Assert.Single(Remaining());
    }

    [Fact]
    public void AFileThisCodeDoesNotUnderstandIsLeftAlone()
    {
        // Deleting a file whose name means nothing to this code is how a folder someone repurposed
        // loses something that mattered.
        Write("agent-notes.log");
        Write("agent-.log");
        Write("agent-2026-13-45.log");   // parses as a name, not as a date

        Assert.Equal(0, LogRetention.Prune(folder, Today));
        Assert.Equal(3, Remaining().Length);
    }

    [Fact]
    public void SomethingElseEntirelyIsLeftAlone()
    {
        Write("readme.txt");
        Write("crash-2020-01-01.log");

        Assert.Equal(0, LogRetention.Prune(folder, Today));
        Assert.Equal(2, Remaining().Length);
    }

    [Fact]
    public void OnlyTheOldOnesGoWhenBothArePresent()
    {
        Write("agent-2026-07-01.log");
        Write("agent-2026-08-16.log");
        Write("agent-2026-08-17.log");
        Write("readme.txt");

        Assert.Equal(1, LogRetention.Prune(folder, Today));
        Assert.Equal(
            ["agent-2026-08-16.log", "agent-2026-08-17.log", "readme.txt"],
            Remaining());
    }

    [Fact]
    public void AMissingFolderIsNotAnError()
    {
        // The agent prunes at startup, which can be before anything has been logged.
        Assert.Equal(0, LogRetention.Prune(Path.Combine(folder, "nope"), Today));
    }

    // ------------------------------- the name parser -------------------------------

    [Theory]
    [InlineData("agent-2026-08-17.log", 2026, 8, 17)]
    [InlineData("agent-2026-08-17.2.log", 2026, 8, 17)]
    [InlineData("agent-2026-08-17.999.log", 2026, 8, 17)]
    [InlineData("agent-2000-01-01.log", 2000, 1, 1)]
    public void ADateIsReadFromTheName(string name, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), LogRetention.DateFromName(name));
    }

    [Theory]
    [InlineData("agent.log")]
    [InlineData("agent-.log")]
    [InlineData("agent-2026-08.log")]
    [InlineData("agent-17-08-2026.log")]
    [InlineData("hotkeyai-2026-08-17.log")]
    [InlineData("")]
    public void AnUnrecognisedNameHasNoDate(string name)
    {
        Assert.Null(LogRetention.DateFromName(name));
    }

    [Fact]
    public void AFullPathWorksAsWellAsABareName()
    {
        Assert.Equal(
            new DateOnly(2026, 8, 17),
            LogRetention.DateFromName(@"C:\Users\me\AppData\Local\HotkeyAI\logs\agent-2026-08-17.log"));
    }
}
