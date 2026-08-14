using HotkeyAI.Core.Policy;
using HotkeyAI.Engine.Store;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The trust-on-first-use gate.
/// </summary>
/// <remarks>
/// Safety control 4 resolves a real conflict: signing automation files and refusing unsigned ones
/// is right against a dropper, but under V1's authoring model every file the user legitimately
/// writes is unsigned, so naive enforcement would make the intended workflow indistinguishable
/// from an attack. The resolution is that nothing is refused and nothing unapproved runs. These
/// tests pin both halves, because getting either wrong is silent: too strict and the user's own
/// automations never work, too loose and a dropped file executes on a keypress.
/// </remarks>
public sealed class AutomationStoreTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "hotkeyai-store-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly FakeApprovals approvals = new();

    private static readonly PolicyOptions Policy = PolicyOptions.Default with
    {
        AllowedRoots = [@"C:\Users\test"],
    };

    public AutomationStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeApprovals : IApprovalStorage
    {
        private Dictionary<string, string> data = new(StringComparer.OrdinalIgnoreCase);

        public int Writes { get; private set; }

        public IReadOnlyDictionary<string, string> Read() => data;

        public void Write(IReadOnlyDictionary<string, string> approvals)
        {
            data = new Dictionary<string, string>(approvals, StringComparer.OrdinalIgnoreCase);
            Writes++;
        }
    }

    private string WritePlan(string name, string trigger = @"[""CTRL"",""ALT"",""P""]", string extra = "")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "name": "{{Path.GetFileNameWithoutExtension(name)}}",
              "trigger": { "type": "hotkey", "keys": {{trigger}} },
              "actions": [ { "type": "notify", "message": "hello{{extra}}" } ]
            }
            """);

        return path;
    }

    private AutomationStore Store() => new(approvals, Policy);

    // ------------------------------- the gate -------------------------------

    [Fact]
    public void ANewAutomationIsLoadedButInert()
    {
        // Not refused, not run. Refusing would break the authoring workflow; running would mean
        // a file appearing in the folder gets code execution on a keypress.
        WritePlan("new.json");

        var loaded = Store().Load(directory);
        var automation = Assert.Single(loaded);

        Assert.Equal(ApprovalStatus.New, automation.Status);
        Assert.True(automation.Validation.IsValid, "The plan itself is fine — only unapproved.");
        Assert.False(automation.IsRunnable);
        Assert.Contains("review the plan", automation.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovingMakesItRunnable()
    {
        WritePlan("mine.json");
        var store = Store();

        store.Approve(store.Load(directory)[0]);

        var automation = store.Load(directory)[0];
        Assert.Equal(ApprovalStatus.Approved, automation.Status);
        Assert.True(automation.IsRunnable);
        Assert.Null(automation.Blocker);
    }

    [Fact]
    public void EditingAnApprovedAutomationRevokesItAutomatically()
    {
        // The point of hashing content rather than recording a filename: approval is granted to
        // a specific plan, so anything that changes the plan withdraws it.
        var path = WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        File.WriteAllText(path, File.ReadAllText(path).Replace("hello", "goodbye", StringComparison.Ordinal));

        var automation = store.Load(directory)[0];
        Assert.Equal(ApprovalStatus.Changed, automation.Status);
        Assert.False(automation.IsRunnable);
        Assert.Contains("approve it again", automation.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApprovedButNowInvalidAutomationDoesNotRun()
    {
        // Trust does not survive the plan becoming invalid. Both conditions are required, so a
        // once-approved automation edited into something the validator rejects stays inert.
        var path = WritePlan("mine.json");
        var store = Store();
        var original = store.Load(directory)[0];
        store.Approve(original);

        // Break it in a way that keeps the same content hash impossible — so re-approve first,
        // then corrupt, to isolate "approved but invalid" from "changed".
        File.WriteAllText(path, """
            { "schemaVersion": 1, "name": "mine",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [ { "type": "notify", "message": "x", "timeoutMs": 999999999 } ] }
            """);

        var broken = store.Load(directory)[0];
        store.Approve(broken);

        var reloaded = store.Load(directory)[0];
        Assert.Equal(ApprovalStatus.Approved, reloaded.Status);
        Assert.False(reloaded.Validation.IsValid);
        Assert.False(reloaded.IsRunnable);
        Assert.Contains("invalid", reloaded.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void RevokingMakesItInertAgain()
    {
        WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        store.Revoke("mine.json");

        Assert.False(store.Load(directory)[0].IsRunnable);
    }

    // ------------------------------- hashing -------------------------------

    [Fact]
    public void LineEndingChangesDoNotRevokeApproval()
    {
        // Git checkouts and editors rewrite line endings constantly. Treating that as a change
        // would make approvals evaporate for reasons that have nothing to do with the plan.
        var crlf = "{\r\n  \"a\": 1\r\n}\r\n";
        var lf = "{\n  \"a\": 1\n}\n";

        Assert.Equal(AutomationStore.HashOf(crlf), AutomationStore.HashOf(lf));
    }

    [Fact]
    public void MeaningfulChangesDoChangeTheHash()
    {
        Assert.NotEqual(
            AutomationStore.HashOf("{\"a\": 1}"),
            AutomationStore.HashOf("{\"a\": 2}"));
    }

    // ------------------------------- robustness -------------------------------

    [Fact]
    public void AnInvalidAutomationIsReportedRatherThanCrashingTheLoad()
    {
        // One bad file must not stop the agent from loading the others, or a single typo
        // silently disables every automation the user has.
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not json");
        WritePlan("good.json");

        var loaded = Store().Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();

        Assert.Equal(2, loaded.Count);
        Assert.False(loaded[0].Validation.IsValid);
        Assert.True(loaded[1].Validation.IsValid);
    }

    [Fact]
    public void AMissingDirectoryIsEmptyNotAnError()
    {
        Assert.Empty(Store().Load(Path.Combine(directory, "nope")));
    }

    [Fact]
    public void ApprovalsArePersistedPerFileNotGlobally()
    {
        WritePlan("one.json");
        WritePlan("two.json", @"[""CTRL"",""ALT"",""Q""]");
        var store = Store();

        var loaded = store.Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();
        store.Approve(loaded[0]);

        var reloaded = store.Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();
        Assert.True(reloaded[0].IsRunnable);
        Assert.False(reloaded[1].IsRunnable);
    }
}
