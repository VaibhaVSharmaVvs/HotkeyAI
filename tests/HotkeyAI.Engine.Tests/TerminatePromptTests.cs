using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The confirmation prompt says how much it is about to close.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding L3. The prompt read "Close chrome?" while the terminate killed
/// every process of that name — routinely a dozen for a browser or an editor — and with
/// <c>force</c> it takes each one's child processes too, via
/// <c>Kill(entireProcessTree: true)</c>. A prompt that understates what it is about to do is worse
/// than no prompt, because the user learns to trust it.
/// <para>
/// The review also noted prompt fatigue: this prompts every run rather than remembering a yes, which
/// is deliberate — the user is approving this kill against whatever is currently open, and a
/// remembered yes was given to a different desktop. What it should not do is prompt when there is
/// nothing to close, which is how the reflex to click through gets trained.
/// </para>
/// </remarks>
public sealed class TerminatePromptTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions) =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(FakeDesktop desktop) =>
        new(desktop, new PathGuard(Roots));

    private const string CloseChrome =
        """{ "type": "terminate_process", "id": "a1", "processName": "chrome" }""";

    private const string ForceCloseChrome =
        """{ "type": "terminate_process", "id": "a1", "processName": "chrome", "force": true }""";

    [Fact]
    public async Task ThePromptSaysHowManyWillClose()
    {
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" } };
        desktop.ProcessCounts["chrome"] = 12;

        await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.Contains("all 12 chrome processes", desktop.ConfirmQuestion!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneProcessIsWordedAsOne()
    {
        // "all 1 chrome processes" is the kind of sentence that makes a prompt look automated and
        // therefore ignorable.
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" } };

        await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.Contains("1 chrome process?", desktop.ConfirmQuestion!, StringComparison.Ordinal);
        Assert.DoesNotContain("all 1", desktop.ConfirmQuestion!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceSaysWhatForceMeans()
    {
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" } };
        desktop.ProcessCounts["chrome"] = 3;

        await Executor(desktop).RunAsync(Plan(ForceCloseChrome), CancellationToken.None);

        var question = desktop.ConfirmQuestion!;
        Assert.Contains("without saving", question, StringComparison.Ordinal);

        // The tree is the part nobody expects, and it is the reason force is not just "harder".
        Assert.Contains("child processes", question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingRunningMeansNoPromptAtAll()
    {
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.Null(desktop.ConfirmQuestion);
        Assert.True(result.Succeeded, result.ToTranscript());
        Assert.Contains(
            result.Entries,
            e => e.Detail.Contains("No chrome process was running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingRunningMeansNothingIsKilledEither()
    {
        var desktop = new FakeDesktop();

        await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.DoesNotContain(
            desktop.Effects, e => e.StartsWith("terminate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLogSaysHowManyActuallyClosed()
    {
        // "Terminated chrome." said nothing about how much happened, and a process can exit or refuse
        // between the count and the kill.
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" } };
        desktop.ProcessCounts["chrome"] = 4;

        var result = await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.Contains(
            result.Entries,
            e => e.Detail.Contains("Closed 4 chrome processes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecliningStillStopsTheRun()
    {
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" }, ConfirmAnswer = false };

        var result = await Executor(desktop).RunAsync(Plan(CloseChrome), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            desktop.Effects, e => e.StartsWith("terminate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePromptFiresOnEveryRunRatherThanBeingRemembered()
    {
        // Deliberately stricter than the control originally read. The user is approving this kill
        // against whatever is open now; a remembered yes was given to a different desktop.
        var desktop = new FakeDesktop { RunningProcesses = { "chrome" } };
        var asked = 0;
        desktop.OnConfirm = _ => asked++;

        var plan = Plan(CloseChrome);

        await Executor(desktop).RunAsync(plan, CancellationToken.None);
        desktop.RunningProcesses.Add("chrome");
        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Equal(2, asked);
    }
}
