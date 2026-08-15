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

    private readonly FakeDisabled disabled = new();

    private readonly FakeHealth health = new();

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

    private sealed class FakeDisabled : IDisabledStorage
    {
        private HashSet<string> off = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> Read() => off;

        public void Write(IReadOnlySet<string> disabled) =>
            off = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeHealth : IHealthStorage
    {
        private Dictionary<string, HealthRecord> records = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, HealthRecord> Read() => records;

        public void Write(IReadOnlyDictionary<string, HealthRecord> health) =>
            records = new Dictionary<string, HealthRecord>(health, StringComparer.OrdinalIgnoreCase);
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

    private AutomationStore Store() => new(approvals, Policy, disabled, health);

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

    // ------------------------------- enable and disable -------------------------------

    [Fact]
    public void AutomationsAreEnabledUntilTheUserSaysOtherwise()
    {
        // Enabled by default matters: a plan the user drops in and approves should just work.
        // The alternative is a new automation that silently does nothing, with the reason
        // buried in a settings file they have never opened.
        WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        var automation = store.Load(directory)[0];

        Assert.True(automation.IsEnabled);
        Assert.True(automation.IsRunnable);
    }

    [Fact]
    public void DisablingStopsItRunning()
    {
        WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        store.SetEnabled("mine.json", enabled: false);

        var automation = store.Load(directory)[0];
        Assert.False(automation.IsEnabled);
        Assert.False(automation.IsRunnable);
        Assert.Equal("turned off", automation.Blocker);
    }

    [Fact]
    public void DisablingDoesNotRevokeApproval()
    {
        // The whole point of keeping these separate. Re-enabling must not send the user back
        // through an approval prompt for a plan they have already read and never changed --
        // that would make the prompt something to click past rather than something to read.
        WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        store.SetEnabled("mine.json", enabled: false);
        store.SetEnabled("mine.json", enabled: true);

        var automation = store.Load(directory)[0];
        Assert.Equal(ApprovalStatus.Approved, automation.Status);
        Assert.True(automation.IsRunnable);
        Assert.Null(automation.Blocker);
    }

    [Fact]
    public void DisablingAnUnapprovedAutomationStillReportsBeingOff()
    {
        // Both blockers apply; the user's own switch is the one reported, because it is the one
        // they can undo and the one that explains what they just did.
        WritePlan("mine.json");
        var store = Store();

        store.SetEnabled("mine.json", enabled: false);

        Assert.Equal("turned off", store.Load(directory)[0].Blocker);
    }

    [Fact]
    public void TogglingIsPerAutomation()
    {
        WritePlan("one.json");
        WritePlan("two.json", @"[""CTRL"",""ALT"",""Q""]");
        var store = Store();

        foreach (var automation in store.Load(directory))
        {
            store.Approve(automation);
        }

        store.SetEnabled("one.json", enabled: false);

        var loaded = store.Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();
        Assert.False(loaded[0].IsRunnable);
        Assert.True(loaded[1].IsRunnable);
    }

    [Fact]
    public void AStoreWithNoDisabledStorageTreatsEverythingAsEnabled()
    {
        // The CLI constructs a store without one. It must not accidentally report every
        // automation as switched off.
        WritePlan("mine.json");
        var store = new AutomationStore(approvals, Policy);
        store.Approve(store.Load(directory)[0]);

        Assert.True(store.Load(directory)[0].IsEnabled);
    }

    // ------------------------------- does it actually work -------------------------------

    [Fact]
    public void AnAutomationStartsUntested()
    {
        // The engine can say an action ran. Only a person can say the automation did what they
        // meant, and until they have, claiming otherwise would be the product lying about the one
        // thing it cannot know.
        WritePlan("mine.json");

        Assert.Equal(AutomationHealth.Untested, Store().Load(directory)[0].Health);
    }

    [Fact]
    public void TheUserCanSayItWorks()
    {
        WritePlan("mine.json");
        var store = Store();

        store.SetHealth(store.Load(directory)[0], AutomationHealth.Works);

        Assert.Equal(AutomationHealth.Works, store.Load(directory)[0].Health);
    }

    [Fact]
    public void TheUserCanSayItDoesNotWorkAndWhy()
    {
        WritePlan("mine.json");
        var store = Store();

        store.SetHealth(
            store.Load(directory)[0], AutomationHealth.NotWorking, "opens the wrong folder");

        var automation = store.Load(directory)[0];
        Assert.Equal(AutomationHealth.NotWorking, automation.Health);
        Assert.Equal("opens the wrong folder", automation.HealthNote);
    }

    [Fact]
    public void EditingThePlanDiscardsTheVerdict()
    {
        // The whole reason the verdict is recorded against a content hash. "I tested this" is a
        // statement about a specific plan, and carrying it across an edit would let a changed
        // automation inherit confidence nobody ever gave it.
        var path = WritePlan("mine.json");
        var store = Store();
        store.SetHealth(store.Load(directory)[0], AutomationHealth.Works);

        File.WriteAllText(path, File.ReadAllText(path).Replace("hello", "goodbye", StringComparison.Ordinal));

        Assert.Equal(AutomationHealth.Untested, store.Load(directory)[0].Health);
    }

    [Fact]
    public void RestoringThePlanRestoresTheVerdict()
    {
        // The mirror of the rule above, and the same behaviour approval has: the verdict is about
        // content, so identical content is the thing that was tested.
        var path = WritePlan("mine.json");
        var original = File.ReadAllText(path);
        var store = Store();
        store.SetHealth(store.Load(directory)[0], AutomationHealth.Works);

        File.WriteAllText(path, original.Replace("hello", "goodbye", StringComparison.Ordinal));
        File.WriteAllText(path, original);

        Assert.Equal(AutomationHealth.Works, store.Load(directory)[0].Health);
    }

    [Fact]
    public void SayingItIsBrokenDoesNotStopItRunning()
    {
        // Load-bearing. You have to run an automation to find out whether it still misbehaves,
        // and this must never become a fourth reason a hotkey quietly stops firing.
        WritePlan("mine.json");
        var store = Store();
        store.Approve(store.Load(directory)[0]);

        store.SetHealth(store.Load(directory)[0], AutomationHealth.NotWorking, "wrong window");

        var automation = store.Load(directory)[0];
        Assert.True(automation.IsRunnable);
        Assert.Null(automation.Blocker);
    }

    [Fact]
    public void AVerdictCanBeWithdrawn()
    {
        WritePlan("mine.json");
        var store = Store();
        store.SetHealth(store.Load(directory)[0], AutomationHealth.Works);

        store.SetHealth(store.Load(directory)[0], AutomationHealth.Untested);

        Assert.Equal(AutomationHealth.Untested, store.Load(directory)[0].Health);
    }

    [Fact]
    public void AVerdictIsPerAutomation()
    {
        WritePlan("one.json");
        WritePlan("two.json", @"[""CTRL"",""ALT"",""Q""]");
        var store = Store();
        var loaded = store.Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();

        store.SetHealth(loaded[0], AutomationHealth.Works);

        var after = store.Load(directory).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();
        Assert.Equal(AutomationHealth.Works, after[0].Health);
        Assert.Equal(AutomationHealth.Untested, after[1].Health);
    }

    [Fact]
    public void AStoreWithNoHealthStorageReportsEverythingUntested()
    {
        WritePlan("mine.json");
        var store = new AutomationStore(approvals, Policy);

        Assert.Equal(AutomationHealth.Untested, store.Load(directory)[0].Health);
    }
}
