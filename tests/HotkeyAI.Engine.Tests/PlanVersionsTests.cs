using HotkeyAI.Engine.Store;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// Past versions of a plan, and getting one back.
/// </summary>
/// <remarks>
/// The history exists so an AI-authored change is reversible. That makes two properties matter
/// more than the rest: a version must be readable back exactly as it was written, and reloading
/// an unchanged plan must not fill the history with copies of itself — a history of forty
/// identical entries hides the one edit worth finding.
/// </remarks>
public sealed class PlanVersionsTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "hotkeyai-versions-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup, not the thing under test. A temp folder that will not delete must not
            // report as a failing test, and the two platforms throw differently here.
        }
    }

    private FileVersionStore Store(int keep = 20) => new(root, keep);

    [Fact]
    public void AnAutomationWithNoHistoryHasNone() =>
        Assert.Empty(Store().History("mine.json"));

    [Fact]
    public void CapturingRecordsAVersionThatReadsBackExactly()
    {
        var store = Store();
        const string content = "{ \"a\": 1 }";

        store.Capture("mine.json", content);

        var version = Assert.Single(store.History("mine.json"));
        Assert.Equal(content, store.Read("mine.json", version.Id));
    }

    [Fact]
    public void CapturingTheSameContentTwiceRecordsItOnce()
    {
        // Every rebind and every folder change reloads, so without this the history would be a
        // list of identical copies of whatever the plan currently says.
        var store = Store();

        store.Capture("mine.json", "same");
        store.Capture("mine.json", "same");
        store.Capture("mine.json", "same");

        Assert.Single(store.History("mine.json"));
    }

    [Fact]
    public void ChangingTheContentAddsAVersion()
    {
        var store = Store();

        store.Capture("mine.json", "first");
        store.Capture("mine.json", "second");

        Assert.Equal(2, store.History("mine.json").Count);
    }

    [Fact]
    public void ReturningToEarlierContentIsRecordedAsANewVersion()
    {
        // It is only compared against the newest, not against everything ever seen. Reverting is
        // a change, and the history should read as a sequence of what happened.
        var store = Store();

        store.Capture("mine.json", "first");
        store.Capture("mine.json", "second");
        store.Capture("mine.json", "first");

        Assert.Equal(3, store.History("mine.json").Count);
    }

    [Fact]
    public void HistoryIsNewestFirst()
    {
        var store = Store();

        store.Capture("mine.json", "oldest");
        store.Capture("mine.json", "middle");
        store.Capture("mine.json", "newest");

        var history = store.History("mine.json");
        Assert.Equal("newest", store.Read("mine.json", history[0].Id));
        Assert.Equal("oldest", store.Read("mine.json", history[^1].Id));
    }

    [Fact]
    public void HistoryIsPerAutomation()
    {
        var store = Store();

        store.Capture("one.json", "a");
        store.Capture("two.json", "b");

        Assert.Single(store.History("one.json"));
        Assert.Equal("b", store.Read("two.json", store.History("two.json")[0].Id));
    }

    [Fact]
    public void OldVersionsArePrunedNewestKept()
    {
        var store = Store(keep: 3);

        foreach (var i in Enumerable.Range(1, 6))
        {
            store.Capture("mine.json", $"version {i}");
        }

        var history = store.History("mine.json");
        Assert.Equal(3, history.Count);
        Assert.Equal("version 6", store.Read("mine.json", history[0].Id));
    }

    [Fact]
    public void APrunedVersionReadsAsMissingRatherThanThrowing()
    {
        var store = Store(keep: 1);
        store.Capture("mine.json", "first");
        var first = store.History("mine.json")[0].Id;

        store.Capture("mine.json", "second");

        Assert.Null(store.Read("mine.json", first));
    }

    [Theory]
    [InlineData(@"..\..\..\Windows\System32\drivers\etc\hosts")]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    public void AVersionIdCannotReachOutsideItsOwnFolder(string id)
    {
        // The id comes from History, but it arrives back through a UI, so it is treated as input.
        // Both separators, because only one of them is a separator on any given platform and the
        // guard has to hold on the one that is.
        var store = Store();
        store.Capture("mine.json", "content");

        Assert.Null(store.Read("mine.json", id));
    }

    [Fact]
    public void TheHashMatchesTheStoresApprovalHashing()
    {
        // So a version can be compared against what is approved without rehashing it differently.
        var store = Store();
        store.Capture("mine.json", "{ \"a\": 1 }");

        Assert.Equal(
            AutomationStore.HashOf("{ \"a\": 1 }"),
            store.History("mine.json")[0].ContentHash);
    }

    [Fact]
    public void LineEndingsDoNotCreateANewVersion()
    {
        // The same normalisation approval uses. A checkout that rewrites line endings is not a
        // change to the plan, and should not look like one in the history either.
        var store = Store();

        store.Capture("mine.json", "{\r\n  \"a\": 1\r\n}");
        store.Capture("mine.json", "{\n  \"a\": 1\n}");

        Assert.Single(store.History("mine.json"));
    }

    [Fact]
    public void ManyRapidCapturesStayInOrder()
    {
        // The regression test for a CI failure that only appeared on Linux. Ordering came from a
        // millisecond timestamp in the file name, and captures are fast enough to share one — on
        // a quick filesystem, several land in the same millisecond and the order then fell to the
        // content hash, which is arbitrary. "Restore the previous version" restoring some other
        // version is about as bad as this feature gets.
        var store = Store(keep: 50);

        foreach (var i in Enumerable.Range(1, 30))
        {
            store.Capture("mine.json", $"version {i}");
        }

        var history = store.History("mine.json");

        Assert.Equal(30, history.Count);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal($"version {30 - i}", store.Read("mine.json", history[i].Id));
        }
    }
}
