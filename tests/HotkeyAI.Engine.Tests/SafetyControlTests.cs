using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The safety controls. These are the reason the engine is testable without a desktop.
/// </summary>
/// <remarks>
/// Each control exists to stop a specific real failure, so each test names it. They assert on
/// what the engine <i>attempted</i>, not just on the error returned: a control that reports a
/// refusal while still performing the action has failed, and only the effect log shows that.
/// </remarks>
public sealed class SafetyControlTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions, string variables = "") =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{{variables}}],
              "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(
        FakeDesktop desktop, ExecutionLimits? limits = null, TimeProvider? clock = null) =>
        new(desktop, new PathGuard(Roots), limits, clock);

    // ------------------------- control 1: caps and panic -------------------------

    [Fact]
    public async Task StepCapStopsARunawayPlan()
    {
        // Without a cap, a plan that loops over a long list holds the desktop until it finishes.
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = [.. Enumerable.Range(0, 50).Select(i => $"p{i}")] },
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "foreach", "source": "dirs", "itemVariable": "one", "maxIterations": 100,
              "body": [ { "type": "notify", "message": "${one}" } ] }
            """,
            """
            { "name": "dirs", "type": "pathList" }, { "name": "one", "type": "path" }
            """);

        var result = await Executor(desktop, new ExecutionLimits { MaxSteps = 10 })
            .RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Step cap reached", result.FailureReason, StringComparison.Ordinal);
        Assert.True(
            desktop.Effects.Count(e => e.StartsWith("notify:", StringComparison.Ordinal)) < 50,
            "The cap should have stopped the loop well before it finished.");
    }

    [Fact]
    public async Task PanicKeyAbortsTheRun()
    {
        using var panic = new CancellationTokenSource();

        var desktop = new FakeDesktop();
        desktop.OnEffect = _ =>
        {
            // The user hits the panic key partway through.
            panic.Cancel();
            return Task.CompletedTask;
        };

        var plan = Plan(
            """
            { "type": "notify", "message": "one" },
            { "type": "notify", "message": "two" },
            { "type": "notify", "message": "three" }
            """);

        var result = await Executor(desktop).RunAsync(plan, panic.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("panic key", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, desktop.Effects.Count(e => e.StartsWith("notify:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PanicKeyReleasesHeldModifiers()
    {
        // The control that matters most on abort. A plan stopped between key-down and key-up
        // leaves Ctrl or Alt stuck system-wide, which is indistinguishable from a hung machine
        // and worse than the automation that was interrupted.
        using var panic = new CancellationTokenSource();
        var desktop = new FakeDesktop();
        desktop.OnEffect = _ => { panic.Cancel(); return Task.CompletedTask; };

        var plan = Plan("""
            { "type": "notify", "message": "one" },
            { "type": "send_keys", "keys": ["CTRL","S"] }
            """);

        await Executor(desktop).RunAsync(plan, panic.Token);

        Assert.Equal(1, desktop.ModifiersReleasedCount);
    }

    // ------------------------- control 2: paths at run time -------------------------

    [Fact]
    public async Task AnInterpolatedPathEscapingTheRootIsRefusedAtRunTime()
    {
        // The gap the validator cannot close. The plan is statically valid: the path is built
        // from a variable, so its value is unknown until the picker returns. Only the engine
        // can catch this, and it must.
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = [@"C:\Users\test\Projects\..\..\..\Windows"] },
            PickerChoice = @"C:\Users\test\Projects\..\..\..\Windows\system32",
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "show_picker", "source": "dirs", "into": "chosen" },
            { "type": "open_path", "path": "${chosen}" }
            """,
            """
            { "name": "dirs", "type": "pathList" }, { "name": "chosen", "type": "path" }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Refused to open", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            desktop.Effects,
            e => e.StartsWith("open:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnresolvedVariableInAPathIsRefusedRatherThanUsedLiterally()
    {
        var desktop = new FakeDesktop();

        var plan = Plan(
            @"{ ""type"": ""open_path"", ""path"": ""${never}\\file.txt"" }",
            @"{ ""name"": ""never"", ""type"": ""path"" }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("open:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostconditionCannotProbeOutsideTheAllowedRoots()
    {
        // A path_exists postcondition would otherwise be a way to ask "does C:\Windows\… exist"
        // from a plan that is forbidden to touch it.
        var desktop = new FakeDesktop { ExistingPaths = { @"C:\Windows\system32\config" } };

        var plan = Plan("""
            { "type": "notify", "message": "x",
              "expect": { "type": "path_exists", "path": "C:\\Windows\\system32\\config",
                          "withinMs": 200 } }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    // ------------------------- control 3: sensitive windows -------------------------

    [Theory]
    [InlineData(InputHazard.ConsentPrompt, "security prompt")]
    [InlineData(InputHazard.CredentialPrompt, "password or credential")]
    public async Task InputIsRefusedWhenTheForegroundIsSensitive(InputHazard hazard, string fragment)
    {
        var desktop = new FakeDesktop { Hazard = hazard };
        var plan = Plan(@"{ ""type"": ""type_text"", ""text"": ""hunter2"" }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(fragment, result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InputToAnElevatedWindowFailsLoudlyInsteadOfSilently()
    {
        // Windows discards synthetic input aimed at a higher-integrity window and reports
        // nothing. Without this the automation "succeeds" while doing absolutely nothing,
        // which is the single most confusing failure this app can produce.
        var desktop = new FakeDesktop { Hazard = InputHazard.ElevatedWindow };
        var plan = Plan(@"{ ""type"": ""send_keys"", ""keys"": [""CTRL"",""S""] }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("elevated", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silently", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("keys:", StringComparison.Ordinal));
    }

    // ------------------------- control 5: destructive actions -------------------------

    [Fact]
    public async Task TerminatingAProcessAsksFirstAndHonoursNo()
    {
        var desktop = new FakeDesktop { ConfirmAnswer = false, RunningProcesses = { "slack" } };
        var plan = Plan(@"{ ""type"": ""terminate_process"", ""processName"": ""slack"" }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(desktop.Effects, e => e.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            desktop.Effects, e => e.StartsWith("terminate:", StringComparison.Ordinal));
    }

    // ------------------------- control 6: secrets in logs -------------------------

    [Fact]
    public async Task TypedTextAndClipboardContentsStayOutOfTheLog()
    {
        // Logs get pasted into repair prompts. A plan may legitimately type or copy something
        // the user would not want to hand over.
        var desktop = new FakeDesktop { ClipboardText = "correct-horse-battery-staple" };

        var plan = Plan(
            """
            { "type": "get_clipboard", "into": "secret" },
            { "type": "type_text", "text": "hunter2" }
            """,
            @"{ ""name"": ""secret"", ""type"": ""text"" }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);
        var transcript = result.ToTranscript();

        Assert.DoesNotContain("hunter2", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse", transcript, StringComparison.Ordinal);
    }
}
