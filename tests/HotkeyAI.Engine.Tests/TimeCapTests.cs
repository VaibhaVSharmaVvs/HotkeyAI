using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The wall-clock cap bounds the run, not the gaps between its steps.
/// </summary>
/// <remarks>
/// Security review 2026-08-17, finding M5. <c>CheckLimits</c> evaluated
/// <c>run.Elapsed &gt; limits.MaxDuration</c> before each action and never during one, so a single
/// action ran for its own <c>timeoutMs</c> — bounded by policy at 300 000 ms, two and a half times
/// the documented 120 s cap — and a verification polled for its own <c>withinMs</c> on top of that.
/// The panic key still worked, because cancellation is honoured throughout; what did not work was
/// the engine's own escape hatch, which <c>PLAN.md</c> describes as being there for when the user
/// cannot reach the keyboard.
/// <para>
/// Real time, deliberately, with the limits shrunk to milliseconds: the fix is about the clock the
/// deadlines are derived from, and a fake clock would let a wrong derivation pass. Before this
/// finding the cap had no test of any kind, partly because <c>RunState</c> measured elapsed time
/// with a <c>Stopwatch</c> while every deadline came from the executor's <c>TimeProvider</c> — two
/// clocks that could disagree. They are now one.
/// </para>
/// </remarks>
public sealed class TimeCapTests
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

    private static PlanExecutor Executor(FakeDesktop desktop, ExecutionLimits limits) =>
        new(desktop, new PathGuard(Roots), limits);

    [Fact]
    public async Task ASingleLongActionIsCutShortByTheCap()
    {
        // One action asking for 10 s under a 300 ms cap. Before the fix the action ran the full
        // 10 s: its timeout was its own timeoutMs and the cap was consulted only between steps.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromMilliseconds(300) };

        var start = DateTimeOffset.UtcNow;

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""{ "type": "wait", "id": "a1", "durationMs": 10000 }"""),
            CancellationToken.None);

        var took = DateTimeOffset.UtcNow - start;

        Assert.False(result.Succeeded);
        Assert.InRange(took, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Contains("Time cap reached", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCapIsBlamedRatherThanTheAction()
    {
        // The two events wear the same OperationCanceledException, and reporting the wrong one sends
        // someone tuning a timeoutMs that was never the problem.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromMilliseconds(200) };

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""{ "type": "wait", "id": "a1", "durationMs": 5000 }"""),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries, e => e.ActionType == "wait");

        Assert.Equal(StepOutcome.Aborted, entry.Outcome);
        Assert.Contains("Time cap reached", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Timed out after", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionInsideItsOwnTimeoutIsStillBlamedForItsOwnTimeout()
    {
        // The converse, and the reason the clip is conditional: with budget to spare, a slow action
        // is its own fault and must say so in its own words. Applied unconditionally, every ordinary
        // timeout would start reading as a cap breach and nobody would know which number to change.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromSeconds(30) };

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""
                 { "type": "wait_for_process", "id": "a1",
                   "processName": "nothing-is-running", "timeoutMs": 200 }
                 """),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries, e => e.ActionType == "wait_for_process");

        Assert.Equal(StepOutcome.Failed, entry.Outcome);
        Assert.Contains("Timed out after 200 ms", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Time cap", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APollingActionIsClippedByTheCapRatherThanItsOwnWindow()
    {
        // wait_for_process polls on its own deadline, taken from timeoutMs — whose policy maximum is
        // 300 000 ms, two and a half times the documented cap. The clipped per-action token is what
        // cuts the poll short, so this is a different code path from the plain wait above and worth
        // its own test.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromMilliseconds(300) };

        var start = DateTimeOffset.UtcNow;

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""
                 { "type": "wait_for_process", "id": "a1",
                   "processName": "nothing-is-running", "timeoutMs": 300000 }
                 """),
            CancellationToken.None);

        var took = DateTimeOffset.UtcNow - start;

        Assert.False(result.Succeeded);
        Assert.InRange(took, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var entry = Assert.Single(result.Entries, e => e.ActionType == "wait_for_process");
        Assert.Equal(StepOutcome.Aborted, entry.Outcome);
        Assert.Contains("Time cap reached", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnErrorContinueCannotWalkPastTheCap()
    {
        // onError: continue is the plan author saying "this step is allowed to fail", not "this run
        // is allowed to outlive its cap" — so the cap breach is Aborted, which continue does not
        // absorb. Without that distinction a plan of ten such steps would run ten times the cap.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromMilliseconds(250) };

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""
                 { "type": "wait", "id": "a1", "durationMs": 4000, "onError": "continue" },
                 { "type": "wait", "id": "a2", "durationMs": 4000, "onError": "continue" }
                 """),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        // The second action must never have started.
        Assert.DoesNotContain(result.Entries, e => e.ActionId == "a2");
    }

    [Fact]
    public async Task SlowVerificationIsBoundedByTheCapToo()
    {
        // Polling happens inside a step, so withinMs was a second way past the cap — and its policy
        // maximum is 120 000 ms, exactly the cap, so one failing check could consume the whole run
        // and then a second one could consume it again.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromMilliseconds(300) };

        var start = DateTimeOffset.UtcNow;

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "x",
                   "expect": { "type": "process_running", "processName": "nothing-is-running" },
                   "withinMs": 20000 }
                 """),
            CancellationToken.None);

        var took = DateTimeOffset.UtcNow - start;

        Assert.False(result.Succeeded);
        Assert.InRange(took, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var entry = Assert.Single(result.Entries, e => e.ActionType == "set_clipboard");
        Assert.Equal(Verification.Failed, entry.Verification);
        Assert.Contains("Time cap reached", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryRunIsUnaffected()
    {
        // The whole point is that nothing changes for a plan that behaves, including the wording of
        // a genuine verification miss.
        var limits = new ExecutionLimits { MaxDuration = TimeSpan.FromSeconds(30) };

        var result = await Executor(new FakeDesktop(), limits).RunAsync(
            Plan("""
                 { "type": "set_clipboard", "id": "a1", "text": "hello",
                   "expect": { "type": "clipboard_matches", "contains": "hello" } },
                 { "type": "wait", "id": "a2", "durationMs": 10 }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
    }
}
