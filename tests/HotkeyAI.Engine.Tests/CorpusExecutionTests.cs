using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;
using HotkeyAI.Tests;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// Every plan in the regression corpus, executed against the fake desktop.
/// </summary>
/// <remarks>
/// The Core-side corpus tests prove the plans still parse, validate and render. This proves the
/// executor can still <i>run</i> them, which is a different claim and the one that catches the
/// mistake the executor is most prone to: a primitive added to the schema and the records, with
/// no case in <c>DispatchAsync</c>. That fallback returns a failure naming the omission rather
/// than doing nothing silently, and this is what reads it.
/// <para>
/// The corpus is not asserted to <i>succeed</i>. Several plans abort on purpose — that is what
/// <c>abort</c> and <c>onError: abort</c> are for — so success would be the wrong bar. What must
/// hold is that nothing throws, and that no action goes undispatched.
/// </para>
/// </remarks>
public sealed class CorpusExecutionTests
{
    public static TheoryData<string> Plans()
    {
        var data = new TheoryData<string>();

        foreach (var file in RepoPaths.CorpusFiles())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public async Task EveryCorpusPlanExecutes(string fileName)
    {
        var plan = JsonSerializer.Deserialize<Automation>(
            File.ReadAllText(Path.Combine(RepoPaths.CorpusPlans, fileName)),
            DslJson.Options)!;

        var desktop = Permissive();
        var executor = new PlanExecutor(desktop, new PathGuard([@"C:\Corpus"]));

        var result = await executor.RunAsync(plan, CancellationToken.None);
        var transcript = result.ToTranscript();

        // The exact words the executor's fallback uses when a primitive has no case.
        Assert.DoesNotContain("No handler for", transcript, StringComparison.Ordinal);

        // A plan that produced no steps at all means dispatch stopped before it started, which a
        // "did not throw" assertion on its own would happily accept.
        Assert.NotEmpty(result.Entries);
    }

    /// <summary>
    /// A desktop where everything a plan might reach for happens to exist.
    /// </summary>
    /// <remarks>
    /// Deliberately permissive. The question here is whether the engine can dispatch and run every
    /// primitive, not whether it handles absence — the safety-control and execution tests cover
    /// that, with much sharper cases than a corpus sweep could.
    /// </remarks>
    private static FakeDesktop Permissive()
    {
        var desktop = new FakeDesktop
        {
            ClipboardText = "corpus clipboard",
            ForegroundProcess = "Notepad",
            PickerChoice = @"C:\Corpus\repos\alpha",
            InputAnswer = @"C:\Corpus\src",
            ConfirmAnswer = true,
        };

        foreach (var app in HotkeyAI.Core.Policy.AppRegistry.KnownApps)
        {
            desktop.InstalledApps[app] = $@"C:\Corpus\bin\{app}.exe";
        }

        // A few applications run under a name that is not their logical one, and corpus plans wait
        // on those names after launching them. Spelling them out keeps the plans realistic rather
        // than shaped around whatever a fake happens to model.
        desktop.InstalledApps["vscode"] = @"C:\Corpus\bin\Code.exe";
        desktop.InstalledApps["outlook"] = @"C:\Corpus\bin\olk.exe";
        desktop.InstalledApps["teams"] = @"C:\Corpus\bin\ms-teams.exe";

        foreach (var process in new[]
                 { "Notepad", "Code", "chrome", "olk", "ms-teams", "slack", "a", "polite", "stubborn" })
        {
            desktop.RunningProcesses.Add(process);
            desktop.OpenWindows.Add(new WindowRef(desktop.OpenWindows.Count + 1, process, $"{process} window", false));
        }

        foreach (var path in new[]
                 {
                     @"C:\Corpus", @"C:\Corpus\src", @"C:\Corpus\repos", @"C:\Corpus\notes",
                     @"C:\Corpus\Desktop", @"C:\Corpus\Downloads", @"C:\Corpus\tools",
                     @"C:\Corpus\Pictures\Screenshots", @"C:\Corpus\repos\alpha",
                 })
        {
            desktop.ExistingPaths.Add(path);
        }

        desktop.Directories[@"C:\Corpus\repos"] = [@"C:\Corpus\repos\alpha", @"C:\Corpus\repos\beta"];
        desktop.Directories[@"C:\Corpus\src"] = [@"C:\Corpus\src\one.cs", @"C:\Corpus\src\two.json"];
        desktop.Directories[@"C:\Corpus\Desktop"] = [@"C:\Corpus\Desktop\stale.tmp"];

        return desktop;
    }
}
