using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The live view behind test-run mode.
/// </summary>
/// <remarks>
/// Two things have to hold, and they pull in opposite directions. What a watcher sees must be the
/// same run the transcript describes — a live view that disagrees with the log is worse than no
/// live view, because it is the one the user was watching when they formed their opinion. And
/// watching must be incapable of changing the run, including when the watcher is broken.
/// </remarks>
public sealed class RunObserverTests
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

    private static PlanExecutor Executor(FakeDesktop desktop, ExecutionLimits? limits = null) =>
        new(desktop, new PathGuard(Roots), limits);

    /// <summary>Records everything it is told, in the order it is told.</summary>
    private sealed class Recorder : IRunObserver
    {
        public List<string> Events { get; } = [];

        public List<LogEntry> Finishes { get; } = [];

        public void Starting(string actionType, string? actionId) =>
            Events.Add($"start:{actionType}");

        public void Finished(LogEntry entry)
        {
            Events.Add($"done:{entry.ActionType}:{entry.Outcome}");
            Finishes.Add(entry);
        }
    }

    [Fact]
    public async Task WhatTheWatcherSeesIsWhatTheTranscriptSays()
    {
        // The property the whole feature rests on. If these can diverge, the user watches one run
        // and then repairs a different one, because the repair prompt is built from the transcript.
        var recorder = new Recorder();

        var plan = Plan(
            """
            { "type": "notify", "message": "one" },
            { "type": "notify", "message": "two" },
            { "type": "wait", "durationMs": 1 }
            """);

        var result = await Executor(new FakeDesktop())
            .RunAsync(plan, recorder, CancellationToken.None);

        Assert.Equal(result.Entries, recorder.Finishes);
    }

    [Fact]
    public async Task EachActionIsAnnouncedBeforeItsOutcome()
    {
        // Ordering is the point: an action is logged when it finishes, so a plan waiting ten
        // seconds shows nothing without the announcement. A view that only ever renders finished
        // steps cannot say which step it is stuck on, which is the question being asked.
        var recorder = new Recorder();

        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""{ "type": "wait", "durationMs": 1 }, { "type": "notify", "message": "x" }"""),
            recorder,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["start:wait", "done:wait:Succeeded", "start:notify", "done:notify:Succeeded"],
            recorder.Events);
    }

    [Fact]
    public async Task AFailingStepIsSeenFailing()
    {
        // The run someone is most likely to be watching. A live view that goes quiet on failure
        // sends them to the log file, which is exactly what test-run mode exists to avoid.
        var recorder = new Recorder();

        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""
                 { "type": "focus_window", "selector": { "titleContains": "nothing here" } },
                 { "type": "notify", "message": "never runs" }
                 """),
            recorder,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(recorder.Events, e => e.StartsWith("done:focus_window:Failed", StringComparison.Ordinal));
        Assert.DoesNotContain("start:notify", recorder.Events);
    }

    [Fact]
    public async Task AnAbortIsSeenToo()
    {
        // The panic key writes its own log line rather than an action's, so it reaches the
        // observer by a different path and is worth pinning separately.
        var recorder = new Recorder();
        using var panic = new CancellationTokenSource();
        await panic.CancelAsync();

        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""{ "type": "notify", "message": "x" }"""), recorder, panic.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(result.Entries, recorder.Finishes);
        Assert.Contains(recorder.Finishes, e => e.Outcome == StepOutcome.Aborted);
    }

    [Fact]
    public async Task AWatcherThatThrowsDoesNotBreakTheRun()
    {
        // A watcher is a window, and a window can throw for reasons that have nothing to do with
        // the automation — closed mid-run, dispatcher gone. Closing the viewer must not abort the
        // thing being viewed.
        var result = await Executor(new FakeDesktop()).RunAsync(
            Plan("""{ "type": "notify", "message": "one" }, { "type": "notify", "message": "two" }"""),
            new Saboteur(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Entries.Count);
    }

    private sealed class Saboteur : IRunObserver
    {
        public void Starting(string actionType, string? actionId) =>
            throw new InvalidOperationException("the window went away");

        public void Finished(LogEntry entry) =>
            throw new InvalidOperationException("the window went away");
    }

    [Fact]
    public async Task NoObserverIsTheNormalCase()
    {
        // Every hotkey press runs without one. Worth an assertion rather than an assumption,
        // because the observer is threaded through the same log path the transcript uses.
        var result = await Executor(new FakeDesktop())
            .RunAsync(Plan("""{ "type": "notify", "message": "x" }"""), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Entries);
    }
}
