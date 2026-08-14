using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Tests;

/// <summary>Execution semantics: order, branching, variables, and honest reporting.</summary>
public sealed class ExecutionTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Projects"];

    private static Automation Plan(string actions, string variables = "") =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1, "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{{variables}}], "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(FakeDesktop desktop) =>
        new(desktop, new PathGuard(Roots));

    // ------------------------------- verification -------------------------------

    [Fact]
    public async Task AnActionWithNoPostconditionIsReportedAsUnverifiedNotAsSuccess()
    {
        // "It ran" and "it worked" are different claims. Conflating them is what makes an
        // automation feel unreliable: it reports success while nothing happened.
        var desktop = new FakeDesktop();
        var plan = Plan(@"{ ""type"": ""notify"", ""message"": ""hello"" }");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.UnverifiedCount);
        Assert.Contains("could not be verified", result.ToTranscript(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedPostconditionFailsTheActionEvenThoughItRan()
    {
        var desktop = new FakeDesktop { InstalledApps = { ["vscode"] = @"C:\Code.exe" } };

        var plan = Plan("""
            { "type": "launch_process", "app": "vscode",
              "expect": { "type": "window_exists",
                          "selector": { "processName": "Code" }, "withinMs": 100 } }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Postcondition not met", result.FailureReason, StringComparison.Ordinal);

        // The launch itself did happen — the failure is that it could not be confirmed.
        Assert.Contains(desktop.Effects, e => e.StartsWith("launch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMetPostconditionIsReportedAsVerified()
    {
        var desktop = new FakeDesktop
        {
            InstalledApps = { ["vscode"] = @"C:\Code.exe" },
            OpenWindows = { new WindowRef(1, "Code", "project — VS Code", false) },
        };

        var plan = Plan("""
            { "type": "launch_process", "app": "vscode",
              "expect": { "type": "window_exists",
                          "selector": { "processName": "Code" }, "withinMs": 500 } }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.UnverifiedCount);
        Assert.Contains("(verified)", result.ToTranscript(), StringComparison.Ordinal);
    }

    // ------------------------------- error policy -------------------------------

    [Fact]
    public async Task FailureStopsTheRunByDefault()
    {
        var desktop = new FakeDesktop();

        var plan = Plan("""
            { "type": "launch_process", "app": "not-installed" },
            { "type": "notify", "message": "should not run" }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("notify:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnErrorContinueCarriesOn()
    {
        var desktop = new FakeDesktop();

        var plan = Plan("""
            { "type": "launch_process", "app": "not-installed", "onError": "continue" },
            { "type": "notify", "message": "still runs" }
            """);

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(desktop.Effects, e => e.StartsWith("notify:", StringComparison.Ordinal));
    }

    // ------------------------------- control flow -------------------------------

    [Fact]
    public async Task IfRunsTheBranchThatMatches()
    {
        var desktop = new FakeDesktop { RunningProcesses = { "Code" } };

        var plan = Plan("""
            { "type": "if",
              "condition": { "type": "process_running", "processName": "Code" },
              "then": [ { "type": "notify", "message": "then-branch" } ],
              "else": [ { "type": "notify", "message": "else-branch" } ] }
            """);

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains("notify:Info:then-branch", desktop.Effects, StringComparer.Ordinal);
        Assert.DoesNotContain("notify:Info:else-branch", desktop.Effects, StringComparer.Ordinal);
    }

    [Fact]
    public async Task NegatedPredicatesInvert()
    {
        var desktop = new FakeDesktop();

        var plan = Plan("""
            { "type": "if",
              "condition": { "type": "process_running", "processName": "Code", "negate": true },
              "then": [ { "type": "notify", "message": "not-running" } ] }
            """);

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains("notify:Info:not-running", desktop.Effects, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ForEachIsCappedByMaxIterations()
    {
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = ["a", "b", "c", "d", "e"] },
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "foreach", "source": "dirs", "itemVariable": "one", "maxIterations": 2,
              "body": [ { "type": "notify", "message": "${one}" } ] }
            """,
            """{ "name": "dirs", "type": "pathList" }, { "name": "one", "type": "path" }""");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Equal(2, desktop.Effects.Count(e => e.StartsWith("notify:", StringComparison.Ordinal)));
        Assert.Contains("3 item(s) skipped", result.ToTranscript(), StringComparison.Ordinal);
    }

    // ------------------------------- variables -------------------------------

    [Fact]
    public async Task VariablesInterpolateIntoFields()
    {
        var desktop = new FakeDesktop { InputAnswer = "world" };

        var plan = Plan(
            """
            { "type": "show_input", "prompt": "who?", "into": "who" },
            { "type": "notify", "message": "hello ${who}" }
            """,
            @"{ ""name"": ""who"", ""type"": ""text"" }");

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains("notify:Info:hello world", desktop.Effects, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PathPropertiesResolveWithWindowsSemantics()
    {
        // Runs on Linux in CI, so this also pins that path properties do not follow host rules.
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = [@"C:\Users\test\Projects\scout-os"] },
            PickerChoice = @"C:\Users\test\Projects\scout-os",
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "show_picker", "source": "dirs", "into": "chosen" },
            { "type": "notify", "message": "${chosen.name} in ${chosen.parent}" }
            """,
            """{ "name": "dirs", "type": "pathList" }, { "name": "chosen", "type": "path" }""");

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains(
            @"notify:Info:scout-os in C:\Users\test\Projects",
            desktop.Effects,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ForEachItemGoesOutOfScopeAfterTheLoop()
    {
        // The policy layer rejects reading it afterwards; the engine must not leave it set, or
        // a plan that slipped past validation would silently read a stale value.
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = ["a"] },
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "foreach", "source": "dirs", "itemVariable": "one",
              "body": [ { "type": "notify", "message": "in:${one}" } ] },
            { "type": "notify", "message": "after:${one}" }
            """,
            """{ "name": "dirs", "type": "pathList" }, { "name": "one", "type": "path" }""");

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains("notify:Info:in:a", desktop.Effects, StringComparer.Ordinal);
        Assert.Contains("notify:Info:after:", desktop.Effects, StringComparer.Ordinal);
    }

    // ------------------------------- the log -------------------------------

    [Fact]
    public async Task TheTranscriptReadsLikeTheConceptsExecutionLog()
    {
        var desktop = new FakeDesktop
        {
            InstalledApps = { ["vscode"] = @"C:\Code.exe" },
            OpenWindows = { new WindowRef(1, "Code", "VS Code", false) },
            Directories = { [@"C:\Users\test\Projects"] = [@"C:\Users\test\Projects\scout-os"] },
            PickerChoice = @"C:\Users\test\Projects\scout-os",
        };

        var plan = Plan(
            """
            { "id": "s1", "type": "list_directories",
              "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "id": "s2", "type": "show_picker", "source": "dirs", "into": "chosen" },
            { "id": "s3", "type": "launch_process", "app": "vscode", "argv": ["${chosen}"],
              "expect": { "type": "window_exists",
                          "selector": { "processName": "Code" }, "withinMs": 500 } }
            """,
            """{ "name": "dirs", "type": "pathList" }, { "name": "chosen", "type": "path" }""");

        var result = await Executor(desktop).RunAsync(plan, CancellationToken.None);
        var transcript = result.ToTranscript();

        Assert.True(result.Succeeded);
        Assert.Contains("[s1]", transcript, StringComparison.Ordinal);
        Assert.Contains("[s3]", transcript, StringComparison.Ordinal);
        Assert.Contains("(verified)", transcript, StringComparison.Ordinal);
        Assert.Contains("(unverified)", transcript, StringComparison.Ordinal);

        // argv carries the selection through as one argument, never as a command line.
        Assert.Contains(
            @"launch:C:\Code.exe:C:\Users\test\Projects\scout-os",
            desktop.Effects,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task TheLogNamesActionsAsThePlanDoes()
    {
        // Regression from the first end-to-end run: the log printed CLR type names
        // ("ListDirectoriesAction") because [DslType] is declared on the base class, so asking a
        // derived record for its own attribute found nothing. The log is read by the user and
        // pasted into repair prompts, so it has to use the words that appear in their plan.
        var desktop = new FakeDesktop
        {
            Directories = { [@"C:\Users\test\Projects"] = ["a"] },
        };

        var plan = Plan(
            """
            { "type": "list_directories", "path": "C:\\Users\\test\\Projects", "into": "dirs" },
            { "type": "if", "condition": { "type": "variable_empty", "variable": "dirs" },
              "then": [ { "type": "notify", "message": "empty" } ] }
            """,
            """{ "name": "dirs", "type": "pathList" }""");

        var transcript = (await Executor(desktop).RunAsync(plan, CancellationToken.None))
            .ToTranscript();

        Assert.Contains("list_directories", transcript, StringComparison.Ordinal);
        Assert.Contains("if:", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("Action:", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgumentsAreNeverConcatenatedIntoACommandLine()
    {
        // There is no shell primitive and argv is a list, so a value containing shell
        // metacharacters is just an argument. This pins that the engine does not undo it.
        var desktop = new FakeDesktop
        {
            InstalledApps = { ["vscode"] = @"C:\Code.exe" },
            InputAnswer = "; rm -rf / && echo pwned",
        };

        var plan = Plan(
            """
            { "type": "show_input", "prompt": "name?", "into": "name" },
            { "type": "launch_process", "app": "vscode", "argv": ["--folder", "${name}"] }
            """,
            @"{ ""name"": ""name"", ""type"": ""text"" }");

        await Executor(desktop).RunAsync(plan, CancellationToken.None);

        Assert.Contains(
            @"launch:C:\Code.exe:--folder|; rm -rf / && echo pwned",
            desktop.Effects,
            StringComparer.Ordinal);
    }
}
